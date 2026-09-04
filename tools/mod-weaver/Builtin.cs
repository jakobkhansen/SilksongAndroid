using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ModWeaver;

/// <summary>
/// The port's own weaves: patches that are part of the build rather than part
/// of somebody's mod folder.
///
/// ── why this exists at all ─────────────────────────────────────────────────
///
/// Silksong clamps the shape it will render into. ForceCameraAspect:
///
///     AutoScaleViewportShared:
///         MinMaxFloat(1.6f, 2.3916667f).GetClampedBetween(w / (float)h)
///
/// Anything narrower than 1.6 : 1 is letterboxed by the game itself, on every
/// platform. That is Team Cherry's framing decision and on a 16:9 monitor it
/// never comes up -- but Android is not 16:9. A Galaxy Z Fold's inner screen is
/// 2160x1856, which is 1.164 : 1, and a 4:3 handheld is 1.333 : 1. Both sit
/// below the floor, so both keep black bars no matter how perfectly the window
/// and the render target are matched. Measured on a Fold 6: 253 px top and
/// bottom, 13.6%, exactly (1 - 1.164/1.6) / 2.
///
/// ── why it is woven rather than patched at runtime ─────────────────────────
///
/// The constant is baked into IL, and by the time the game runs there is no IL
/// left -- il2cpp has turned it into C++ and then into a .so. The only moment
/// it can be changed is the one this tool already owns: after the depot's
/// assemblies are staged and before il2cpp is handed them.
///
/// ── why it is still a runtime toggle ───────────────────────────────────────
///
/// Because a constant is not the only thing IL can hold. Rather than writing a
/// different number in, the load is replaced with a CALL to a gate injected
/// beside it, and the gate answers from a field the game's own C# can set at
/// startup. So the weave is unconditional and free, and turning the feature on
/// and off costs a relaunch rather than the twenty minutes an il2cpp
/// conversion costs. That is the same bargain Mods.gates strikes for plugins,
/// and for the same reason.
///
/// ── how it fails ───────────────────────────────────────────────────────────
///
/// Safely, in both directions. An unset gate reads zero, and the gate treats
/// anything below 1.0 as "no answer" and returns the game's own 1.6 -- so a
/// build whose C# side never runs behaves exactly like an unwoven one. And if
/// the anchor below is ever not found, because the game was updated and the
/// method moved or the constants changed, nothing is written and the build
/// continues on stock behaviour. A missing frill must never cost somebody a
/// game that boots.
/// </summary>
internal static class Builtin
{
    /// The type injected into Assembly-CSharp. Deliberately in the global
    /// namespace, as the game's own classes are, and deliberately named so
    /// that it is obvious in a stack trace that it is not Team Cherry's.
    public const string GateType = "SilksongAspectGate";

    /// The field the game's C# writes, and the method the woven call reads it
    /// through. Named on the patch side too -- see AspectGate.cs.
    public const string FloorField = "Floor";
    public const string FloorMethod = "GetFloor";
    public const string CeilingField = "Ceiling";
    public const string CeilingMethod = "GetCeiling";

    /// What the game asks for, and what it means.
    ///
    /// The pair is the anchor rather than either number alone: 1.6f appears
    /// nine times in Assembly-CSharp and only once immediately before
    /// 2.3916667f. CameraAspectScaler builds a MinMaxFloat too, but from
    /// 1.7777778f, so matching the pair cannot hit it by accident -- and
    /// because BOTH ends are replaced from the one anchor, the ceiling is
    /// found positionally rather than by searching for its value, which is
    /// what keeps CameraAspectScaler's copy of it out of this.
    const float StockFloor = 1.6f;
    const float StockCeiling = 2.3916667f;

    const string TargetType = "ForceCameraAspect";
    const string TargetMethod = "AutoScaleViewportShared";

    /// <summary>
    /// Applies every built-in weave to the staged assembly set.
    ///
    /// Returns what happened, one line per weave, for the build log. Never
    /// throws: see the class comment.
    /// </summary>
    public static List<string> Apply(string assemblies)
    {
        var notes = new List<string>();
        var path = Path.Combine(assemblies, "Assembly-CSharp.dll");
        if (!File.Exists(path))
        {
            notes.Add("aspect floor: no Assembly-CSharp.dll in the staged set; skipped");
            return notes;
        }

        var resolver = new StagedResolver(assemblies);
        AssemblyDefinition assembly;
        try
        {
            assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters
            {
                // In memory, so the file can be written back to the same path.
                InMemory = true,
                AssemblyResolver = resolver,
            });
        }
        catch (Exception e)
        {
            notes.Add($"aspect floor: cannot read Assembly-CSharp.dll ({e.GetType().Name}); skipped");
            return notes;
        }

        bool changed;
        try
        {
            changed = WeaveAspectFloor(assembly, notes);
        }
        catch (Exception e)
        {
            notes.Add($"aspect floor: not applied ({e.GetType().Name}: {e.Message})");
            return notes;
        }

        if (!changed) return notes;

        try
        {
            assembly.Write(path);
            notes.Add("aspect floor: Assembly-CSharp.dll rewritten");
        }
        catch (Exception e)
        {
            notes.Add($"aspect floor: could not write Assembly-CSharp.dll ({e.GetType().Name}: {e.Message})");
        }
        return notes;
    }

    /// <summary>
    /// Replaces the floor constant with a call to an injected gate.
    ///
    /// One instruction changes. Everything else about the method -- the
    /// ceiling, the clamp, the viewport arithmetic, the height multiplier --
    /// is left exactly as Team Cherry wrote it, because the goal is to let the
    /// game's own code run on a number it was never given rather than to
    /// reimplement any part of it.
    /// </summary>
    static bool WeaveAspectFloor(AssemblyDefinition assembly, List<string> notes)
    {
        var module = assembly.MainModule;

        var type = module.GetType(TargetType);
        if (type is null)
        {
            notes.Add($"aspect floor: no {TargetType} in this build; skipped");
            return false;
        }

        var method = type.Methods.FirstOrDefault(m => m.Name == TargetMethod && m.HasBody);
        if (method is null)
        {
            notes.Add($"aspect floor: {TargetType}.{TargetMethod} not found; skipped");
            return false;
        }

        // The anchor: ldc.r4 1.6 immediately followed by ldc.r4 2.3916667.
        var il = method.Body.Instructions;
        Instruction? floor = null;
        for (var i = 0; i + 1 < il.Count; i++)
        {
            if (!IsLoad(il[i], StockFloor)) continue;
            if (!IsLoad(il[i + 1], StockCeiling)) continue;
            if (floor is not null)
            {
                notes.Add($"aspect floor: {TargetType}.{TargetMethod} has more than one "
                          + "candidate pair; skipped");
                return false;
            }
            floor = il[i];
        }

        if (floor is null)
        {
            // Already woven is not a failure: it is what a second run looks
            // like, and saying so is more useful than saying nothing.
            var already = module.GetType(GateType) is not null;
            notes.Add(already
                ? "aspect floor: already woven; nothing to do"
                : $"aspect floor: the {StockFloor}/{StockCeiling} pair is not in "
                  + $"{TargetType}.{TargetMethod}; skipped (game updated?)");
            return false;
        }

        // Taken before anything is replaced: the ceiling is the instruction
        // after the floor, and once the floor is swapped out that relationship
        // is gone.
        var ceiling = floor.Next;

        var gate = module.GetType(GateType) ?? InjectGate(module);
        var floorGetter = gate.Methods.First(m => m.Name == FloorMethod);
        var ceilingGetter = gate.Methods.First(m => m.Name == CeilingMethod);

        var processor = method.Body.GetILProcessor();
        processor.Replace(floor, processor.Create(OpCodes.Call, floorGetter));
        processor.Replace(ceiling, processor.Create(OpCodes.Call, ceilingGetter));

        notes.Add($"aspect range: {TargetType}.{TargetMethod} now asks {GateType} "
                  + $"instead of loading {StockFloor}/{StockCeiling}");
        return true;
    }

    static bool IsLoad(Instruction instruction, float value) =>
        instruction.OpCode == OpCodes.Ldc_R4
        && instruction.Operand is float f
        && f.Equals(value);

    /// <summary>
    /// Injects <c>public static class SilksongAspectGate</c>.
    ///
    /// <code>
    ///     public static float Floor;                  // 0 until the game sets it
    ///     public static float Ceiling;
    ///     public static float GetFloor()              // what the woven calls read
    ///         => Floor   &lt; 1f ? 1.6f       : Floor;
    ///     public static float GetCeiling()
    ///         => Ceiling &lt; 1f ? 2.3916667f : Ceiling;
    /// </code>
    ///
    /// There is deliberately no static constructor. Fields that default to
    /// zero and getters that treat zero as "nobody said" need no
    /// initialisation order to be correct, and cannot be defeated by the game
    /// reading the value before our C# has run -- which, since this is read
    /// from Update, it otherwise could be.
    /// </summary>
    static TypeDefinition InjectGate(ModuleDefinition module)
    {
        var type = new TypeDefinition(
            "", GateType,
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed
                | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
            module.TypeSystem.Object);

        AddGate(module, type, FloorField, FloorMethod, StockFloor);
        AddGate(module, type, CeilingField, CeilingMethod, StockCeiling);

        module.Types.Add(type);
        return type;
    }

    /// <summary>One field and the getter that defends it.</summary>
    static void AddGate(
        ModuleDefinition module, TypeDefinition type,
        string fieldName, string methodName, float stock)
    {
        var single = module.TypeSystem.Single;

        var field = new FieldDefinition(
            fieldName, FieldAttributes.Public | FieldAttributes.Static, single);
        type.Fields.Add(field);

        var getter = new MethodDefinition(
            methodName,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            single);

        var il = getter.Body.GetILProcessor();
        var fallback = il.Create(OpCodes.Ldc_R4, stock);

        // Below 1.0 -- and, because blt.un is the unordered comparison, an
        // unset NaN too -- means nobody has answered; use the game's own. An
        // aspect ratio below 1.0 is a portrait window, which this must never
        // hand the game whatever it is asked for.
        il.Append(il.Create(OpCodes.Ldsfld, field));
        il.Append(il.Create(OpCodes.Ldc_R4, 1f));
        il.Append(il.Create(OpCodes.Blt_Un_S, fallback));
        il.Append(il.Create(OpCodes.Ldsfld, field));
        il.Append(il.Create(OpCodes.Ret));
        il.Append(fallback);
        il.Append(il.Create(OpCodes.Ret));

        type.Methods.Add(getter);
    }
}

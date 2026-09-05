using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ModWeaver;

/// <summary>
/// Harmony's other way of naming a target: a TargetMethod() the game calls.
///
/// Harmony lets a patch class compute its own target instead of declaring it,
/// either through [HarmonyTargetMethod] or -- far more commonly, because it
/// needs no attribute at all -- a static method simply named TargetMethod.
/// It is the idiom every plugin reaches for the moment a target is private or
/// overloaded, which is exactly when a plain [HarmonyPatch(typeof(T), "M")]
/// stops being expressive enough.
///
/// "Computed" sounds like it settles the question for a weaver that runs
/// before the game does, and for a body that reads a config file it does. But
/// the overwhelming majority of these are a single constant expression:
///
///     static MethodBase TargetMethod() =>
///         AccessTools.Method(typeof(HealthManager), "TakeDamage",
///                            new[] { typeof(HitInstance) });
///
/// There is nothing there that needs a running game -- every operand is in the
/// metadata this weaver is already holding. So the body is interpreted rather
/// than refused: a few opcodes' worth of constant folding over a stack that
/// only ever holds a type, a string, an int or an array of types.
///
/// Anything else -- a branch, a field read, a call this does not know -- ends
/// the attempt and the caller reports the plugin as picking its targets at
/// runtime, which is the truth and is what the user needs to be told. The
/// alternative, and what happened before this existed, is a plugin reported
/// "Ok" with nothing woven into the game at all.
/// </summary>
internal static class TargetMethods
{
    /// The names Harmony recognises on a static method with no attribute.
    public const string One = "TargetMethod";
    public const string Many = "TargetMethods";

    /// <summary>
    /// The chooser this patch class declares, by attribute or by name, or null
    /// when it declares none.
    /// </summary>
    public static MethodDefinition? Find(TypeDefinition type)
    {
        foreach (var method in type.Methods)
        {
            if (method.CustomAttributes.Any(a =>
                    a.AttributeType.Name is "HarmonyTargetMethod" or "HarmonyTargetMethods"))
            {
                return method;
            }
            if (method.IsStatic && method.Name is One or Many) return method;
        }
        return null;
    }

    /// <summary>
    /// Reads a constant chooser body into a spec, or returns null when the
    /// body needs a running game.
    ///
    /// Only [One] is ever worth trying: TargetMethods returns a sequence, and
    /// a class that patches several methods with one pair of prefixes is not
    /// something the rest of this weaver is shaped to express.
    /// </summary>
    public static PatchSpec? Read(MethodDefinition chooser)
    {
        if (chooser.Name == Many || !chooser.HasBody) return null;

        var stack = new List<object?>();
        foreach (var ins in chooser.Body.Instructions)
        {
            if (!Step(ins, stack)) return null;
            if (ins.OpCode.Code == Code.Ret) break;
        }

        return stack.Count == 1 ? stack[0] as PatchSpec : null;
    }

    /// <summary>
    /// One instruction against the little constant stack.
    ///
    /// False means "this body does something real", and the whole attempt is
    /// abandoned -- deliberately, rather than guessing a target from the part
    /// that was understood. A patch woven into the wrong method is worse than
    /// a patch that is honestly reported as not woven at all.
    /// </summary>
    static bool Step(Instruction ins, List<object?> stack)
    {
        switch (ins.OpCode.Code)
        {
            case Code.Nop:
                return true;

            case Code.Ldtoken:
                if (ins.Operand is not TypeReference token) return false;
                Push(stack, token);
                return true;

            case Code.Ldstr:
                Push(stack, (string)ins.Operand);
                return true;

            case Code.Ldnull:
                Push(stack, null);
                return true;

            case Code.Ldc_I4: Push(stack, (int)ins.Operand); return true;
            case Code.Ldc_I4_S: Push(stack, (int)(sbyte)ins.Operand); return true;
            case Code.Ldc_I4_M1: Push(stack, -1); return true;
            case Code.Ldc_I4_0: Push(stack, 0); return true;
            case Code.Ldc_I4_1: Push(stack, 1); return true;
            case Code.Ldc_I4_2: Push(stack, 2); return true;
            case Code.Ldc_I4_3: Push(stack, 3); return true;
            case Code.Ldc_I4_4: Push(stack, 4); return true;
            case Code.Ldc_I4_5: Push(stack, 5); return true;
            case Code.Ldc_I4_6: Push(stack, 6); return true;
            case Code.Ldc_I4_7: Push(stack, 7); return true;
            case Code.Ldc_I4_8: Push(stack, 8); return true;

            case Code.Dup:
                if (stack.Count == 0) return false;
                Push(stack, stack[^1]);
                return true;

            case Code.Newarr:
                // Only an array of types is ever a target's parameter list.
                if (ins.Operand is not TypeReference element ||
                    element.FullName != "System.Type" ||
                    Pop(stack) is not int length ||
                    length < 0 || length > 64)
                {
                    return false;
                }
                Push(stack, new TypeReference?[length]);
                return true;

            case Code.Stelem_Ref:
            {
                // Pops all three: the array reference this consumes is the one
                // the preceding dup left, and the original stays underneath
                // for the call that follows. Peeking instead would leave a
                // second copy and shift every later argument by one.
                var value = Pop(stack);
                if (Pop(stack) is not int index) return false;
                if (Pop(stack) is not TypeReference?[] array) return false;
                if (index < 0 || index >= array.Length) return false;
                if (value is not TypeReference type) return false;
                array[index] = type;
                return true;
            }

            case Code.Call:
            case Code.Callvirt:
                return Call(ins.Operand as MethodReference, stack);

            case Code.Ret:
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// The handful of calls a constant chooser is built from.
    ///
    /// GetTypeFromHandle is the other half of `typeof`, and the rest are the
    /// AccessTools lookups plus Type.GetMethod. Everything is matched by name
    /// and arity rather than by full signature, because AccessTools has grown
    /// overloads across Harmony versions and a plugin is compiled against
    /// whichever one it was written for.
    /// </summary>
    static bool Call(MethodReference? called, List<object?> stack)
    {
        if (called is null) return false;

        var owner = called.DeclaringType?.FullName ?? "";
        var name = called.Name;

        // typeof(T) is ldtoken + this call; the token is already the type.
        if (owner == "System.Type" && name == "GetTypeFromHandle") return true;

        var args = new object?[called.Parameters.Count];
        for (var i = args.Length - 1; i >= 0; i--) args[i] = Pop(stack);

        // Type.GetMethod is an instance call, so the type is under the args.
        var declaring = owner == "System.Type"
            ? Pop(stack) as TypeReference
            : args.Length > 0 ? args[0] as TypeReference : null;

        var spec = (owner, name) switch
        {
            ("HarmonyLib.AccessTools", "Method" or "DeclaredMethod") => Lookup(declaring, args, 1),
            ("HarmonyLib.AccessTools", "PropertyGetter" or "DeclaredPropertyGetter") =>
                Accessor(declaring, args, HarmonyMethodType.Getter),
            ("HarmonyLib.AccessTools", "PropertySetter" or "DeclaredPropertySetter") =>
                Accessor(declaring, args, HarmonyMethodType.Setter),
            ("HarmonyLib.AccessTools", "Constructor" or "DeclaredConstructor") =>
                Constructor(declaring, args),
            ("System.Type", "GetMethod") => Lookup(declaring, args, 0),
            _ => null,
        };

        if (spec is null) return false;
        Push(stack, spec);
        return true;
    }

    /// AccessTools.Method(type, name, parameters?, generics?) and Type.GetMethod(name, ...).
    static PatchSpec? Lookup(TypeReference? declaring, object?[] args, int nameAt)
    {
        if (declaring is null || args.Length <= nameAt || args[nameAt] is not string name) return null;

        // The parameter list is optional and is the only Type[] in the call;
        // a null one means "whichever overload", which Resolve already treats
        // as unspecified.
        var parameters = args.Skip(nameAt + 1).OfType<TypeReference?[]>().FirstOrDefault();

        return new PatchSpec
        {
            DeclaringType = declaring,
            MethodName = name,
            ArgumentTypes = Solid(parameters),
        };
    }

    static PatchSpec? Accessor(TypeReference? declaring, object?[] args, HarmonyMethodType kind)
    {
        if (declaring is null || args.Length < 2 || args[1] is not string name) return null;
        return new PatchSpec { DeclaringType = declaring, MethodName = name, MethodType = kind };
    }

    static PatchSpec? Constructor(TypeReference? declaring, object?[] args)
    {
        if (declaring is null) return null;
        return new PatchSpec
        {
            DeclaringType = declaring,
            MethodType = HarmonyMethodType.Constructor,
            ArgumentTypes = Solid(args.Skip(1).OfType<TypeReference?[]>().FirstOrDefault()),
        };
    }

    /// An array with a hole in it was never fully understood, so it is dropped
    /// rather than resolved against.
    static TypeReference[]? Solid(TypeReference?[]? types)
    {
        if (types is null) return null;
        if (types.Any(t => t is null)) return null;
        return types.Select(t => t!).ToArray();
    }

    static void Push(List<object?> stack, object? value)
    {
        // A body long enough to overflow this is not the constant expression
        // this exists to read.
        if (stack.Count > 64) stack.Clear();
        stack.Add(value);
    }

    static object? Pop(List<object?> stack)
    {
        if (stack.Count == 0) return null;
        var value = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        return value;
    }
}

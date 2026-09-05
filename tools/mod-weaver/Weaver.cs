using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ModWeaver;

/// <summary>
/// The chainloader, minus the loading: finds each plugin's Harmony patches and
/// applies them to the staged assembly set.
/// </summary>
internal sealed class Weaver
{
    readonly string _assemblies;
    readonly StagedResolver _resolver;
    readonly HashSet<AssemblyDefinition> _dirty = new();

    /// Assemblies a plugin may ship a copy of, and which we supply ourselves.
    ///
    /// A BepInEx download contains BepInEx.dll and 0Harmony.dll because that
    /// is how it works on a PC. Here they are the port's own shims, compiled
    /// on the device against the depot, and letting a plugin's copy overwrite
    /// them would replace working code with an implementation that assumes a
    /// runtime we do not have.
    static readonly HashSet<string> Supplied = new(StringComparer.OrdinalIgnoreCase)
    {
        "BepInEx", "BepInEx.Core", "BepInEx.Harmony", "BepInEx.Preloader",
        "0Harmony", "0Harmony20", "HarmonyXInterop",
        "Mono.Cecil", "Mono.Cecil.Mdb", "Mono.Cecil.Pdb", "Mono.Cecil.Rocks",
        "MonoMod.RuntimeDetour", "MonoMod.Utils", "MonoMod.Common",
    };

    public Weaver(string assemblies)
    {
        _assemblies = assemblies;
        _resolver = new StagedResolver(assemblies);
    }

    public List<PluginReport> Run(IReadOnlyList<string> mods)
    {
        var reports = new List<PluginReport>();
        var plugins = new List<(string Path, AssemblyDefinition Assembly, PluginReport Report)>();

        // Read everything first. A mod is often several assemblies -- a plugin
        // and the library it was split out of -- and the plugin does not
        // resolve until the library is in the resolver's hands.
        foreach (var path in mods)
        {
            var file = Path.GetFileName(path);
            if (!File.Exists(path))
            {
                reports.Add(Failed(file, "", $"{file} is not there any more"));
                continue;
            }

            AssemblyDefinition assembly;
            try
            {
                assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters
                {
                    // In memory, because the file is written back out to the
                    // staged set under its assembly name and a held handle on
                    // an external-storage file is a needless way to fail.
                    InMemory = true,
                    AssemblyResolver = _resolver,
                });
            }
            catch (Exception e)
            {
                reports.Add(Failed(file, "", $"not a managed assembly ({e.GetType().Name})"));
                continue;
            }

            var name = assembly.Name.Name;
            if (Supplied.Contains(name))
            {
                Console.WriteLine($"{file}: skipped, {name} is supplied by the port");
                continue;
            }
            if (File.Exists(Path.Combine(_assemblies, name + ".dll")))
            {
                reports.Add(Failed(file, name, $"{name} is already part of the game and cannot be replaced"));
                continue;
            }

            var report = new PluginReport { File = file, Assembly = name };
            reports.Add(report);
            _resolver.Add(assembly);
            plugins.Add((path, assembly, report));
        }

        foreach (var (path, assembly, report) in plugins)
        {
            try
            {
                Process(assembly, report);
            }
            catch (Exception e)
            {
                report.Fail($"weaving threw {e.GetType().Name}: {e.Message}");
            }

            if (report.Status == PluginStatus.Failed) continue;

            // Named for the assembly rather than the file: Unity resolves an
            // assembly by its name, and a plugin shipped as MyMod-1.2.dll
            // would otherwise be listed under a name nothing refers to.
            //
            // Written through Cecil rather than copied when the weave changed
            // it -- raising a private patch method to public is a change to
            // the plugin, not to the game.
            var staged = Path.Combine(_assemblies, assembly.Name.Name + ".dll");
            if (_dirty.Remove(assembly)) assembly.Write(staged);
            else File.Copy(path, staged, overwrite: true);
        }

        foreach (var assembly in _dirty)
        {
            var to = Path.Combine(_assemblies, assembly.Name.Name + ".dll");
            assembly.Write(to);
            Console.WriteLine($"rewrote {Path.GetFileName(to)}");
        }

        // A plugin whose patches all failed is still in the build: its own
        // code runs, and a mod that adds a menu without patching anything is
        // an ordinary and useful kind of mod.
        return reports;
    }

    static PluginReport Failed(string file, string assembly, string why)
    {
        var report = new PluginReport { File = file, Assembly = assembly };
        report.Fail(why);
        return report;
    }

    void Process(AssemblyDefinition plugin, PluginReport report)
    {
        foreach (var reference in plugin.MainModule.AssemblyReferences)
        {
            if (Supplied.Contains(reference.Name)) continue;
            if (_resolver.CanResolve(reference)) continue;
            report.Fail($"needs {reference.Name}, which is not in this build");
            return;
        }

        Describe(plugin, report);
        if (!ReferencesResolve(plugin, report)) return;
        NoteUnsupportedCalls(plugin, report);

        var gate = GateFor(plugin);

        foreach (var type in plugin.MainModule.GetTypes())
        {
            try
            {
                ProcessPatchClass(type, report, gate);
            }
            catch (Exception e)
            {
                report.Note($"{type.Name} could not be woven ({e.GetType().Name})");
            }
        }
    }

    /// <summary>
    /// The plugin's on/off switch, as a field in the plugin's own assembly.
    ///
    /// Every woven call site is wrapped in a test of this, and the chainloader
    /// sets it at startup from the launcher's list. That is what makes a
    /// toggle free: the mod is compiled into the game either way, and turning
    /// it off is a branch not taken rather than a twenty-minute rebuild.
    ///
    /// It defaults to false and nothing initialises it but the chainloader.
    /// A static initialiser would be the obvious way to default it to "on",
    /// and it would be a trap: the runtime may run a type's initialiser at any
    /// point before the first access, so one that fired after the chainloader
    /// had already written the field would quietly turn a disabled mod back
    /// on. Defaulting to off also means a chainloader that never ran leaves
    /// every mod inert, which is the safe direction to fail in.
    ///
    /// The name cannot be written in C#, so it cannot collide with anything
    /// the plugin declares.
    /// </summary>
    FieldDefinition GateFor(AssemblyDefinition plugin)
    {
        var module = plugin.MainModule;

        var existing = module.GetType(GateType);
        if (existing is not null)
        {
            var found = existing.Fields.FirstOrDefault(f => f.Name == GateField);
            if (found is not null) return found;
        }

        var type = new TypeDefinition(
            "", GateType,
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed |
            TypeAttributes.Class | TypeAttributes.AnsiClass,
            module.TypeSystem.Object);
        var field = new FieldDefinition(
            GateField, FieldAttributes.Public | FieldAttributes.Static, module.TypeSystem.Boolean);
        type.Fields.Add(field);
        module.Types.Add(type);
        _dirty.Add(plugin);
        return field;
    }

    internal const string GateType = "<ModGate>";
    internal const string GateField = "Enabled";


    /// <summary>
    /// Every type and member the plugin names must exist in this build.
    ///
    /// This is the check that pays for itself. An unresolvable reference is not
    /// caught by anything else until il2cpp trips over it, and il2cpp failing
    /// takes the whole build down -- seventeen minutes of native compile that
    /// never starts, for one plugin using a Harmony class the shim does not
    /// have. Here it is one line in a report, before anything is built.
    ///
    /// Types are fatal to the plugin, because a type that is not there cannot
    /// be substituted. Members are only noted: Cecil cannot always resolve a
    /// member of a generic type it can resolve perfectly well, and refusing a
    /// working mod is worse than letting an unusual one through.
    /// </summary>
    bool ReferencesResolve(AssemblyDefinition plugin, PluginReport report)
    {
        var missing = new List<string>();

        foreach (var reference in plugin.MainModule.GetTypeReferences())
        {
            if (reference.IsGenericParameter) continue;
            if (missing.Count >= 5) break;
            if (Resolves(() => reference.Resolve() is not null)) continue;
            missing.Add(reference.FullName);
        }

        if (missing.Count > 0)
        {
            report.Fail($"needs {string.Join(", ", missing)}, which this build does not have");
            return false;
        }

        foreach (var reference in plugin.MainModule.GetMemberReferences())
        {
            if (missing.Count >= 5) break;
            var declaring = reference.DeclaringType;
            if (declaring is null || declaring.IsGenericInstance || declaring.IsArray) continue;
            if (Resolves(() => reference.Resolve() is not null)) continue;
            missing.Add($"{declaring.FullName}.{reference.Name}");
        }

        if (missing.Count > 0)
            report.Note($"calls {string.Join(", ", missing)}, which this build does not have");

        return true;
    }

    static bool Resolves(Func<bool> resolve)
    {
        try { return resolve(); }
        catch (AssemblyResolutionException) { return false; }
        catch (Exception) { return true; }
    }

    /// Reads [BepInPlugin(guid, name, version)] for the launcher's list.
    static void Describe(AssemblyDefinition plugin, PluginReport report)
    {
        foreach (var type in plugin.MainModule.GetTypes())
        {
            var attr = type.CustomAttributes.FirstOrDefault(a => a.AttributeType.Name == "BepInPlugin");
            if (attr is null) continue;
            var args = attr.ConstructorArguments;
            if (args.Count > 0) report.Guid = args[0].Value as string ?? "";
            if (args.Count > 1) report.Name = args[1].Value as string ?? "";
            if (args.Count > 2) report.Version = args[2].Value as string ?? "";
            return;
        }
    }

    /// <summary>
    /// What the plugin does that cannot survive an AOT compile.
    ///
    /// A note rather than a failure: a plugin that calls Harmony directly for
    /// one patch and declares the other twelve as attributes is still worth
    /// most of what it is worth. Saying which is which is the point.
    /// </summary>
    static void NoteUnsupportedCalls(AssemblyDefinition plugin, PluginReport report)
    {
        foreach (var type in plugin.MainModule.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                foreach (var ins in method.Body.Instructions)
                {
                    if (ins.Operand is not MethodReference called) continue;
                    var owner = called.DeclaringType.FullName;

                    if (owner == "HarmonyLib.Harmony" && called.Name is "Patch" or "ReversePatch")
                    {
                        report.Note("patches methods by calling Harmony at runtime, which does nothing here");
                    }
                    else if (owner.StartsWith("System.Reflection.Emit.", StringComparison.Ordinal))
                    {
                        report.Note("generates code at runtime (Reflection.Emit), which does nothing here");
                    }
                    else if (owner == "System.Reflection.Assembly" &&
                             called.Name is "Load" or "LoadFrom" or "LoadFile")
                    {
                        report.Note("loads assemblies at runtime, which does nothing here");
                    }
                }
            }
        }
    }

    void ProcessPatchClass(TypeDefinition type, PluginReport report, FieldDefinition gate)
    {
        var classSpec = Merge(type.CustomAttributes);
        var classPatched = type.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyPatch");

        // A class that chooses its own target, by attribute or by the bare
        // name Harmony also accepts. Most of these are a constant expression
        // and are read here rather than refused -- see TargetMethods for why
        // that is worth doing and where it stops.
        var chooser = TargetMethods.Find(type);
        if (chooser is not null)
        {
            var chosen = TargetMethods.Read(chooser);
            if (chosen is null)
            {
                report.Note($"{type.Name} picks its targets at runtime, which a build-time weaver cannot follow");
                return;
            }
            classSpec = classSpec.MergedWith(chosen);
        }

        var prefixes = new Dictionary<MethodDefinition, List<MethodDefinition>>();
        var postfixes = new Dictionary<MethodDefinition, List<MethodDefinition>>();

        foreach (var method in type.Methods)
        {
            if (method == chooser) continue;
            var kind = KindOf(method, classPatched);
            switch (kind)
            {
                case null:
                    continue;
                case "unsupported":
                    report.Note($"{type.Name}.{method.Name} is a transpiler or finalizer, which cannot be woven");
                    continue;
            }

            var spec = classSpec.MergedWith(Merge(method.CustomAttributes));
            if (spec.IsEmpty)
            {
                // Nothing said which method this patches, here or on the
                // class. Silence was the old answer and it read as success:
                // the plugin was reported "Ok" with nothing woven at all.
                report.Note($"{type.Name}.{method.Name} does not say which method it patches");
                continue;
            }

            var target = spec.Resolve(FindType, out var why);
            if (target is null)
            {
                report.Note(why);
                continue;
            }

            var into = kind == "prefix" ? prefixes : postfixes;
            if (!into.TryGetValue(target, out var list)) into[target] = list = new List<MethodDefinition>();
            list.Add(method);
        }

        foreach (var target in prefixes.Keys.Concat(postfixes.Keys).Distinct().ToList())
        {
            var pre = prefixes.TryGetValue(target, out var a) ? a : new List<MethodDefinition>();
            var post = postfixes.TryGetValue(target, out var b) ? b : new List<MethodDefinition>();

            if ((pre.Count > 1 || post.Count > 1) && pre.Concat(post).Any(UsesState))
            {
                report.Note($"{type.Name} uses __state across multiple patches on {target.FullName}; state is paired by position");
            }
            if (pre.Count(p => p.ReturnType.MetadataType == MetadataType.Boolean) > 1)
            {
                report.Note($"{type.Name} has multiple bool prefixes on {target.FullName}; a skipped original may skip later prefixes too");
            }

            // Paired so that a prefix and a postfix from the same class share
            // one __state local, which is the only way __state means anything.
            for (var i = 0; i < Math.Max(pre.Count, post.Count); i++)
            {
                var prefix = i < pre.Count ? pre[i] : null;
                var postfix = i < post.Count ? post[i] : null;
                if (!Weave.Apply(target, prefix, postfix, gate, report, a => _dirty.Add(a))) continue;
                report.Patched++;
            }
        }
    }

    /// "prefix", "postfix", "unsupported", or null for a method that is not a patch.
    static string? KindOf(MethodDefinition method, bool classPatched)
    {
        foreach (var a in method.CustomAttributes)
        {
            switch (a.AttributeType.Name)
            {
                case "HarmonyPrefix": return "prefix";
                case "HarmonyPostfix": return "postfix";
                case "HarmonyTranspiler":
                case "HarmonyFinalizer":
                case "HarmonyReversePatch": return "unsupported";
            }
        }

        // Harmony's other convention: inside a patched class, the names alone
        // are the annotation.
        var own = method.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyPatch");
        if (!classPatched && !own) return null;
        return method.Name switch
        {
            "Prefix" => "prefix",
            "Postfix" => "postfix",
            "Transpiler" or "Finalizer" => "unsupported",
            _ => null,
        };
    }

    static bool UsesState(MethodDefinition method) =>
        method.Parameters.Any(p => p.Name == "__state");

    static PatchSpec Merge(IEnumerable<CustomAttribute> attributes)
    {
        var spec = new PatchSpec();
        foreach (var a in attributes)
        {
            if (a.AttributeType.Name != "HarmonyPatch") continue;
            spec = spec.MergedWith(PatchSpec.FromAttribute(a));
        }
        return spec;
    }

    /// A type named as a string rather than referenced. Looked for in whatever
    /// has already been loaded, then in the game's own assembly.
    TypeDefinition? FindType(string fullName)
    {
        foreach (var assembly in _resolver.Loaded)
        {
            var type = assembly.MainModule.GetType(fullName);
            if (type is not null) return type;
        }
        var game = _resolver.TryResolve("Assembly-CSharp");
        return game?.MainModule.GetType(fullName);
    }
}

/// <summary>
/// Resolves against the staged assembly set and nothing else.
///
/// Cecil's default resolver falls back to the GAC and to the directory the
/// tool itself is in, which here means .NET 8's own class library -- so a
/// plugin referencing mscorlib would silently bind to a completely different
/// System.Object from the one il2cpp is about to compile. The set il2cpp is
/// handed is the only correct universe, so it is the only one offered.
/// </summary>
internal sealed class StagedResolver : IAssemblyResolver
{
    readonly string _dir;
    readonly Dictionary<string, AssemblyDefinition> _cache = new(StringComparer.OrdinalIgnoreCase);

    public StagedResolver(string dir) => _dir = dir;

    public IEnumerable<AssemblyDefinition> Loaded => _cache.Values;

    public void Add(AssemblyDefinition assembly) => _cache[assembly.Name.Name] = assembly;

    public AssemblyDefinition Resolve(AssemblyNameReference name) => Resolve(name, new ReaderParameters());

    public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
    {
        var found = TryResolve(name.Name);
        if (found is null) throw new AssemblyResolutionException(name);
        return found;
    }

    public bool CanResolve(AssemblyNameReference name) => TryResolve(name.Name) is not null;

    public AssemblyDefinition? TryResolve(string name)
    {
        if (_cache.TryGetValue(name, out var cached)) return cached;

        var path = Path.Combine(_dir, name + ".dll");
        if (!File.Exists(path)) return null;

        var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters
        {
            InMemory = true,
            AssemblyResolver = this,
        });
        _cache[name] = assembly;
        return assembly;
    }

    public void Dispose()
    {
        foreach (var a in _cache.Values) a.Dispose();
        _cache.Clear();
    }
}

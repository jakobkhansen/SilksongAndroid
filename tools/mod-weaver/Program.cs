using System.Text.Json;
using Mono.Cecil;

namespace ModWeaver;

// mod-weaver: BepInEx's chainloader, run at build time instead of at startup.
//
// A BepInEx 5 plugin is an ordinary managed assembly compiled against the
// game's Mono assemblies. On a PC that is enough: BepInEx loads it into the
// running Mono domain and HarmonyX rewrites the game's methods in memory.
// Neither of those exists after IL2CPP has AOT-compiled everything to C++ --
// there is no JIT to emit a detour into, and no assembly loader to hand a DLL
// to at runtime.
//
// What this port has instead is the moment in between. The device converts the
// game itself, so just before il2cpp runs there is a directory holding the
// depot's original IL assemblies, and a plugin DLL is a perfectly ordinary
// input to a static rewriter at that point. So the patches are applied then,
// as IL, and il2cpp compiles the already-patched game. The plugin's own code
// travels with it as another assembly in the set.
//
// The cost is that installing a mod is a rebuild rather than a restart, and
// that anything a plugin decides at runtime -- transpilers, a patch target
// computed from a string, Reflection.Emit -- cannot be honoured. Those are
// reported per plugin rather than silently dropped: see Report.cs.
//
// usage:
//   ModWeaver weave --assemblies <dir> --report <file.json> --mod <dll> ...
//   ModWeaver builtin --assemblies <dir>
//
// <dir> is the staged assembly set il2cpp is about to be handed. Patched
// assemblies are rewritten in place, and every plugin that survives is copied
// in beside them.
//
// "builtin" is the port's own weaves rather than the user's -- see Builtin.cs.
// It is a separate verb because it must run on every build, including the
// overwhelmingly common one where the mods folder is empty, whereas "weave"
// has nothing to do then.
internal static class Program
{
    static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "builtin") return RunBuiltin(args);

        if (args.Length < 1 || args[0] != "weave")
        {
            Console.Error.WriteLine(
                "usage: ModWeaver weave --assemblies <dir> --report <file.json> --mod <dll> [--mod <dll> ...]\n" +
                "       ModWeaver builtin --assemblies <dir>");
            return 2;
        }

        string? assemblies = null;
        string? report = null;
        var mods = new List<string>();

        for (var i = 1; i < args.Length; i++)
        {
            string Next(string what) =>
                i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{what} needs a value");

            switch (args[i])
            {
                case "--assemblies": assemblies = Next("--assemblies"); break;
                case "--report": report = Next("--report"); break;
                case "--mod": mods.Add(Next("--mod")); break;
                default:
                    Console.Error.WriteLine($"unknown argument: {args[i]}");
                    return 2;
            }
        }

        if (assemblies is null || report is null)
        {
            Console.Error.WriteLine("--assemblies and --report are both required");
            return 2;
        }
        if (!Directory.Exists(assemblies))
        {
            Console.Error.WriteLine($"no such assembly directory: {assemblies}");
            return 2;
        }

        // A failure here must not be able to take the build down: a plugin is
        // the user's file, not ours, and the only sane response to one that
        // cannot be woven is to leave it out and say so. Only an error that
        // makes the whole run meaningless -- an unreadable assembly directory,
        // a report that cannot be written -- is fatal.
        try
        {
            var results = new Weaver(assemblies).Run(mods);
            Write(report, results);
            foreach (var r in results)
            {
                Console.WriteLine($"{r.File}: {r.Status}" +
                    (r.Patched > 0 ? $", {r.Patched} patch(es)" : "") +
                    (r.Issues.Count > 0 ? $" -- {string.Join("; ", r.Issues)}" : ""));
            }
            var ok = results.Count(r => r.Status != PluginStatus.Failed);
            Console.WriteLine($"woven {ok} of {results.Count} plugin(s)");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"mod-weaver failed: {e}");
            return 1;
        }
    }

    /// <summary>
    /// The port's own weaves. See Builtin.cs.
    ///
    /// Always exits 0 for anything short of a broken command line: a built-in
    /// weave that cannot be applied leaves stock behaviour, which is a working
    /// game, and failing the build over a frill would be the worse outcome by
    /// a wide margin.
    /// </summary>
    static int RunBuiltin(string[] args)
    {
        string? assemblies = null;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--assemblies":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("--assemblies needs a value");
                        return 2;
                    }
                    assemblies = args[++i];
                    break;
                default:
                    Console.Error.WriteLine($"unknown argument: {args[i]}");
                    return 2;
            }
        }

        if (assemblies is null)
        {
            Console.Error.WriteLine("--assemblies is required");
            return 2;
        }
        if (!Directory.Exists(assemblies))
        {
            Console.Error.WriteLine($"no such assembly directory: {assemblies}");
            return 2;
        }

        foreach (var note in Builtin.Apply(assemblies)) Console.WriteLine(note);
        return 0;
    }

    static void Write(string path, IReadOnlyList<PluginReport> results)
    {
        Directory.GetParent(Path.GetFullPath(path))?.Create();
        var json = JsonSerializer.Serialize(
            new { generated = DateTimeOffset.UtcNow.ToString("o"), plugins = results },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
}

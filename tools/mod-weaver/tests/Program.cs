using Mono.Cecil;
using Mono.Cecil.Cil;
using Reflection = System.Reflection;

namespace ModWeaver.RegressionTests;

internal static class Program
{
    static int Main()
    {
        (string Name, Action Run)[] tests =
        {
            ("false prefix woven between postfixes", () => Composition(1, 2, 3)),
            ("false prefix woven before postfixes", () => Composition(2, 1, 3)),
            ("false prefix woven after postfixes", () => Composition(1, 3, 2)),
            ("default result when original is skipped", DefaultResult),
            ("prefix ordering and paired state", () => State(skip: false)),
            ("skipped prefix leaves state default, not postfix skipped", () => State(skip: true)),
            ("multiple postfixes in one class", MultiplePostfixes),
            ("void target and multiple returns", VoidTarget),
            ("finally and exceptional original paths", Finally),
            ("protected return and open-ended handler", ProtectedReturn),
            ("rejected patch leaves shared composition intact", RejectedPatch),
            ("missing direct method rejected before weaving", () => InvalidMember(field: false, signature: false)),
            ("missing direct field rejected before weaving", () => InvalidMember(field: true, signature: false)),
            ("wrong method signature rejected before weaving", () => InvalidMember(field: false, signature: true)),
            ("wrong field signature rejected before weaving", () => InvalidMember(field: true, signature: true)),
            ("dependency rejection cascades in reverse input order", DependencyCascade),
            ("missing assembly rejection still cascades", MissingAssembly),
            ("rejected assemblies unavailable to string targets", RejectedStringTarget),
            ("generic member resolution remains best-effort", GenericMembers),
            ("builtin weave survives plugin composition", BuiltinComposition),
            ("controller menu aliases survive remapping and repeated weaving", ControllerMenuTests.Bindings),
            ("controller menu weave skips an unsupported API", ControllerMenuTests.MissingApi),
        };
        var failed = 0;
        foreach (var (name, run) in tests)
        {
            try
            {
                run();
                Console.WriteLine("PASS " + name);
            }
            catch (Exception e)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {name}\n{e}");
            }
        }
        Console.WriteLine($"{tests.Length - failed}/{tests.Length} regression tests passed");
        return failed == 0 ? 0 : 1;
    }

    static void Composition(params int[] order)
    {
        using var fixture = new Fixture();
        var a = fixture.Plugin("A", new Patch { Digit = 1, PostfixArgumentDelta = 1 });
        var b = fixture.Plugin("B", new Patch
        {
            Prefix = false, Result = 4, State = 37, ArgumentDelta = 3,
            PostfixArgumentDelta = 2, Digit = 2, PrefixDigit = 9,
        });
        var c = fixture.Plugin("C", new Patch { Digit = 3, ReturnResult = true, ArgumentByValue = true });
        var plugins = new[] { a, b, c };
        var reports = fixture.Run(order.Select(i => plugins[i - 1]).ToArray());
        foreach (var report in reports) Accepted(report);
        Structure(fixture, "Run", resultLocals: 4);

        using var run = fixture.Load();
        run.Enable("A", "B", "C");
        var expected = order.Aggregate(4, (result, digit) => result * 10 + digit);
        Equal((expected, 8), run.Call(2), "shared result and ref argument");
        Equal(0, run.GameField("OriginalCalls"), "original skipped");
        Equal(order.Aggregate(9, (trace, digit) => trace * 10 + digit), run.GameField("Trace"), "postfix order");
        foreach (var name in new[] { "A", "B", "C" })
        {
            Equal(1, run.PatchField(name, "PostfixCalls"), name + " postfix executes once");
            Equal(10, run.PatchField(name, "InstanceSeen"), name + " __instance");
        }
        Equal(37, run.PatchField("B", "StateSeen"), "state from false prefix");
        Equal(10, run.PatchField("B", "PrefixInstanceSeen"), "prefix __instance");
        var resultBefore = 4;
        var argumentBefore = 5;
        foreach (var digit in order)
        {
            var name = plugins[digit - 1].Name.Name;
            Equal(resultBefore, run.PatchField(name, "ResultSeen"), name + " sees preceding result");
            Equal(argumentBefore, run.PatchField(name, "ArgumentSeen"), name + " sees preceding ref changes");
            resultBefore = resultBefore * 10 + digit;
            if (digit != 3) argumentBefore += digit;
        }

        run.Reset();
        run.Enable("A", "C");
        Equal((1413, 5), run.Call(2), "disabled false prefix cannot skip original");
        Equal(1, run.GameField("OriginalCalls"), "original runs with skip plugin off");
        Equal(0, run.PatchField("B", "PostfixCalls"), "disabled plugin postfix stays off");
        Equal(0, run.PatchField("B", "PrefixCalls"), "disabled plugin prefix stays off");

        run.Reset();
        run.Enable("B");
        Equal((42, 7), run.Call(2), "other postfix gates stay independent");
        Equal(0, run.PatchField("A", "PostfixCalls"), "A gate");
        Equal(0, run.PatchField("C", "PostfixCalls"), "C gate");

        run.Reset();
        Equal((14, 4), run.Call(2), "all gates off preserves positive original return");
        Equal((-1, -3), run.Call(-5), "all gates off preserves negative original return");
        Equal(2, run.GameField("OriginalCalls"), "both original returns executed");
        foreach (var name in new[] { "A", "B", "C" })
            Equal(0, run.PatchField(name, "PostfixCalls"), "default-off postfix " + name);
    }

    static void DefaultResult()
    {
        using var fixture = new Fixture();
        var post = fixture.Plugin("Post", new Patch());
        var skip = fixture.Plugin("Skip", new Patch { Prefix = false, Postfix = false });
        foreach (var report in fixture.Run(post, skip)) Accepted(report);
        using var run = fixture.Load();
        run.Enable("Post", "Skip");
        Equal((1, 2), run.Call(2), "postfix starts from default result on skip");
        Equal(0, run.PatchField("Post", "ResultSeen"), "default __result");
        Equal(1, run.PatchField("Post", "PostfixCalls"), "postfix survives prefix-only patch");
    }

    static void State(bool skip)
    {
        using var fixture = new Fixture();
        var a = fixture.Plugin("A", new Patch
        {
            Prefix = true, State = 11, ArgumentDelta = 2, PostfixArgumentDelta = 1,
            Digit = 1, PrefixDigit = 3,
        });
        var b = fixture.Plugin("B", new Patch
        {
            Prefix = !skip, State = 22, ArgumentDelta = 3, PostfixArgumentDelta = 2,
            Result = skip ? 7 : null, Digit = 2, PrefixDigit = 4,
        });
        foreach (var report in fixture.Run(a, b)) Accepted(report);
        using var run = fixture.Load();
        run.Enable("A", "B");
        Equal(skip ? (712, 7) : (1812, 11), run.Call(1), "result and ref-argument composition");
        Equal(skip ? 412 : 43512, run.GameField("Trace"), "reverse prefixes, original, forward postfixes");
        Equal(skip ? 0 : 11, run.PatchField("A", "StateSeen"), "A has its own state");
        Equal(22, run.PatchField("B", "StateSeen"), "B has its own state");
        Equal(skip ? 0 : 1, run.PatchField("A", "PrefixCalls"), "remaining prefix skip policy unchanged");
        Equal(1, run.PatchField("A", "PostfixCalls"), "A postfix always runs");
        Equal(1, run.PatchField("B", "PostfixCalls"), "B postfix always runs");

        run.Reset();
        run.Enable("A");
        Equal((151, 6), run.Call(1), "disabled second prefix does not affect first state or original");
        Equal(11, run.PatchField("A", "StateSeen"), "enabled state pair");
        Equal(0, run.PatchField("B", "StateSeen"), "disabled state pair");
    }

    static void MultiplePostfixes()
    {
        using var fixture = new Fixture();
        var multi = fixture.Plugin("Multi", new Patch { Digit = 1 }, new Patch { Digit = 2 });
        var first = multi.MainModule.GetType("Fixture.Patch0");
        var second = multi.MainModule.GetType("Fixture.Patch1");
        var postfix = second.Methods.Single();
        second.Methods.Remove(postfix);
        postfix.Name = "OtherPostfix";
        first.Methods.Add(postfix);
        var attribute = Fixture.Attribute(multi, "HarmonyLib", "HarmonyPostfix", 0);
        postfix.CustomAttributes.Add(new CustomAttribute(attribute));
        var skip = fixture.Plugin("Skip", new Patch { Prefix = false, Postfix = false, Result = 4 });
        var reports = fixture.Run(multi, skip);
        Equal(PluginStatus.Partial, reports[0].Status, "existing state-pairing limitation still reported");
        Equal(2, reports[0].Patched, "both postfixes woven");
        Contains(reports[0], "state is paired by position");
        Accepted(reports[1]);
        using var run = fixture.Load();
        run.Enable("Multi", "Skip");
        Equal((412, 2), run.Call(2), "all same-class postfixes run on skip");
        Equal(1, run.PatchField("Multi", "PostfixCalls", 0), "first postfix");
        Equal(1, run.PatchField("Multi", "PostfixCalls", 1), "second postfix");
    }

    static void VoidTarget()
    {
        using var fixture = new Fixture();
        var a = fixture.Plugin("A", new Patch { Target = "Touch", VoidTarget = true });
        var b = fixture.Plugin("B", new Patch
        {
            Target = "Touch", VoidTarget = true, Prefix = false, State = 9, ArgumentDelta = 3, Digit = 2,
        });
        foreach (var report in fixture.Run(a, b)) Accepted(report);
        Structure(fixture, "Touch", resultLocals: 2);
        using var run = fixture.Load();
        run.Enable("A", "B");
        Equal(((int?)null, 5), run.Call(2, "Touch"), "void original skipped");
        Equal(0, run.GameField("OriginalCalls"), "void original not called");
        Equal(1, run.PatchField("A", "PostfixCalls"), "earlier void postfix");
        Equal(9, run.PatchField("B", "StateSeen"), "void state");
        run.Reset();
        Equal(((int?)null, 4), run.Call(2, "Touch"), "void original with gates off");
        run.Reset();
        run.Enable("A");
        Equal(((int?)null, -1), run.Call(-1, "Touch"), "void early return");
        Equal(1, run.PatchField("A", "PostfixCalls"), "void early return postfix");
    }

    static void Finally()
    {
        using var fixture = new Fixture();
        var a = fixture.Plugin("A", new Patch { Target = "WithFinally" });
        var b = fixture.Plugin("B", new Patch
        {
            Target = "WithFinally", Prefix = false, State = 12, Result = 4, Digit = 2,
        });
        foreach (var report in fixture.Run(a, b)) Accepted(report);
        Structure(fixture, "WithFinally", resultLocals: 4);
        using var run = fixture.Load();
        run.Enable("A");
        Equal((41, 4), run.Call(2, "WithFinally"), "postfix after finally");
        Equal(1, run.GameField("FinallyCalls"), "finally ran once");
        Equal(561, run.GameField("Trace"), "finally before postfix");
        run.Reset();
        run.Enable("A", "B");
        Equal((412, 2), run.Call(2, "WithFinally"), "skip reaches every postfix outside try");
        Equal(0, run.GameField("FinallyCalls"), "skip does not enter original finally");
        Equal(12, run.PatchField("B", "StateSeen"), "state outside exception regions");
        run.Reset();
        run.Enable("A");
        Throws<InvalidOperationException>(() => run.Call(-1, "WithFinally"), "original exception preserved");
        Equal(1, run.GameField("FinallyCalls"), "finally runs while unwinding");
        Equal(0, run.PatchField("A", "PostfixCalls"), "postfix is not a finalizer");
        Equal(56, run.GameField("Trace"), "exception path unchanged");
        run.Reset();
        Equal((4, 4), run.Call(2, "WithFinally"), "gates off preserves protected body");
    }

    static void ProtectedReturn()
    {
        using var fixture = new Fixture();
        var post = fixture.Plugin("Post", new Patch { Target = "ProtectedReturn" });
        var skip = fixture.Plugin("Skip", new Patch
        {
            Target = "ProtectedReturn", Prefix = false, Result = 4, Digit = 2,
        });
        foreach (var report in fixture.Run(post, skip)) Accepted(report);
        Structure(fixture, "ProtectedReturn", resultLocals: 3);
        using var run = fixture.Load();
        run.Enable("Post");
        Equal((71, 2), run.Call(2, "ProtectedReturn"), "leave from original return executes finally");
        Equal(561, run.GameField("Trace"), "open handler ends before postfix");
        run.Reset();
        run.Enable("Post", "Skip");
        Equal((412, 2), run.Call(2, "ProtectedReturn"), "false prefix lands outside open handler");
        Equal(0, run.GameField("FinallyCalls"), "handler not entered on skip");
        run.Reset();
        Equal((7, 2), run.Call(2, "ProtectedReturn"), "disabled gates preserve protected return");
        Equal(1, run.GameField("FinallyCalls"), "finally still runs with gates off");
    }

    static void RejectedPatch()
    {
        using var fixture = new Fixture();
        var bad = fixture.Plugin("Unsupported", new Patch { Prefix = false, Result = 9, Digit = 9 });
        bad.MainModule.GetType("Fixture.Patch0").Methods[0].Parameters[0].Name = "notAnArgument";
        var a = fixture.Plugin("A", new Patch());
        var b = fixture.Plugin("B", new Patch { Prefix = false, Result = 4, Digit = 2 });
        var reports = fixture.Run(bad, a, b);
        Equal(PluginStatus.Partial, reports[0].Status, "unsupported injection remains partial, not invalid DLL");
        Equal(0, reports[0].Patched, "unsupported pair not committed");
        Accepted(reports[1]);
        Accepted(reports[2]);
        using var run = fixture.Load();
        run.Enable("Unsupported", "A", "B");
        Equal((412, 2), run.Call(2), "rejected pair cannot poison shared target");
        Equal(0, run.PatchField("Unsupported", "PrefixCalls"), "unsupported prefix absent");
        Equal(0, run.PatchField("Unsupported", "PostfixCalls"), "unsupported postfix absent");
    }

    static void InvalidMember(bool field, bool signature)
    {
        using var fixture = new Fixture();
        var before = File.ReadAllBytes(Path.Combine(fixture.Staged, "Assembly-CSharp.dll"));
        var bad = fixture.Plugin("Invalid", new Patch { Prefix = false, Result = 9, Digit = 9 });
        fixture.MissingMember(bad, field, signature);
        var report = fixture.Run(bad).Single();
        Rejected(fixture, report);
        Contains(report, signature ? (field ? "Api.Value" : "Api.Present") : (field ? "Api.MissingField" : "Api.Missing"));
        Equal("tests.Invalid", report.Guid, "failed plugin still described");
        True(before.SequenceEqual(File.ReadAllBytes(Path.Combine(fixture.Staged, "Assembly-CSharp.dll"))),
            "definite invalid DLL must leave target bytes untouched");
        using var run = fixture.Load();
        Equal((13, 3), run.Call(1), "original runs with no invalid plugin effects");
    }

    static void DependencyCascade()
    {
        using var fixture = new Fixture();
        var bad = fixture.Plugin("Invalid", new Patch { Prefix = false, Result = 9, Digit = 9 });
        fixture.MissingMember(bad, field: false);
        var middle = fixture.Plugin("Middle", new Patch { Digit = 8 });
        var top = fixture.Plugin("Top", new Patch { Digit = 7 });
        var good = fixture.Plugin("Good", new Patch());
        Fixture.DependsOn(middle, bad);
        Fixture.DependsOn(top, middle);
        Fixture.DependsOn(bad, top);
        var reports = fixture.Run(top, good, middle, bad).ToDictionary(r => r.Assembly);
        foreach (var name in new[] { "Invalid", "Middle", "Top" }) Rejected(fixture, reports[name]);
        Contains(reports["Middle"], "needs Invalid");
        Contains(reports["Top"], "needs Middle");
        Contains(reports["Top"], "rejected");
        Accepted(reports["Good"]);
        using var game = AssemblyDefinition.ReadAssembly(Path.Combine(fixture.Staged, "Assembly-CSharp.dll"));
        True(!game.MainModule.AssemblyReferences.Any(r => r.Name is "Invalid" or "Middle" or "Top"),
            "rejected plugins have no woven calls or gates in the target");
        using var run = fixture.Load();
        run.Enable("Good");
        Equal((131, 3), run.Call(1), "unrelated valid plugin stays accepted");
        Equal(1, run.GameField("OriginalCalls"), "no rejected prefix effects");
    }

    static void MissingAssembly()
    {
        using var fixture = new Fixture();
        var bad = fixture.Plugin("MissingLibrary", new Patch());
        bad.MainModule.AssemblyReferences.Add(new AssemblyNameReference("NotInstalled", new Version(1, 0)));
        var dependent = fixture.Plugin("Dependent", new Patch());
        Fixture.DependsOn(dependent, bad);
        var before = File.ReadAllBytes(Path.Combine(fixture.Staged, "Assembly-CSharp.dll"));
        var reports = fixture.Run(dependent, bad);
        foreach (var report in reports) Rejected(fixture, report);
        Contains(reports[1], "needs NotInstalled");
        Contains(reports[0], "needs MissingLibrary");
        True(before.SequenceEqual(File.ReadAllBytes(Path.Combine(fixture.Staged, "Assembly-CSharp.dll"))),
            "dependency preflight happens before any target change");
    }

    static void RejectedStringTarget()
    {
        using var fixture = new Fixture();
        var bad = fixture.Plugin("Invalid");
        fixture.MissingMember(bad, field: true);
        var removed = Fixture.Type(bad, "Removed", isStatic: false);
        removed.BaseType = bad.MainModule.ImportReference(fixture.Target);
        var method = Fixture.Method(removed, "Run", bad.MainModule.TypeSystem.Int32, isStatic: false);
        Fixture.Parameter(method, "value", new ByReferenceType(bad.MainModule.TypeSystem.Int32));
        method.Body.GetILProcessor().Emit(OpCodes.Ldc_I4_7);
        method.Body.GetILProcessor().Emit(OpCodes.Ret);
        var good = fixture.Plugin("StringTarget", new Patch());
        var attribute = good.MainModule.GetType("Fixture.Patch0").CustomAttributes.Single();
        attribute.ConstructorArguments[0] =
            new CustomAttributeArgument(good.MainModule.TypeSystem.String, "Fixture.Removed");
        var reports = fixture.Run(good, bad);
        Rejected(fixture, reports[1]);
        Equal(PluginStatus.Partial, reports[0].Status, "unavailable dynamic target remains a reported limitation");
        Equal(0, reports[0].Patched, "cannot patch a rejected DLL via a string");
        Contains(reports[0], "Fixture.Removed");
        True(File.Exists(Path.Combine(fixture.Staged, "StringTarget.dll")), "unrelated plugin code still staged");
    }

    static void GenericMembers()
    {
        using var fixture = new Fixture();
        var plugin = fixture.Plugin("Generic", new Patch());
        AddGenericCall(fixture, plugin, inherited: false);
        AddGenericCall(fixture, plugin, inherited: true);
        Accepted(fixture.Run(plugin).Single());
        using var resolver = new StagedResolver(fixture.Staged);
        using var input = AssemblyDefinition.ReadAssembly(fixture.InputPath(plugin),
            new ReaderParameters { AssemblyResolver = resolver });
        var inherited = input.MainModule.GetMemberReferences()
            .Where(r => r.DeclaringType?.Name == "Derived`2").ToList();
        True(inherited.Count > 0, "fixture includes an inherited generic method reference");
        True(inherited.Any(r => r.Resolve() is null),
            "fixture exercises a genuine Cecil generic-resolution uncertainty");
        using var run = fixture.Load();
        Equal(23, run.Invoke("Generic", "ReadGeneric"), "generic method and field execute");
        Equal(23, run.Invoke("Generic", "ReadInherited"), "unresolved generic references still execute");
        run.Enable("Generic");
        Equal((131, 3), run.Call(1), "generic plugin patches still woven");
    }

    static void AddGenericCall(Fixture fixture, AssemblyDefinition plugin, bool inherited)
    {
        var module = plugin.MainModule;
        var box = new GenericInstanceType(module.ImportReference(
            fixture.Game.MainModule.GetType(inherited ? "Fixture.Derived`2" : "Fixture.Box`1")));
        if (inherited) box.GenericArguments.Add(module.TypeSystem.String);
        box.GenericArguments.Add(module.TypeSystem.Int32);
        var parameter = box.ElementType.GenericParameters[inherited ? 1 : 0];
        var echo = new MethodReference("Echo", parameter, box);
        echo.Parameters.Add(new ParameterDefinition(parameter));
        var fieldOwner = new GenericInstanceType(module.ImportReference(fixture.Game.MainModule.GetType("Fixture.Box`1")));
        fieldOwner.GenericArguments.Add(module.TypeSystem.Int32);
        var field = new FieldReference("Value", fieldOwner.ElementType.GenericParameters[0], fieldOwner);
        var identity = new GenericInstanceMethod(module.ImportReference(fixture.Api.Methods.Single(m => m.Name == "Identity")));
        identity.GenericArguments.Add(module.TypeSystem.Int32);
        var method = Fixture.Method(module.GetType("Fixture.Plugin"),
            inherited ? "ReadInherited" : "ReadGeneric", module.TypeSystem.Int32);
        var il = method.Body.GetILProcessor();
        il.Emit(OpCodes.Ldc_I4, 23);
        il.Emit(OpCodes.Call, identity);
        il.Emit(OpCodes.Call, echo);
        il.Emit(OpCodes.Stsfld, field);
        il.Emit(OpCodes.Ldsfld, field);
        il.Emit(OpCodes.Ret);
    }

    static void BuiltinComposition()
    {
        using var fixture = new Fixture();
        var type = Fixture.Type(fixture.Game, "ForceCameraAspect");
        type.Namespace = "";
        var method = Fixture.Method(type, "AutoScaleViewportShared", fixture.Game.MainModule.TypeSystem.Single);
        var il = method.Body.GetILProcessor();
        il.Emit(OpCodes.Ldc_R4, 1.6f);
        il.Emit(OpCodes.Ldc_R4, 2.3916667f);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ret);
        fixture.Game.Write(Path.Combine(fixture.Staged, "Assembly-CSharp.dll"));
        True(Builtin.Apply(fixture.Staged).Any(n => n.Contains("rewritten")), "builtin applied");
        var plugin = fixture.Plugin("Post", new Patch());
        Accepted(fixture.Run(plugin).Single());
        True(Builtin.Apply(fixture.Staged).Any(n => n.Contains("already woven")), "builtin remains idempotent");
        using var run = fixture.Load();
        var aspect = run.Target.Assembly.GetType("ForceCameraAspect")!.GetMethod("AutoScaleViewportShared")!;
        Equal(1.6f + 2.3916667f, aspect.Invoke(null, null), "builtin gates default to stock constants");
        var gate = run.Target.Assembly.GetType(Builtin.GateType)!;
        gate.GetField(Builtin.FloorField)!.SetValue(null, 1.1f);
        gate.GetField(Builtin.CeilingField)!.SetValue(null, 10f);
        Equal(11.1f, aspect.Invoke(null, null), "builtin gate still works");
        run.Enable("Post");
        Equal((131, 3), run.Call(1), "plugin weave coexists with builtin");
    }

    static void Structure(Fixture fixture, string name, int resultLocals)
    {
        using var game = AssemblyDefinition.ReadAssembly(Path.Combine(fixture.Staged, "Assembly-CSharp.dll"));
        var method = game.MainModule.GetType("Fixture.Target").Methods.Single(m => m.Name == name);
        var instructions = method.Body.Instructions.ToHashSet();
        Equal(1, instructions.Count(i => i.OpCode.Code == Code.Ret), "one composed return path");
        Equal(resultLocals, method.Body.Variables.Count, "one shared result, separate state locals");
        foreach (var instruction in instructions)
        {
            if (instruction.Operand is Instruction branch) True(instructions.Contains(branch), "branch target attached");
            if (instruction.Operand is Instruction[] branches)
                True(branches.All(instructions.Contains), "switch targets attached");
            if (instruction.OpCode.OperandType == OperandType.ShortInlineBrTarget)
            {
                var distance = ((Instruction)instruction.Operand).Offset - instruction.Offset - instruction.GetSize();
                True(distance is >= sbyte.MinValue and <= sbyte.MaxValue, "short branch within range");
            }
        }
        foreach (var handler in method.Body.ExceptionHandlers)
        {
            True(handler.TryEnd is not null && instructions.Contains(handler.TryEnd), "try end preserved");
            True(handler.HandlerEnd is not null && instructions.Contains(handler.HandlerEnd), "handler closed before postfixes");
            var end = method.Body.Instructions.IndexOf(handler.HandlerEnd);
            foreach (var call in instructions.Where(i => i.OpCode.Code == Code.Call &&
                         i.Operand is MethodReference m && m.Name == "Postfix"))
                True(method.Body.Instructions.IndexOf(call) >= end, "postfix outside original handler");
        }
    }

    static void Accepted(PluginReport report)
    {
        Equal(PluginStatus.Ok, report.Status, $"{report.Assembly}: {string.Join("; ", report.Issues)}");
        Equal(1, report.Patched, report.Assembly + " patch count");
    }

    static void Rejected(Fixture fixture, PluginReport report)
    {
        Equal(PluginStatus.Failed, report.Status, report.Assembly + " rejected");
        Equal(0, report.Patched, report.Assembly + " no patch effects");
        True(!File.Exists(Path.Combine(fixture.Staged, report.Assembly + ".dll")), report.Assembly + " not staged");
        True(report.Issues.Count > 0, report.Assembly + " has a diagnostic");
    }

    static void Contains(PluginReport report, string text) =>
        True(report.Issues.Any(issue => issue.Contains(text, StringComparison.Ordinal)),
            $"{report.Assembly} diagnostic contains '{text}': {string.Join("; ", report.Issues)}");

    static void Equal(object? expected, object? actual, string message)
    {
        // ValueTuple<int, int> and ValueTuple<int?, int> are distinct boxed types.
        if (actual is ValueTuple<int?, int> nullable && expected is ValueTuple<int, int> integer)
            expected = ((int?)integer.Item1, integer.Item2);
        if (!Equals(expected, actual)) throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
    }

    static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (Reflection.TargetInvocationException e) when (e.InnerException is T) { return; }
        throw new InvalidOperationException(message + ": expected " + typeof(T).Name);
    }
}

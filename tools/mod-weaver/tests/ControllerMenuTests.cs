using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ModWeaver.RegressionTests;

internal static class ControllerMenuTests
{
    public static void Bindings()
    {
        using var fixture = new Fixture();
        BuildInputApi(fixture);
        var path = Path.Combine(fixture.Staged, "Assembly-CSharp.dll");
        fixture.Game.Write(path);
        Check(Builtin.Apply(fixture.Staged).Any(n => n.StartsWith("controller menus: added")), "weave applied");
        var bytes = File.ReadAllBytes(path);
        Check(Builtin.Apply(fixture.Staged).Any(n => n == "controller menus: already woven; nothing to do"),
            "second weave skipped");
        Check(bytes.SequenceEqual(File.ReadAllBytes(path)), "second weave leaves assembly unchanged");

        using var run = fixture.Load();
        var assembly = run.Target.Assembly;
        var handlerType = assembly.GetType("InputHandler")!;
        var actionsType = assembly.GetType("HeroActions")!;
        var actionType = assembly.GetType("InControl.PlayerAction")!;
        var handler = Activator.CreateInstance(handlerType)!;
        var map = handlerType.GetMethod("MapControllerButtons")!;
        // A missing action set is harmless during initialization.
        map.Invoke(handler, new object[] { true });
        foreach (var earlyReturn in new[] { true, false })
        {
            var actions = Activator.CreateInstance(actionsType)!;
            var pause = Activator.CreateInstance(actionType)!;
            var inventory = Activator.CreateInstance(actionType)!;
            // Represent a pre-existing/custom binding independently of the aliases.
            actionType.GetField("Bits")!.SetValue(pause, 16);
            actionType.GetField("Bits")!.SetValue(inventory, 16);
            actionsType.GetField("Pause")!.SetValue(actions, pause);
            actionsType.GetField("OpenInventory")!.SetValue(actions, inventory);
            handlerType.GetField("inputActions")!.SetValue(handler, actions);
            map.Invoke(handler, new object[] { earlyReturn });
            map.Invoke(handler, new object[] { earlyReturn });
            Check((int)actionType.GetField("Bits")!.GetValue(pause)! == (16 | 1), "Options pauses");
            Check((int)actionType.GetField("Bits")!.GetValue(inventory)! == (16 | 2 | 4 | 8),
                "Back/Share/Create open inventory; existing bindings preserved");
            Check((int)actionType.GetField("Adds")!.GetValue(pause)! == 1, "pause alias added once");
            Check((int)actionType.GetField("Adds")!.GetValue(inventory)! == 3, "inventory aliases added once");
            // Also tolerate an individual action missing from a partial action set.
            actionsType.GetField("Pause")!.SetValue(actions, null);
            map.Invoke(handler, new object[] { earlyReturn });
        }
    }

    public static void MissingApi()
    {
        using var fixture = new Fixture();
        BuildInputApi(fixture);
        var controls = fixture.Game.MainModule.GetType("InControl.InputControlType");
        controls.Fields.Remove(controls.Fields.Single(f => f.Name == "Create"));
        var path = Path.Combine(fixture.Staged, "Assembly-CSharp.dll");
        fixture.Game.Write(path);
        var bytes = File.ReadAllBytes(path);
        Check(Builtin.Apply(fixture.Staged).Any(n => n.StartsWith("controller menus: unsupported")),
            "unsupported API reported");
        Check(bytes.SequenceEqual(File.ReadAllBytes(path)), "unsupported API leaves assembly unchanged");
    }

    static void BuildInputApi(Fixture fixture)
    {
        var game = fixture.Game;
        var module = game.MainModule;
        TypeDefinition Type(string ns, string name)
        {
            var t = Fixture.Type(game, name, isStatic: false);
            t.Namespace = ns;
            Constructor(t);
            return t;
        }
        var controls = new TypeDefinition("InControl", "InputControlType",
            TypeAttributes.Public | TypeAttributes.Sealed, module.ImportReference(typeof(Enum)));
        module.Types.Add(controls);
        controls.Fields.Add(new FieldDefinition("value__",
            FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName,
            module.TypeSystem.Int32));
        // Deliberately unlike the game's numeric values: resolve aliases by name.
        foreach (var (name, value) in new[] { ("Options", 1), ("Back", 2), ("Share", 4), ("Create", 8) })
            controls.Fields.Add(new FieldDefinition(name,
                FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal, controls) { Constant = value });

        var source = Type("InControl", "BindingSource");
        var control = Fixture.Field(source, "Control", isStatic: false);
        var device = Type("InControl", "DeviceBindingSource");
        device.BaseType = source;
        device.Methods.Clear();
        var ctor = Fixture.Method(device, ".ctor", module.TypeSystem.Void, isStatic: false);
        ctor.IsSpecialName = ctor.IsRuntimeSpecialName = true;
        Fixture.Parameter(ctor, "control", controls);
        var il = ctor.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, source.Methods.Single(m => m.IsConstructor));
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, control);
        il.Emit(OpCodes.Ret);

        var action = Type("InControl", "PlayerAction");
        var bits = Fixture.Field(action, "Bits", isStatic: false);
        var adds = Fixture.Field(action, "Adds", isStatic: false);
        var has = Fixture.Method(action, "HasBinding", module.TypeSystem.Boolean, isStatic: false);
        Fixture.Parameter(has, "binding", source);
        il = has.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, bits);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldfld, control);
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Cgt_Un);
        il.Emit(OpCodes.Ret);
        var add = Fixture.Method(action, "AddBinding", module.TypeSystem.Boolean, isStatic: false);
        Fixture.Parameter(add, "binding", source);
        il = add.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldfld, bits);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldfld, control);
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Stfld, bits);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldfld, adds);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stfld, adds);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);

        var actions = Type("", "HeroActions");
        Fixture.Field(actions, "Pause", action, isStatic: false);
        Fixture.Field(actions, "OpenInventory", action, isStatic: false);
        var handler = Type("", "InputHandler");
        Fixture.Field(handler, "inputActions", actions, isStatic: false);
        var map = Fixture.Method(handler, "MapControllerButtons", module.TypeSystem.Void, isStatic: false);
        Fixture.Parameter(map, "earlyReturn", module.TypeSystem.Boolean);
        il = map.Body.GetILProcessor();
        var ret = Instruction.Create(OpCodes.Ret);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Brtrue_S, ret); // Direct branch to a return must execute the added bindings too.
        il.Emit(OpCodes.Ret);
        // The extra call grows this initially valid short branch past 127 bytes.
        for (var i = 0; i < 122; i++) il.Emit(OpCodes.Nop);
        il.Append(ret);
    }

    static void Constructor(TypeDefinition type)
    {
        var ctor = Fixture.Method(type, ".ctor", type.Module.TypeSystem.Void, isStatic: false);
        ctor.IsSpecialName = ctor.IsRuntimeSpecialName = true;
        var il = ctor.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, type.Module.ImportReference(typeof(object).GetConstructor(System.Type.EmptyTypes)!));
        il.Emit(OpCodes.Ret);
    }

    static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}

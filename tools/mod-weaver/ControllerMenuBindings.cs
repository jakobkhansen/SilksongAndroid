using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace ModWeaver;

/// <summary>Add Android PlayStation command aliases after the game rebuilds its bindings.</summary>
internal static class ControllerMenuBindings
{
    internal const string HelperName = "SilksongAddControllerMenuBindings";

    public static bool Weave(ModuleDefinition module, List<string> notes)
    {
        var handler = module.GetType("InputHandler");
        var target = handler?.Methods.SingleOrDefault(m => m.Name == "MapControllerButtons"
            && !m.IsStatic && m.HasBody && m.ReturnType.MetadataType == MetadataType.Void);
        if (target is null)
        {
            notes.Add("controller menus: InputHandler.MapControllerButtons not found; skipped");
            return false;
        }
        if (handler!.Methods.Any(m => m.Name == HelperName))
        {
            notes.Add("controller menus: already woven; nothing to do");
            return false;
        }

        var actions = module.GetType("HeroActions");
        var playerAction = module.GetType("InControl.PlayerAction");
        var binding = module.GetType("InControl.DeviceBindingSource");
        var controls = module.GetType("InControl.InputControlType");
        var inputActions = handler.Fields.SingleOrDefault(f => f.Name == "inputActions"
            && !f.IsStatic && f.FieldType.FullName == actions?.FullName);
        var ctor = binding?.Methods.SingleOrDefault(m => m.IsConstructor && !m.IsStatic
            && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == controls?.FullName);
        var hasBinding = playerAction?.Methods.SingleOrDefault(m => m.Name == "HasBinding"
            && !m.IsStatic && m.Parameters.Count == 1
            && m.Parameters[0].ParameterType.FullName == "InControl.BindingSource"
            && m.ReturnType.MetadataType == MetadataType.Boolean);
        var addBinding = playerAction?.Methods.SingleOrDefault(m => m.Name == "AddBinding"
            && !m.IsStatic && m.Parameters.Count == 1
            && m.Parameters[0].ParameterType.FullName == "InControl.BindingSource"
            && m.ReturnType.MetadataType == MetadataType.Boolean);
        var aliases = new[]
        {
            (Action: "Pause", Control: "Options"),
            (Action: "OpenInventory", Control: "Back"),
            (Action: "OpenInventory", Control: "Share"),
            (Action: "OpenInventory", Control: "Create"),
        };
        var fields = aliases.Select(a => actions?.Fields.SingleOrDefault(f => f.Name == a.Action
            && !f.IsStatic && f.FieldType.FullName == playerAction?.FullName)).ToArray();
        var values = aliases.Select(a => controls?.Fields.SingleOrDefault(f => f.Name == a.Control
            && f.HasConstant && f.Constant is int)).ToArray();
        var returns = target.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray();

        // Resolve the complete API before changing anything. A new game version
        // with different types or signatures should keep its original behaviour.
        if (inputActions is null || ctor is null || hasBinding is null || addBinding is null
            || fields.Any(f => f is null) || values.Any(v => v is null)
            || returns.Length == 0 || target.Body.HasExceptionHandlers)
        {
            notes.Add("controller menus: unsupported binding API or method body; skipped (game updated?)");
            return false;
        }

        var helper = new MethodDefinition(HelperName,
            MethodAttributes.Private | MethodAttributes.HideBySig, module.TypeSystem.Void);
        helper.Body.InitLocals = true;
        var actionLocal = new VariableDefinition(playerAction!);
        var bindingLocal = new VariableDefinition(binding!);
        helper.Body.Variables.Add(actionLocal);
        helper.Body.Variables.Add(bindingLocal);
        var il = helper.Body.GetILProcessor();
        var end = il.Create(OpCodes.Ret);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, inputActions);
        il.Emit(OpCodes.Brfalse, end);
        for (var i = 0; i < aliases.Length; i++)
        {
            var next = il.Create(OpCodes.Nop);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, inputActions);
            il.Emit(OpCodes.Ldfld, fields[i]!);
            il.Emit(OpCodes.Stloc, actionLocal);
            il.Emit(OpCodes.Ldloc, actionLocal);
            il.Emit(OpCodes.Brfalse, next);
            il.Emit(OpCodes.Ldc_I4, (int)values[i]!.Constant);
            il.Emit(OpCodes.Newobj, ctor);
            il.Emit(OpCodes.Stloc, bindingLocal);
            il.Emit(OpCodes.Ldloc, actionLocal);
            il.Emit(OpCodes.Ldloc, bindingLocal);
            il.Emit(OpCodes.Callvirt, hasBinding);
            il.Emit(OpCodes.Brtrue, next);
            il.Emit(OpCodes.Ldloc, actionLocal);
            il.Emit(OpCodes.Ldloc, bindingLocal);
            il.Emit(OpCodes.Callvirt, addBinding);
            il.Emit(OpCodes.Pop);
            il.Append(next);
        }
        il.Append(end);
        handler.Methods.Add(helper);

        // Preserve branch targets that point directly at a return, and widen
        // short branches before the extra instructions increase their distance.
        target.Body.SimplifyMacros();
        var processor = target.Body.GetILProcessor();
        foreach (var ret in returns)
        {
            ret.OpCode = OpCodes.Ldarg_0;
            ret.Operand = null;
            var call = processor.Create(OpCodes.Call, helper);
            processor.InsertAfter(ret, call);
            processor.InsertAfter(call, processor.Create(OpCodes.Ret));
        }
        target.Body.OptimizeMacros();
        notes.Add("controller menus: added Options -> Pause and Back/Share/Create -> OpenInventory");
        return true;
    }
}

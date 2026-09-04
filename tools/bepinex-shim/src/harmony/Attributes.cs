// The attributes a Harmony patch is written with.
//
// A plugin DLL carries these by reference, so they have to exist here with the
// same names and the same constructor shapes or the assembly does not resolve
// at all. What they mean is decided at build time by tools/mod-weaver, which
// reads exactly these attributes out of the plugin and rewrites the game's IL
// accordingly -- so the bodies here are empty on purpose. They are a
// vocabulary, not an implementation.

using System;

namespace HarmonyLib
{
    /// <summary>Which method a patch is aimed at, when it is not an ordinary one.</summary>
    public enum MethodType
    {
        Normal,
        Getter,
        Setter,
        Constructor,
        StaticConstructor,
        Enumerator,
        Async,
    }

    /// <summary>How an argument is passed, for overload disambiguation.</summary>
    public enum ArgumentType
    {
        Normal,
        Ref,
        Out,
        Pointer,
    }

    /// <summary>Which end of a method a patch applies to.</summary>
    public enum HarmonyPatchType
    {
        All,
        Prefix,
        Postfix,
        Transpiler,
        Finalizer,
        ReversePatch,
    }

    public enum HarmonyReversePatchType
    {
        Original,
        Snapshot,
    }

    /// <summary>The base every Harmony annotation derives from.</summary>
    public class HarmonyAttribute : Attribute
    {
        public HarmonyMethod info = new HarmonyMethod();
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Constructor, AllowMultiple = true)]
    public class HarmonyPatch : HarmonyAttribute
    {
        public HarmonyPatch() { }
        public HarmonyPatch(Type declaringType) { info.declaringType = declaringType; }
        public HarmonyPatch(Type declaringType, Type[] argumentTypes) { info.declaringType = declaringType; info.argumentTypes = argumentTypes; }
        public HarmonyPatch(Type declaringType, string methodName) { info.declaringType = declaringType; info.methodName = methodName; }
        public HarmonyPatch(Type declaringType, string methodName, params Type[] argumentTypes) { info.declaringType = declaringType; info.methodName = methodName; info.argumentTypes = argumentTypes; }
        public HarmonyPatch(Type declaringType, string methodName, Type[] argumentTypes, ArgumentType[] argumentVariations) { info.declaringType = declaringType; info.methodName = methodName; info.argumentTypes = argumentTypes; }
        public HarmonyPatch(Type declaringType, MethodType methodType) { info.declaringType = declaringType; info.methodType = methodType; }
        public HarmonyPatch(Type declaringType, MethodType methodType, params Type[] argumentTypes) { info.declaringType = declaringType; info.methodType = methodType; info.argumentTypes = argumentTypes; }
        public HarmonyPatch(Type declaringType, MethodType methodType, Type[] argumentTypes, ArgumentType[] argumentVariations) { info.declaringType = declaringType; info.methodType = methodType; info.argumentTypes = argumentTypes; }
        // A named member reached as something other than a plain method:
        // [HarmonyPatch(typeof(Hero), "Health", MethodType.Getter)]. The
        // weaver reads the arguments by type rather than by overload, so it
        // already understands this one -- but a plugin cannot be converted at
        // all unless the constructor it was compiled against exists here.
        public HarmonyPatch(Type declaringType, string methodName, MethodType methodType) { info.declaringType = declaringType; info.methodName = methodName; info.methodType = methodType; }
        public HarmonyPatch(string methodName) { info.methodName = methodName; }
        public HarmonyPatch(string methodName, params Type[] argumentTypes) { info.methodName = methodName; info.argumentTypes = argumentTypes; }
        public HarmonyPatch(string methodName, Type[] argumentTypes, ArgumentType[] argumentVariations) { info.methodName = methodName; info.argumentTypes = argumentTypes; }
        public HarmonyPatch(string methodName, MethodType methodType) { info.methodName = methodName; info.methodType = methodType; }
        public HarmonyPatch(MethodType methodType) { info.methodType = methodType; }
        public HarmonyPatch(MethodType methodType, params Type[] argumentTypes) { info.methodType = methodType; info.argumentTypes = argumentTypes; }
        public HarmonyPatch(MethodType methodType, Type[] argumentTypes, ArgumentType[] argumentVariations) { info.methodType = methodType; info.argumentTypes = argumentTypes; }
        public HarmonyPatch(Type[] argumentTypes) { info.argumentTypes = argumentTypes; }
        public HarmonyPatch(Type[] argumentTypes, ArgumentType[] argumentVariations) { info.argumentTypes = argumentTypes; }
        public HarmonyPatch(string typeName, string methodName, MethodType methodType = MethodType.Normal) { info.declaringTypeName = typeName; info.methodName = methodName; info.methodType = methodType; }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public class HarmonyPatchAll : HarmonyAttribute { }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
    public class HarmonyDelegate : HarmonyAttribute { }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public class HarmonyPriority : HarmonyAttribute
    {
        public HarmonyPriority(int priority) { info.priority = priority; }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public class HarmonyBefore : HarmonyAttribute
    {
        public HarmonyBefore(params string[] before) { info.before = before; }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public class HarmonyAfter : HarmonyAttribute
    {
        public HarmonyAfter(params string[] after) { info.after = after; }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public class HarmonyDebug : HarmonyAttribute { }

    [AttributeUsage(AttributeTargets.Method)]
    public class HarmonyPrepare : Attribute { }

    [AttributeUsage(AttributeTargets.Method)]
    public class HarmonyCleanup : Attribute { }

    [AttributeUsage(AttributeTargets.Method)]
    public class HarmonyTargetMethod : Attribute { }

    [AttributeUsage(AttributeTargets.Method)]
    public class HarmonyTargetMethods : Attribute { }

    [AttributeUsage(AttributeTargets.Method)]
    public class HarmonyPrefix : Attribute { }

    [AttributeUsage(AttributeTargets.Method)]
    public class HarmonyPostfix : Attribute { }

    [AttributeUsage(AttributeTargets.Method)]
    public class HarmonyTranspiler : Attribute { }

    [AttributeUsage(AttributeTargets.Method)]
    public class HarmonyFinalizer : Attribute { }

    [AttributeUsage(AttributeTargets.Method)]
    public class HarmonyReversePatch : Attribute
    {
        public HarmonyReversePatch(HarmonyReversePatchType type = HarmonyReversePatchType.Original) { }
    }

    [AttributeUsage(AttributeTargets.Parameter)]
    public class HarmonyArgument : Attribute
    {
        public string OriginalName { get; private set; }
        public int Index { get; private set; }
        public string NewName { get; private set; }

        public HarmonyArgument(string originalName) : this(originalName, null) { }
        public HarmonyArgument(int index) : this(index, null) { }

        public HarmonyArgument(string originalName, string newName)
        {
            OriginalName = originalName;
            Index = -1;
            NewName = newName;
        }

        public HarmonyArgument(int index, string name)
        {
            OriginalName = null;
            Index = index;
            NewName = name;
        }
    }

    /// <summary>
    /// A target, as an object rather than as an attribute.
    ///
    /// Plugins build these to pass to Harmony.Patch at runtime. Nothing here
    /// acts on one -- a target chosen at runtime is precisely what a build-time
    /// weaver cannot follow -- but the type has to exist for the plugin to
    /// resolve, and mod-weaver reports any plugin that relies on it.
    /// </summary>
    public class HarmonyMethod
    {
        public System.Reflection.MethodInfo method;
        public Type declaringType;
        public string declaringTypeName;
        public string methodName;
        public MethodType? methodType;
        public Type[] argumentTypes;
        public int? priority = -1;
        public string[] before;
        public string[] after;
        public bool? debug;

        public HarmonyMethod() { }

        public HarmonyMethod(System.Reflection.MethodInfo method)
        {
            this.method = method;
        }

        public HarmonyMethod(Type type, string name, Type[] parameters = null)
        {
            declaringType = type;
            methodName = name;
            argumentTypes = parameters;
        }
    }
}

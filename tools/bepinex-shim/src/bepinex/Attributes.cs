// The attributes a BepInEx plugin declares itself with, and the metadata the
// loader reads back out of them.
//
// Same reasoning as the Harmony attributes: a plugin DLL references these by
// name, so they must exist with these shapes for the assembly to resolve. The
// difference is that these ones are also read at runtime -- Chainloader finds
// plugins by looking for [BepInPlugin], exactly as BepInEx does.

using System;

namespace BepInEx
{
    /// <summary>Marks a class as a plugin, and names it.</summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class BepInPlugin : Attribute
    {
        public string GUID { get; protected set; }
        public string Name { get; protected set; }

        /// <summary>
        /// A System.Version, as in BepInEx, though it is written as a string
        /// in the attribute. A plugin that prints its own version -- or lists
        /// everyone else's, as a settings UI does -- reads it as this type,
        /// and a string here is a member such a plugin cannot resolve.
        /// </summary>
        public Version Version { get; protected set; }

        public BepInPlugin(string GUID, string Name, string Version)
        {
            this.GUID = GUID;
            this.Name = Name;
            this.Version = ParseVersion(Version);
        }

        /// <summary>
        /// Never throws, and keeps the shape it was given: "2.0.4" is a
        /// three-part version, not 2.0.4.0. Mod versions in the wild are
        /// "1.0", "2.0.4", "1.2.3.4.5" and "v1.0-beta", and a plugin that
        /// cannot be described is worse than one described as 0.0.
        /// </summary>
        static Version ParseVersion(string text)
        {
            var parts = (text ?? "").Trim().TrimStart('v', 'V').Split('.');
            var numbers = new int[4];
            var used = 0;
            foreach (var part in parts)
            {
                if (used == 4) break;
                var digits = 0;
                while (digits < part.Length && part[digits] >= '0' && part[digits] <= '9') digits++;
                if (digits == 0) break;
                int value;
                if (!int.TryParse(part.Substring(0, digits), out value)) break;
                numbers[used++] = value;
                // "1.0-beta" ends the version at the part that stopped being
                // a number, rather than throwing the whole thing away.
                if (digits != part.Length) break;
            }
            if (used == 0) return new Version(0, 0);
            if (used == 1) return new Version(numbers[0], 0);
            if (used == 2) return new Version(numbers[0], numbers[1]);
            if (used == 3) return new Version(numbers[0], numbers[1], numbers[2]);
            return new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
        }
    }

    /// <summary>
    /// Another plugin this one needs, or would like.
    ///
    /// Honoured for load order. Not honoured as a hard failure: on a PC a
    /// missing hard dependency stops the plugin loading, but here the whole
    /// set was already fixed when the game was built, and refusing to start
    /// something at that point helps nobody.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class BepInDependency : Attribute
    {
        /// <summary>
        /// Nested, because that is where BepInEx puts it.
        ///
        /// A plugin naming this type was compiled against
        /// BepInEx.BepInDependency/DependencyFlags, and a top-level
        /// BepInEx.DependencyFlags is a different type with the same name --
        /// which the plugin cannot resolve, however identical the members are.
        /// </summary>
        [Flags]
        public enum DependencyFlags
        {
            HardDependency = 1,
            SoftDependency = 2,
        }

        public string DependencyGUID { get; protected set; }
        public DependencyFlags Flags { get; protected set; }
        public string MinimumVersion { get; protected set; }

        public BepInDependency(string DependencyGUID, DependencyFlags Flags = DependencyFlags.HardDependency)
        {
            this.DependencyGUID = DependencyGUID;
            this.Flags = Flags;
            MinimumVersion = "";
        }

        public BepInDependency(string DependencyGUID, string MinimumDependencyVersion)
            : this(DependencyGUID)
        {
            MinimumVersion = MinimumDependencyVersion;
        }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class BepInIncompatibility : Attribute
    {
        public string IncompatibilityGUID { get; protected set; }

        public BepInIncompatibility(string IncompatibilityGUID)
        {
            this.IncompatibilityGUID = IncompatibilityGUID;
        }
    }

    /// <summary>
    /// Restricts a plugin to a named executable. There is one process here and
    /// it is the game, so this is recorded and ignored.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class BepInProcess : Attribute
    {
        public string ProcessName { get; protected set; }

        public BepInProcess(string ProcessName)
        {
            this.ProcessName = ProcessName;
        }
    }

    /// <summary>What Chainloader knows about one loaded plugin.</summary>
    public class PluginInfo
    {
        public BepInPlugin Metadata { get; internal set; }
        public string Location { get; internal set; }

        /// <summary>
        /// The running plugin. Typed, not object: a plugin compiled against
        /// BepInEx calls this expecting a BaseUnityPlugin back, and a
        /// signature that does not match is a member it cannot resolve.
        /// </summary>
        public BaseUnityPlugin Instance { get; internal set; }
        public Version TargettedBepInExVersion { get; internal set; }

        internal PluginInfo(BepInPlugin metadata, string location)
        {
            Metadata = metadata;
            Location = location;
            TargettedBepInExVersion = new Version(5, 4, 21);
        }
    }
}

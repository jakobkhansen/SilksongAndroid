// Plugin configuration.
//
// Config files keep the shape BepInEx uses on a PC -- INI-with-comments, one
// .cfg per plugin GUID, sections and keys -- so that a config a user already
// has, or one written by the Configuration Manager on desktop, drops straight
// in. They are the one part of a mod that stays editable after the build: the
// game reads them on startup, so changing a value costs a relaunch rather than
// the seventeen minutes a rebuild costs.
//
// Defaults are written back on first run, which is what makes a fresh mod
// discoverable: install it, launch once, and the file listing every setting
// with its type and default is sitting in mods/config.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace BepInEx.Configuration
{
    public class ConfigDefinition : IEquatable<ConfigDefinition>
    {
        public string Section { get; private set; }
        public string Key { get; private set; }

        public ConfigDefinition(string section, string key)
        {
            Section = section;
            Key = key;
        }

        public bool Equals(ConfigDefinition other)
        {
            return other != null && string.Equals(Key, other.Key) && string.Equals(Section, other.Section);
        }

        public override bool Equals(object obj) { return Equals(obj as ConfigDefinition); }

        public override int GetHashCode()
        {
            return ((Section ?? "").GetHashCode() * 397) ^ (Key ?? "").GetHashCode();
        }

        public override string ToString() { return Section + "." + Key; }
    }

    public abstract class AcceptableValueBase
    {
        public Type ValueType { get; private set; }

        protected AcceptableValueBase(Type valueType) { ValueType = valueType; }

        public abstract object Clamp(object value);
        public abstract bool IsValid(object value);
        public abstract string ToDescriptionString();
    }

    public class AcceptableValueRange<T> : AcceptableValueBase where T : IComparable
    {
        public T MinValue { get; private set; }
        public T MaxValue { get; private set; }

        public AcceptableValueRange(T minValue, T maxValue) : base(typeof(T))
        {
            MinValue = minValue;
            MaxValue = maxValue;
        }

        public override object Clamp(object value)
        {
            if (MinValue.CompareTo(value) > 0) return MinValue;
            if (MaxValue.CompareTo(value) < 0) return MaxValue;
            return value;
        }

        public override bool IsValid(object value)
        {
            return MinValue.CompareTo(value) <= 0 && MaxValue.CompareTo(value) >= 0;
        }

        public override string ToDescriptionString()
        {
            return "# Acceptable value range: From " + MinValue + " to " + MaxValue;
        }
    }

    public class AcceptableValueList<T> : AcceptableValueBase where T : IEquatable<T>
    {
        public T[] AcceptableValues { get; private set; }

        public AcceptableValueList(params T[] acceptableValues) : base(typeof(T))
        {
            AcceptableValues = acceptableValues;
        }

        public override object Clamp(object value)
        {
            return IsValid(value) ? value : AcceptableValues[0];
        }

        public override bool IsValid(object value)
        {
            return value is T && AcceptableValues.Any(v => v.Equals((T)value));
        }

        public override string ToDescriptionString()
        {
            return "# Acceptable values: " + string.Join(", ", AcceptableValues.Select(v => v.ToString()).ToArray());
        }
    }

    public class ConfigDescription
    {
        public string Description { get; private set; }
        public AcceptableValueBase AcceptableValues { get; private set; }
        public object[] Tags { get; private set; }

        public ConfigDescription(string description, AcceptableValueBase acceptableValues = null, params object[] tags)
        {
            Description = description;
            AcceptableValues = acceptableValues;
            Tags = tags ?? new object[0];
        }

        public static readonly ConfigDescription Empty = new ConfigDescription("");
    }

    public abstract class ConfigEntryBase
    {
        public ConfigFile ConfigFile { get; private set; }
        public ConfigDefinition Definition { get; private set; }
        public ConfigDescription Description { get; private set; }
        public Type SettingType { get; private set; }
        public object DefaultValue { get; private set; }

        public abstract object BoxedValue { get; set; }

        protected ConfigEntryBase(ConfigFile configFile, ConfigDefinition definition, Type settingType,
            object defaultValue, ConfigDescription configDescription)
        {
            ConfigFile = configFile;
            Definition = definition;
            SettingType = settingType;
            DefaultValue = defaultValue;
            Description = configDescription ?? ConfigDescription.Empty;
        }

        public string GetSerializedValue() { return TomlTypeConverter.ConvertToString(BoxedValue, SettingType); }

        public void SetSerializedValue(string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            object parsed;
            if (TomlTypeConverter.TryConvertToValue(value, SettingType, out parsed)) BoxedValue = parsed;
        }

        protected void ClampValue(ref object value)
        {
            if (Description.AcceptableValues != null) value = Description.AcceptableValues.Clamp(value);
        }

        protected void OnSettingChanged(object sender)
        {
            ConfigFile.OnSettingChanged(sender, this);
        }

        internal void WriteDescription(StringBuilder builder)
        {
            if (!string.IsNullOrEmpty(Description.Description))
                foreach (var line in Description.Description.Split('\n'))
                    builder.Append("## ").Append(line.TrimEnd('\r')).Append('\n');

            builder.Append("# Setting type: ").Append(SettingType.Name).Append('\n');
            builder.Append("# Default value: ")
                .Append(TomlTypeConverter.ConvertToString(DefaultValue, SettingType)).Append('\n');

            if (Description.AcceptableValues != null)
                builder.Append(Description.AcceptableValues.ToDescriptionString()).Append('\n');
            else if (SettingType.IsEnum)
                builder.Append("# Acceptable values: ")
                    .Append(string.Join(", ", Enum.GetNames(SettingType))).Append('\n');
        }
    }

    public sealed class ConfigEntry<T> : ConfigEntryBase
    {
        T _value;

        public event EventHandler SettingChanged;

        public T Value
        {
            get { return _value; }
            set
            {
                object boxed = value;
                ClampValue(ref boxed);
                if (Equals(_value, boxed)) return;
                _value = (T)boxed;
                var handler = SettingChanged;
                if (handler != null) handler(this, EventArgs.Empty);
                OnSettingChanged(this);
            }
        }

        public override object BoxedValue
        {
            get { return _value; }
            set { Value = (T)value; }
        }

        internal ConfigEntry(ConfigFile configFile, ConfigDefinition definition, T defaultValue,
            ConfigDescription configDescription)
            : base(configFile, definition, typeof(T), defaultValue, configDescription)
        {
            _value = defaultValue;
        }
    }

    /// <summary>
    /// String to value and back. Named for the BepInEx type a few plugins
    /// reach into directly; the conversions are the ones its .cfg format uses.
    /// </summary>
    public static class TomlTypeConverter
    {
        public static string ConvertToString(object value, Type valueType)
        {
            if (value == null) return "";
            if (valueType == typeof(bool)) return ((bool)value) ? "true" : "false";
            if (value is IFormattable && !(value is Enum))
                return ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture);
            return value.ToString();
        }

        public static bool TryConvertToValue(string text, Type valueType, out object value)
        {
            value = null;
            if (text == null) return false;
            text = text.Trim();

            try
            {
                if (valueType == typeof(string)) { value = text; return true; }
                if (valueType.IsEnum) { value = Enum.Parse(valueType, text, true); return true; }
                if (valueType == typeof(bool))
                {
                    value = text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1";
                    return true;
                }
                if (valueType == typeof(KeyboardShortcut))
                {
                    value = KeyboardShortcut.Deserialize(text);
                    return true;
                }
                value = Convert.ChangeType(text, valueType, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static object ConvertToValue(string text, Type valueType)
        {
            object value;
            if (!TryConvertToValue(text, valueType, out value))
                throw new InvalidOperationException("Cannot convert \"" + text + "\" to " + valueType.Name);
            return value;
        }

        /// <summary>
        /// Whether a setting of this type can be stored in a .cfg at all.
        ///
        /// The list is what <see cref="ConvertToString"/> and
        /// <see cref="TryConvertToValue"/> above actually round-trip, and it is
        /// what <see cref="ConfigFile.Bind{T}(ConfigDefinition,T,ConfigDescription)"/>
        /// refuses on.
        /// </summary>
        public static bool CanConvert(Type type)
        {
            return type != null
                && (type.IsEnum
                    || type == typeof(string)
                    || type == typeof(bool)
                    || type == typeof(byte) || type == typeof(sbyte)
                    || type == typeof(short) || type == typeof(ushort)
                    || type == typeof(int) || type == typeof(uint)
                    || type == typeof(long) || type == typeof(ulong)
                    || type == typeof(float) || type == typeof(double) || type == typeof(decimal)
                    || type == typeof(char)
                    || type == typeof(KeyboardShortcut));
        }

        public static IEnumerable<Type> GetSupportedTypes()
        {
            return new[]
            {
                typeof(string), typeof(bool), typeof(byte), typeof(sbyte), typeof(short), typeof(ushort),
                typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double),
                typeof(decimal), typeof(char), typeof(KeyboardShortcut),
            };
        }

        /// <summary>
        /// The pair of functions for one type, or null if there is no such pair.
        ///
        /// A settings UI asks for this rather than calling the methods above:
        /// null is how it decides that a setting of some exotic type can be
        /// shown but not edited. The two delegates close over nothing, so one
        /// converter per type is made once and handed out.
        /// </summary>
        public static TypeConverter GetConverter(Type type)
        {
            if (!CanConvert(type)) return null;

            lock (_converters)
            {
                TypeConverter converter;
                if (!_converters.TryGetValue(type, out converter))
                {
                    converter = new TypeConverter
                    {
                        ConvertToString = ConvertToString,
                        ConvertToObject = ConvertToValue,
                    };
                    _converters[type] = converter;
                }
                return converter;
            }
        }

        static readonly Dictionary<Type, TypeConverter> _converters = new Dictionary<Type, TypeConverter>();
    }

    /// <summary>
    /// One type's string form, as a pair of functions.
    ///
    /// BepInEx's own registry of these is extensible; this one is not, because
    /// the conversions here are fixed by what the .cfg format can hold. The
    /// type exists because a plugin that draws settings asks for it by name.
    /// </summary>
    public class TypeConverter
    {
        public Func<object, Type, string> ConvertToString { get; set; }
        public Func<string, Type, object> ConvertToObject { get; set; }
    }

    /// <summary>
    /// A key combination. Carried so that plugins binding one still compile and
    /// still read their config; the polling side depends on the input backend
    /// the player was built with and is reported, once, if it is unavailable.
    /// </summary>
    public struct KeyboardShortcut : IEquatable<KeyboardShortcut>
    {
        public UnityEngine.KeyCode MainKey { get; private set; }
        public UnityEngine.KeyCode[] Modifiers { get; private set; }

        public static readonly KeyboardShortcut Empty = new KeyboardShortcut();

        public KeyboardShortcut(UnityEngine.KeyCode mainKey, params UnityEngine.KeyCode[] modifiers)
        {
            MainKey = mainKey;
            Modifiers = modifiers ?? new UnityEngine.KeyCode[0];
        }

        public UnityEngine.KeyCode[] GetAllKeyCodes()
        {
            if (MainKey == UnityEngine.KeyCode.None) return new UnityEngine.KeyCode[0];
            var all = new List<UnityEngine.KeyCode> { MainKey };
            all.AddRange(Modifiers ?? new UnityEngine.KeyCode[0]);
            return all.ToArray();
        }

        public static KeyboardShortcut Deserialize(string text)
        {
            try
            {
                var parts = (text ?? "").Split('+')
                    .Select(p => p.Trim())
                    .Where(p => p.Length > 0)
                    .Select(p => (UnityEngine.KeyCode)Enum.Parse(typeof(UnityEngine.KeyCode), p, true))
                    .ToArray();
                if (parts.Length == 0) return Empty;
                return new KeyboardShortcut(parts[0], parts.Skip(1).ToArray());
            }
            catch (Exception)
            {
                return Empty;
            }
        }

        public string Serialize() { return ToString(); }

        public override string ToString()
        {
            if (MainKey == UnityEngine.KeyCode.None) return "Not set";
            return string.Join(" + ", GetAllKeyCodes().Select(k => k.ToString()).ToArray());
        }

        public bool IsDown() { return Poll(false); }
        public bool IsUp() { return false; }
        public bool IsPressed() { return Poll(true); }

        bool Poll(bool held)
        {
            if (MainKey == UnityEngine.KeyCode.None) return false;

            // Through UnityInput rather than UnityEngine.Input: a player built
            // against the new input system throws from every legacy call, and
            // that decision belongs in one place.
            var input = UnityInput.Current;
            foreach (var modifier in Modifiers ?? new UnityEngine.KeyCode[0])
                if (!input.GetKey(modifier)) return false;
            return held ? input.GetKey(MainKey) : input.GetKeyDown(MainKey);
        }

        public bool Equals(KeyboardShortcut other)
        {
            return MainKey == other.MainKey &&
                GetAllKeyCodes().Length == other.GetAllKeyCodes().Length &&
                GetAllKeyCodes().SequenceEqual(other.GetAllKeyCodes());
        }

        public override bool Equals(object obj)
        {
            return obj is KeyboardShortcut && Equals((KeyboardShortcut)obj);
        }

        public override int GetHashCode()
        {
            var hash = (int)MainKey;
            foreach (var key in Modifiers ?? new UnityEngine.KeyCode[0]) hash = (hash * 397) ^ (int)key;
            return hash;
        }
    }

    public sealed class SettingChangedEventArgs : EventArgs
    {
        public ConfigEntryBase ChangedSetting { get; private set; }

        public SettingChangedEventArgs(ConfigEntryBase changedSetting)
        {
            ChangedSetting = changedSetting;
        }
    }

    /// <summary>
    /// One plugin's settings, backed by one .cfg file.
    ///
    /// A dictionary, as BepInEx's is, and for one reason: a settings UI is
    /// handed a plugin's <see cref="ConfigFile"/> and iterates it. Everything
    /// else here would work as an opaque object.
    /// </summary>
    public class ConfigFile : IDictionary<ConfigDefinition, ConfigEntryBase>
    {
        readonly Dictionary<ConfigDefinition, ConfigEntryBase> _entries =
            new Dictionary<ConfigDefinition, ConfigEntryBase>();

        /// <summary>
        /// Values read from disk that no plugin has claimed yet.
        ///
        /// They have to survive a save, and that is not a detail. A plugin
        /// that binds a setting only in certain circumstances -- one per save
        /// slot, say -- has every OTHER slot's value sitting here unclaimed,
        /// and a save that dropped them would quietly destroy the settings for
        /// every save the player is not currently in.
        /// </summary>
        readonly Dictionary<ConfigDefinition, string> _orphans = new Dictionary<ConfigDefinition, string>();

        public Dictionary<ConfigDefinition, string> OrphanedEntries
        {
            get { lock (_lock) return new Dictionary<ConfigDefinition, string>(_orphans); }
        }

        readonly object _lock = new object();

        static ConfigFile _core;

        /// <summary>
        /// The loader's own settings, as opposed to a plugin's.
        ///
        /// Internal and static because that is exactly how a settings UI looks
        /// for it -- BepInEx's own is `internal static`, and Configuration
        /// Manager fetches it by name with BindingFlags.NonPublic | Static and
        /// logs an error when it is not there. Naming it the same means the
        /// one setting in it, the chord that opens the window, is listed in
        /// the window it opens.
        /// </summary>
        internal static ConfigFile CoreConfig
        {
            get
            {
                if (_core == null) _core = new ConfigFile(Path.Combine(Paths.ConfigPath, "BepInEx.cfg"), true);
                return _core;
            }
        }

        public string ConfigFilePath { get; private set; }

        /// <summary>Write the file back whenever a value changes.</summary>
        public bool SaveOnConfigSet { get; set; }

        public event EventHandler<SettingChangedEventArgs> SettingChanged;
        public event EventHandler ConfigReloaded;

        public ICollection<ConfigDefinition> Keys { get { lock (_lock) return _entries.Keys.ToArray(); } }

        public ICollection<ConfigEntryBase> Values { get { lock (_lock) return _entries.Values.ToArray(); } }

        /// <summary>Every setting, as an array. BepInEx's own name for it.</summary>
        public ConfigEntryBase[] GetConfigEntries() { lock (_lock) return _entries.Values.ToArray(); }

        public ReadOnlyCollection<ConfigDefinition> ConfigDefinitions
        {
            get { lock (_lock) return new ReadOnlyCollection<ConfigDefinition>(_entries.Keys.ToArray()); }
        }

        public int Count { get { lock (_lock) return _entries.Count; } }

        public bool IsReadOnly { get { return false; } }

        public ConfigFile(string configPath, bool saveOnInit) : this(configPath, saveOnInit, null) { }

        public ConfigFile(string configPath, bool saveOnInit, BepInPlugin ownerMetadata)
        {
            ConfigFilePath = configPath;
            OwnerMetadata = ownerMetadata;
            SaveOnConfigSet = true;

            if (File.Exists(ConfigFilePath)) Reload();
            else if (saveOnInit) Save();
        }

        public BepInPlugin OwnerMetadata { get; private set; }

        public ConfigEntry<T> Bind<T>(ConfigDefinition definition, T defaultValue, ConfigDescription configDescription = null)
        {
            if (!TomlTypeConverter.CanConvert(typeof(T)))
                throw new ArgumentException("Type " + typeof(T).Name + " is not supported by the config system.");

            lock (_lock)
            {
                ConfigEntryBase existing;
                if (_entries.TryGetValue(definition, out existing)) return (ConfigEntry<T>)existing;

                var entry = new ConfigEntry<T>(this, definition, defaultValue, configDescription);
                _entries[definition] = entry;

                string raw;
                if (_orphans.TryGetValue(definition, out raw))
                {
                    _orphans.Remove(definition);
                    entry.SetSerializedValue(raw);
                }

                if (SaveOnConfigSet) Save();
                return entry;
            }
        }

        public ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, ConfigDescription configDescription = null)
        {
            return Bind(new ConfigDefinition(section, key), defaultValue, configDescription);
        }

        public ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, string description)
        {
            return Bind(new ConfigDefinition(section, key), defaultValue,
                string.IsNullOrEmpty(description) ? null : new ConfigDescription(description));
        }

        public bool TryGetEntry<T>(ConfigDefinition definition, out ConfigEntry<T> entry)
        {
            lock (_lock)
            {
                ConfigEntryBase found;
                if (_entries.TryGetValue(definition, out found))
                {
                    entry = found as ConfigEntry<T>;
                    return entry != null;
                }
                entry = null;
                return false;
            }
        }

        public bool TryGetEntry<T>(string section, string key, out ConfigEntry<T> entry)
        {
            return TryGetEntry(new ConfigDefinition(section, key), out entry);
        }

        public bool Remove(ConfigDefinition definition)
        {
            lock (_lock) return _entries.Remove(definition);
        }

        public void Clear()
        {
            lock (_lock) _entries.Clear();
        }

        /// <summary>
        /// Get only, as in BepInEx. An entry is made by <see cref="Bind{T}"/>,
        /// which is what gives it a type, a default and a description; one
        /// assigned here would have none of those and would be written back to
        /// the file as a line nothing can read.
        /// </summary>
        public ConfigEntryBase this[ConfigDefinition key]
        {
            get { lock (_lock) return _entries[key]; }
        }

        ConfigEntryBase IDictionary<ConfigDefinition, ConfigEntryBase>.this[ConfigDefinition key]
        {
            get { return this[key]; }
            set { throw new InvalidOperationException("Use Bind instead of setting entries directly"); }
        }

        public ConfigEntryBase this[string section, string key]
        {
            get { return this[new ConfigDefinition(section, key)]; }
        }

        public bool ContainsKey(ConfigDefinition key)
        {
            lock (_lock) return _entries.ContainsKey(key);
        }

        public void Add(ConfigDefinition key, ConfigEntryBase value)
        {
            throw new InvalidOperationException("Use Bind instead of adding entries directly");
        }

        /// <summary>
        /// A snapshot, deliberately: a plugin binding a new setting while
        /// something else walks the file is ordinary, and would otherwise
        /// invalidate the enumerator.
        /// </summary>
        public IEnumerator<KeyValuePair<ConfigDefinition, ConfigEntryBase>> GetEnumerator()
        {
            lock (_lock) return ((IEnumerable<KeyValuePair<ConfigDefinition, ConfigEntryBase>>)_entries.ToArray()).GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { return GetEnumerator(); }

        public bool Contains(KeyValuePair<ConfigDefinition, ConfigEntryBase> item)
        {
            lock (_lock)
            {
                ConfigEntryBase found;
                return _entries.TryGetValue(item.Key, out found) && Equals(found, item.Value);
            }
        }

        bool IDictionary<ConfigDefinition, ConfigEntryBase>.TryGetValue(ConfigDefinition key, out ConfigEntryBase value)
        {
            lock (_lock) return _entries.TryGetValue(key, out value);
        }

        void ICollection<KeyValuePair<ConfigDefinition, ConfigEntryBase>>.Add(
            KeyValuePair<ConfigDefinition, ConfigEntryBase> item)
        {
            Add(item.Key, item.Value);
        }

        /// <summary>
        /// One lock, not two: between a Contains and a Remove taken separately
        /// another thread can replace the entry, and this would then remove
        /// the replacement -- something the caller never asked to remove.
        /// </summary>
        bool ICollection<KeyValuePair<ConfigDefinition, ConfigEntryBase>>.Remove(
            KeyValuePair<ConfigDefinition, ConfigEntryBase> item)
        {
            lock (_lock)
            {
                ConfigEntryBase found;
                if (!_entries.TryGetValue(item.Key, out found) || !Equals(found, item.Value)) return false;
                return _entries.Remove(item.Key);
            }
        }

        void ICollection<KeyValuePair<ConfigDefinition, ConfigEntryBase>>.CopyTo(
            KeyValuePair<ConfigDefinition, ConfigEntryBase>[] array, int arrayIndex)
        {
            lock (_lock) ((ICollection<KeyValuePair<ConfigDefinition, ConfigEntryBase>>)_entries).CopyTo(array, arrayIndex);
        }

        public void Reload()
        {
            try
            {
                lock (_lock)
                {
                    _orphans.Clear();
                    var section = "";
                    foreach (var rawLine in File.ReadAllLines(ConfigFilePath))
                    {
                        var line = rawLine.Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;

                        if (line.StartsWith("[") && line.EndsWith("]"))
                        {
                            section = line.Substring(1, line.Length - 2).Trim();
                            continue;
                        }

                        var split = line.IndexOf('=');
                        if (split < 0) continue;

                        var definition = new ConfigDefinition(section, line.Substring(0, split).Trim());
                        var value = line.Substring(split + 1).Trim();

                        ConfigEntryBase entry;
                        if (_entries.TryGetValue(definition, out entry)) entry.SetSerializedValue(value);
                        else _orphans[definition] = value;
                    }
                }

                var handler = ConfigReloaded;
                if (handler != null) handler(this, EventArgs.Empty);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[BepInEx] Could not read " + ConfigFilePath + ": " + e.Message);
            }
        }

        public void Save()
        {
            try
            {
                lock (_lock)
                {
                    var directory = Path.GetDirectoryName(ConfigFilePath);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                    var builder = new StringBuilder();
                    if (OwnerMetadata != null)
                    {
                        builder.Append("## Settings file was created by plugin ")
                            .Append(OwnerMetadata.Name).Append(" v").Append(OwnerMetadata.Version).Append('\n');
                        builder.Append("## Plugin GUID: ").Append(OwnerMetadata.GUID).Append('\n');
                        builder.Append('\n');
                    }

                    // Bound entries and orphans together, in one pass, exactly
                    // as BepInEx writes them: an orphan is an ordinary line in
                    // its own section, indistinguishable from a bound one.
                    //
                    // It used to be written as "# Section.Key = Value" after
                    // everything else, which looked like a tidy way to keep a
                    // value nobody had claimed. It was not: Reload skips lines
                    // beginning with #, so every unclaimed value was destroyed
                    // by the first save after it was read. A plugin that binds
                    // its settings per save slot -- which is a real thing that
                    // real plugins do -- had the settings for every slot but
                    // the open one silently erased.
                    var lines = new List<KeyValuePair<ConfigDefinition, ConfigEntryBase>>(_entries);
                    var orphaned = new Dictionary<ConfigDefinition, string>(_orphans);
                    foreach (var orphan in orphaned)
                        lines.Add(new KeyValuePair<ConfigDefinition, ConfigEntryBase>(orphan.Key, null));

                    foreach (var section in lines.Select(e => e.Key.Section).Distinct()
                        .OrderBy(s => s, StringComparer.Ordinal))
                    {
                        builder.Append('[').Append(section).Append(']').Append('\n').Append('\n');

                        foreach (var pair in lines.Where(e => e.Key.Section == section)
                            .OrderBy(e => e.Key.Key, StringComparer.Ordinal))
                        {
                            string value;
                            if (pair.Value != null)
                            {
                                pair.Value.WriteDescription(builder);
                                value = pair.Value.GetSerializedValue();
                            }
                            else
                            {
                                // No description: nothing has claimed it, so
                                // nothing knows its type or its default.
                                value = orphaned[pair.Key];
                            }
                            builder.Append(pair.Key.Key).Append(" = ").Append(value).Append('\n').Append('\n');
                        }
                    }

                    File.WriteAllText(ConfigFilePath, builder.ToString());
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[BepInEx] Could not write " + ConfigFilePath + ": " + e.Message);
            }
        }

        internal void OnSettingChanged(object sender, ConfigEntryBase changedEntry)
        {
            var handler = SettingChanged;
            if (handler != null) handler(sender, new SettingChangedEventArgs(changedEntry));
            if (SaveOnConfigSet) Save();
        }
    }
}

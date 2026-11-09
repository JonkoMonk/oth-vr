using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace OthVr;

// Our own settings file, so the mod behaves the same under BepInEx and MelonLoader.
// Same API shape (Bind / Value / Save / Reload) and same file format as BepInEx's ConfigFile,
// so cfg files written by older versions keep working and the README stays valid.
// Like BepInEx: setting a Value saves the file; unknown keys in the file are kept.
public sealed class ConfigFile
{
    readonly object _lock = new();
    readonly List<IEntry> _entries = new();
    readonly Dictionary<(string, string), string> _fileValues = new();
    bool _loading;

    public string ConfigFilePath { get; }

    public ConfigFile(string path)
    {
        ConfigFilePath = path;
        Reload();
    }

    public ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, string description)
    {
        lock (_lock)
        {
            var existing = _entries.OfType<ConfigEntry<T>>().FirstOrDefault(e => e.Definition.Section == section && e.Definition.Key == key);
            if (existing != null) return existing;
            var entry = new ConfigEntry<T>(this, new ConfigDefinition(section, key), defaultValue, description);
            _entries.Add(entry);
            if (_fileValues.TryGetValue((section, key), out var raw)) ((IEntry)entry).SetFromText(raw);
            Save();
            return entry;
        }
    }

    public void Reload()
    {
        lock (_lock)
        {
            _fileValues.Clear();
            if (!File.Exists(ConfigFilePath)) return;
            string section = "";
            foreach (var rawLine in File.ReadAllLines(ConfigFilePath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                if (line.StartsWith("[") && line.EndsWith("]")) { section = line.Substring(1, line.Length - 2).Trim(); continue; }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                _fileValues[(section, line.Substring(0, eq).Trim())] = line.Substring(eq + 1).Trim();
            }
            _loading = true;
            try
            {
                foreach (var e in _entries)
                    if (_fileValues.TryGetValue((e.Definition.Section, e.Definition.Key), out var raw)) e.SetFromText(raw);
            }
            finally { _loading = false; }
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            if (_loading) return;
            var sb = new StringBuilder();
            sb.Append($"## Settings file was created by plugin {Plugin.Name} v{Plugin.Version}\r\n");
            sb.Append($"## Plugin GUID: {Plugin.Guid}\r\n\r\n");

            var bound = new HashSet<(string, string)>(_entries.Select(e => (e.Definition.Section, e.Definition.Key)));
            var sections = _entries.Select(e => e.Definition.Section).Concat(_fileValues.Keys.Select(k => k.Item1)).Distinct().OrderBy(s => s, StringComparer.Ordinal);
            foreach (var section in sections)
            {
                sb.Append($"[{section}]\r\n\r\n");
                foreach (var e in _entries.Where(e => e.Definition.Section == section))
                {
                    if (!string.IsNullOrEmpty(e.Description))
                        foreach (var d in e.Description.Split('\n')) sb.Append($"## {d.TrimEnd('\r')}\r\n");
                    sb.Append($"# Setting type: {e.SettingType.Name}\r\n");
                    sb.Append($"# Default value: {e.DefaultText}\r\n");
                    if (e.SettingType.IsEnum) sb.Append($"# Acceptable values: {string.Join(", ", Enum.GetNames(e.SettingType))}\r\n");
                    sb.Append($"{e.Definition.Key} = {e.ValueText}\r\n\r\n");
                }
                // Keys from the file that nothing binds (yet): keep them, like BepInEx does.
                foreach (var kv in _fileValues.Where(kv => kv.Key.Item1 == section && !bound.Contains(kv.Key)))
                    sb.Append($"{kv.Key.Item2} = {kv.Value}\r\n\r\n");
            }
            var dir = Path.GetDirectoryName(ConfigFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(ConfigFilePath, sb.ToString(), new UTF8Encoding(false));
        }
    }

    internal interface IEntry
    {
        ConfigDefinition Definition { get; }
        string Description { get; }
        Type SettingType { get; }
        string DefaultText { get; }
        string ValueText { get; }
        void SetFromText(string raw);
    }

    // Text conversion, matching BepInEx's TomlTypeConverter for the types the mod uses.
    internal static string ToText(object v) => v switch
    {
        null => "",
        string s => Escape(s),
        bool b => b ? "true" : "false",
        float f => f.ToString(NumberFormatInfo.InvariantInfo),
        double d => d.ToString(NumberFormatInfo.InvariantInfo),
        int i => i.ToString(NumberFormatInfo.InvariantInfo),
        _ => v.ToString(),
    };

    internal static object FromText(string s, Type t)
    {
        if (t == typeof(string)) return Unescape(s);
        if (t == typeof(bool)) return bool.Parse(s);
        if (t == typeof(float)) return float.Parse(s, NumberFormatInfo.InvariantInfo);
        if (t == typeof(double)) return double.Parse(s, NumberFormatInfo.InvariantInfo);
        if (t == typeof(int)) return int.Parse(s, NumberFormatInfo.InvariantInfo);
        if (t.IsEnum) return Enum.Parse(t, s, true);
        throw new NotSupportedException($"setting type {t.Name}");
    }

    static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        foreach (char c in s)
            sb.Append(c switch
            {
                '\0' => @"\0", '\a' => @"\a", '\b' => @"\b", '\t' => @"\t", '\n' => @"\n", '\v' => @"\v", '\f' => @"\f", '\r' => @"\r",
                '\'' => @"\'", '\\' => @"\\", '"' => "\\\"",
                _ => c.ToString(),
            });
        return sb.ToString();
    }

    static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
            char n = s[++i];
            sb.Append(n switch
            {
                '0' => '\0', 'a' => '\a', 'b' => '\b', 't' => '\t', 'n' => '\n', 'v' => '\v', 'f' => '\f', 'r' => '\r',
                _ => n,   // \' \\ \" and anything unknown: the character itself
            });
        }
        return sb.ToString();
    }
}

public sealed class ConfigDefinition
{
    public string Section { get; }
    public string Key { get; }
    public ConfigDefinition(string section, string key) { Section = section; Key = key; }
}

public sealed class ConfigEntry<T> : ConfigFile.IEntry
{
    T _value;

    public ConfigFile ConfigFile { get; }
    public ConfigDefinition Definition { get; }
    public string Description { get; }
    public T DefaultValue { get; }
    public Type SettingType => typeof(T);

    internal ConfigEntry(ConfigFile file, ConfigDefinition def, T defaultValue, string description)
    {
        ConfigFile = file; Definition = def; DefaultValue = defaultValue; Description = description; _value = defaultValue;
    }

    public T Value
    {
        get => _value;
        set
        {
            if (EqualityComparer<T>.Default.Equals(_value, value)) return;
            _value = value;
            ConfigFile.Save();
        }
    }

    string ConfigFile.IEntry.DefaultText => ConfigFile.ToText(DefaultValue);
    string ConfigFile.IEntry.ValueText => ConfigFile.ToText(_value);

    void ConfigFile.IEntry.SetFromText(string raw)
    {
        try { _value = (T)ConfigFile.FromText(raw, typeof(T)); }
        catch (Exception e) { Plugin.Warn($"setting [{Definition.Section}] {Definition.Key} = '{raw}' is not valid ({e.Message}), using {ConfigFile.ToText(_value)}"); }
    }
}

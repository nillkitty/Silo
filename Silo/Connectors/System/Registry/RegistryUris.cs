namespace Silo.Connectors.System;

/// <summary>Turns what a person types for a registry key (<c>HKEY_CURRENT_USER\Software</c>, <c>HKCU:\Software</c>, <c>Computer\HKEY_LOCAL_MACHINE\...</c>) into the <c>reg:</c> URI Silo uses.</summary>
public static class RegistryUris
{
    private static readonly Dictionary<string, string> Hives = new(StringComparer.OrdinalIgnoreCase)
                                                               {
                                                                   ["hkcr"]                  = "hkey_classes_root",
                                                                   ["hkey_classes_root"]     = "hkey_classes_root",
                                                                   ["hkcu"]                  = "hkey_current_user",
                                                                   ["hkey_current_user"]     = "hkey_current_user",
                                                                   ["hklm"]                  = "hkey_local_machine",
                                                                   ["hkey_local_machine"]    = "hkey_local_machine",
                                                                   ["hku"]                   = "hkey_users",
                                                                   ["hkey_users"]            = "hkey_users",
                                                                   ["hkcc"]                  = "hkey_current_config",
                                                                   ["hkey_current_config"]   = "hkey_current_config",
                                                                   ["hkpd"]                  = "hkey_performance_data",
                                                                   ["hkey_performance_data"] = "hkey_performance_data"
                                                               };

    /// <summary>
    ///     The <c>reg:</c> URI for the text, or null if it does not start with a registry hive. Text that already is a URI
    ///     (it contains <c>://</c>) is returned as the URI it is, whatever its scheme.
    /// </summary>
    public static Uri? FromText(string? text)
    {
        text = text?.Trim().Trim('"').Trim();
        if (string.IsNullOrEmpty(text)) return null;

        if (text.Contains("://", StringComparison.Ordinal))
            return Uri.TryCreate(text, UriKind.Absolute, out var given) ? given : null;

        var parts = text.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (parts.Count > 0 && parts[0].Equals("Computer", StringComparison.OrdinalIgnoreCase)) parts.RemoveAt(0);
        if (parts.Count == 0) return null;

        if (!Hives.TryGetValue(parts[0].TrimEnd(':'), out var hive)) return null;

        string path = string.Join('/', new[] { hive }.Concat(parts.Skip(1).Select(Uri.EscapeDataString)));
        return Uri.TryCreate("reg:///" + path, UriKind.Absolute, out var uri) ? uri : null;
    }

    /// <summary>What a value with no name (the key's "(Default)" value) is called in a URI, which has no way to say "the empty name".</summary>
    public const string DefaultValueName = "(Default)";

    /// <summary>
    ///     The <c>reg:</c> URI of a key, given its full name as <c>RegistryKey.Name</c> spells it
    ///     (<c>HKEY_CURRENT_USER\Software\Silo</c>), and of one of its values when <paramref name="valueName" /> is given
    ///     (<c>reg:///hkey_current_user/Software/Silo?Name</c>: the whole query is the value's name; an empty name is
    ///     <see cref="DefaultValueName" />). An empty key name is the local registry's root, <c>reg:///</c>. The opposite of
    ///     <see cref="RegistryUriProvider.ParseUri" />.
    /// </summary>
    public static Uri FromKeyName(string? keyName, string? valueName = null)
    {
        var parts = (keyName ?? string.Empty).Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (parts.Count > 0 && parts[0].Equals("Computer", StringComparison.OrdinalIgnoreCase)) parts.RemoveAt(0);

        string text = "reg:///";
        if (parts.Count > 0)
        {
            string hive = Hives.TryGetValue(parts[0].TrimEnd(':'), out var known) ? known : Uri.EscapeDataString(parts[0]);
            text += string.Join('/', new[] { hive }.Concat(parts.Skip(1).Select(Uri.EscapeDataString)));
        }

        if (valueName is not null) text += "?" + Uri.EscapeDataString(valueName.Length == 0 ? DefaultValueName : valueName);

        return new Uri(text, UriKind.Absolute);
    }

    /// <summary>The <c>reg:</c> URI of a <see cref="RegistryPath" />: its key, or its value when it names one.</summary>
    public static Uri FromPath(RegistryPath path)
    {
        return FromKeyName(path.Required().FullName, path.ValueName);
    }

    /// <summary>Whether the text names a registry location <paramref name="connector" /> will take, and if not, why.</summary>
    public static UriCheck Check(string? text, IUriConnector connector)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new UriCheck(false, null, null, "Enter a registry key such as HKEY_CURRENT_USER\\Software.");

        var uri = FromText(text);
        if (uri is null)
            return new UriCheck(false, null, null, "Start with a registry hive (HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE, HKCU, ...) or give a reg: URI.");

        return UriRules.CheckUri(uri, [connector], UriRules.AnyScope);
    }
}

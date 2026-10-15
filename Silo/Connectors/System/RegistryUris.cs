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
                                                                   ["hkey_current_config"]   = "hkey_current_config"
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

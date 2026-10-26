using Newtonsoft.Json;

namespace Silo.Settings;

// The values behind the Settings panes (Views/Settings). Plain classes: a new one holds the defaults, and each is written
// as JSON -- into the Silo (SiloSettings<T>) or a file in %APPDATA%\Silo (SettingsFile<T>).
//
// The Silo ones are read by Stored<T>, which adds to a collection the class already filled in rather than replacing it,
// so their collections start empty; a default list is computed where it is used instead.

/// <summary>Settings ▸ Silo Settings ▸ Assemblies.</summary>
public sealed class AssembliesSettings
{
    /// <summary>Assemblies the Silo cannot be opened without.</summary>
    public List<string> Required { get; set; } = [];

    /// <summary>Assemblies some items need in order to be shown or edited.</summary>
    public List<string> Auxiliary { get; set; } = [];

    /// <summary>Assemblies that add tools, data sources, connectors, labs and other resources.</summary>
    public List<string> Extension { get; set; } = [];
}

/// <summary>Settings ▸ Silo Settings ▸ Branding: how the app presents itself while this Silo is open.</summary>
public sealed class BrandingSettings
{
    /// <summary>The customizations that can be set, in the order they are listed.</summary>
    public static IReadOnlyList<string> Keys { get; } =
    [
        "Application.Name", "Application.Icon", "Application.ShortName", "Application.UserKey", "Application.AutoReload",
        "Application.WindowPosition", "Application.WindowState"
    ];

    /// <summary>The value of each customization that was set; a key that is not here has the app's own value.</summary>
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Settings ▸ Silo Settings ▸ Security/Scripting: what in this Silo may run.</summary>
public sealed class SecuritySettings
{
    public bool Queries    { get; set; } = true;
    public bool Sql        { get; set; } = true;
    public bool Xql        { get; set; } = true;
    public bool Scripts    { get; set; } = true;
    public bool JavaScript { get; set; } = true;
    public bool FSharp     { get; set; } = true;
    public bool CSharp     { get; set; } = true;
    public bool Python     { get; set; } = true;
    public bool PowerShell { get; set; } = true;

    /// <summary>if, switch, for, each, while...</summary>
    public bool ConditionalExpressions { get; set; } = true;

    /// <summary>and, or, not...</summary>
    public bool LogicalExpressions { get; set; } = true;

    /// <summary>avg, min, max, arithmetic...</summary>
    public bool ValueExpressions { get; set; } = true;

    public bool RequireSignedAssemblies { get; set; }
    public bool RequireSignedScripts    { get; set; }
    public bool ConfirmExecution        { get; set; }

    /// <summary>With <see cref="ConfirmExecution" />: the person may approve running the same content again without asking.</summary>
    public bool AllowPreApproval { get; set; } = true;

    public bool RequireSignedPackages { get; set; } = true;
}

/// <summary>Settings ▸ Silo Settings ▸ Backup/Restore.</summary>
public sealed class BackupSettings
{
    /// <summary>The backup providers that can be chosen.</summary>
    public static IReadOnlyList<string> Providers { get; } = ["Google Drive", "Filesystem Backup", "Remote SQL Backup"];

    /// <summary>The provider that makes the automatic backups; null for none.</summary>
    public string? AutomaticProvider { get; set; }

    /// <summary>The provider backups are restored from; null for none.</summary>
    public string? RestoreProvider { get; set; }
}

/// <summary>Settings ▸ Silo Settings ▸ Network &amp; Energy: when background work may run.</summary>
public sealed class NetworkSettings
{
    public bool DisableSyncOnBattery        { get; set; }
    public bool DisableSyncWithoutNetwork   { get; set; } = true;
    public bool DisableBackupOnBattery      { get; set; }
    public bool OnlySyncOnNetwork           { get; set; }
    public string SyncNetwork               { get; set; } = string.Empty;
    public bool OnlyBackupOnNetwork         { get; set; }
    public string BackupNetwork             { get; set; } = string.Empty;
}

/// <summary>Settings ▸ Silo Settings ▸ Auditing &amp; Undo.</summary>
public sealed class AuditUndoSettings
{
    /// <summary>The kinds of action, in the order they are listed; "All" first.</summary>
    public static IReadOnlyList<string> Kinds { get; } = ["All", "Data", "Authoring", "Development"];

    /// <summary>The kinds tracked for undo/redo (by name; "All" is all three of the others).</summary>
    public List<string> NotUndone { get; set; } = [];

    /// <summary>The kinds not written to the audit log.</summary>
    public List<string> NotAudited { get; set; } = [];

    public int UndoMaxLevels     { get; set; } = 30;
    public int UndoMaxUniverses  { get; set; } = 5;

    public bool IsUndone(string kind)  => kind == "All" ? Kinds.Skip(1).All(IsUndone) : !NotUndone.Contains(kind);
    public bool IsAudited(string kind) => kind == "All" ? Kinds.Skip(1).All(IsAudited) : !NotAudited.Contains(kind);

    public void SetUndone(string kind, bool on)  => Set(NotUndone, kind, on);
    public void SetAudited(string kind, bool on) => Set(NotAudited, kind, on);

    private static void Set(List<string> off, string kind, bool on)
    {
        foreach (string k in kind == "All" ? Kinds.Skip(1) : [kind])
        {
            off.Remove(k);
            if (!on) off.Add(k);
        }
    }
}

/// <summary>Settings ▸ Silo Settings ▸ Keyboard: the keys of the app's own commands, where they differ from the defaults.</summary>
public sealed class KeyboardSettings
{
    /// <summary>Command id -> key (<c>Ctrl+Shift+S</c>); an empty value takes the key away.</summary>
    public Dictionary<string, string> CommandKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>What another person may do in the Silo.</summary>
public enum AccessLevel
{
    ReadOnly,
    ReadWrite
}

/// <summary>One other person (or group) on Settings ▸ Owner Settings ▸ Access Control.</summary>
public sealed class AccessEntry
{
    /// <summary><c>DOMAIN\name</c>.</summary>
    public string Account { get; set; } = string.Empty;

    public AccessLevel Level { get; set; } = AccessLevel.ReadOnly;
}

/// <summary>Settings ▸ Owner Settings ▸ Access Control: who else the owner shares the Silo with.</summary>
public sealed class AccessSettings
{
    public List<AccessEntry> Users { get; set; } = [];
}

/// <summary>A switch that turns a set of named aspects on and off; all are on unless listed in <see cref="Off" />.</summary>
public abstract class AspectSettings
{
    /// <summary>The aspects that are switched off.</summary>
    public List<string> Off { get; set; } = [];

    public bool Has(string aspect) => !Off.Contains(aspect);

    public void Set(string aspect, bool on)
    {
        Off.Remove(aspect);
        if (!on) Off.Add(aspect);
    }
}

/// <summary>Settings ▸ User Settings ▸ Authoring Settings (Author level).</summary>
public sealed class AuthoringSettings : AspectSettings
{
    public static IReadOnlyList<string> TooltipAspects { get; } =
        ["Type", "Url", "Interfaces", "Action Sources", "Data Sources", "Name Source", "Icon Source", "Tooltip Source"];

    public static IReadOnlyList<string> MenuPieces { get; } = ["Icon Size", "Display Names", "Tooltip", "Visibility", "Action", "Enabled", "Other"];

    /// <summary>Authoring tooltips (providers, interfaces, data sources) on Ctrl+Shift.</summary>
    public bool Tooltips { get; set; } = true;

    /// <summary>The authoring (design) context menu.</summary>
    public bool ContextMenu { get; set; } = true;
}

/// <summary>Settings ▸ User Settings ▸ Developer Settings (Developer level).</summary>
public sealed class DeveloperSettings : AspectSettings
{
    public static IReadOnlyList<string> TooltipAspects { get; } = ["Type and Value", "Errors", "Log Messages"];

    public static IReadOnlyList<string> MenuPieces { get; } = ["Assign to Variable", "Copy Value", "Copy Dump"];

    /// <summary>Developer tooltips on Alt+Ctrl.</summary>
    public bool Tooltips { get; set; } = true;

    /// <summary>The developer context menu.</summary>
    public bool ContextMenu { get; set; } = true;
}

/// <summary>Settings ▸ App Settings ▸ Auto-Update.</summary>
public sealed class AutoUpdateSettings
{
    /// <summary>The kinds of component that can update on their own, in the order they are listed.</summary>
    public static IReadOnlyList<string> Components { get; } =
    [
        "Schema", "Extension Package (NuGet)", "PowerShell Installed Module", "Universal Package", "Imported Types", "Imported Syntaxes",
        "Imported Enums"
    ];

    /// <summary>The kinds that are never updated.</summary>
    public List<string> NeverUpdate { get; set; } = [];
}

/// <summary>Settings ▸ App Settings ▸ Registered Handlers.</summary>
public sealed class HandlerSettings
{
    /// <summary>The URI schemes Silo offers to handle out of the box.</summary>
    public static IReadOnlyList<string> BuiltIn { get; } = ["silo", "sql", "ldap", "drawio"];

    /// <summary>Schemes the person added.</summary>
    public List<string> Added { get; set; } = [];

    /// <summary>Every scheme listed: the built-in ones, then the added ones.</summary>
    [JsonIgnore]
    public IEnumerable<string> All => BuiltIn.Concat(Added).Distinct(StringComparer.OrdinalIgnoreCase);
}

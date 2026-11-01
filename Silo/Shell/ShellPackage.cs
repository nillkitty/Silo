using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace Silo.Shell;

/// <summary>Whose registry a package writes to: the person's (no elevation needed) or every user's (needs an administrator).</summary>
public enum ShellScope
{
    CurrentUser,
    AllUsers
}

/// <summary>What one entry of a Shell package sets up.</summary>
public enum ShellEntryKind
{
    /// <summary>A protocol handler: <c>scheme://...</c> addresses open a command.</summary>
    Protocol,

    /// <summary>A ProgID (a kind of file) with its description, icon and open command, and the extensions that use it.</summary>
    FileType,

    /// <summary>A static action in the right-click menu of a class (a kind of file, folders, drives, the folder background...).</summary>
    Verb,

    /// <summary>A static cascading submenu in the right-click menu of a class; its children are verbs.</summary>
    Submenu,

    /// <summary>The icon of a class.</summary>
    ClassIcon,

    /// <summary>Which action of a class is the default one (a double-click).</summary>
    DefaultVerb,

    /// <summary>A shortcut in the Send To menu.</summary>
    SendTo,

    /// <summary>An item of the New menu (a template for a file type).</summary>
    NewFile,

    /// <summary>A program name: what Run and the shell find by <c>name.exe</c> alone (App Paths).</summary>
    AppPath,

    /// <summary>A program's registration as an application (friendly name, icon, open command, the types it opens).</summary>
    Application,

    /// <summary>Any registry value, for what the other kinds do not cover.</summary>
    Registry
}

public enum ShellPosition
{
    None,
    Top,
    Bottom
}

public enum ShellValueKind
{
    String,
    ExpandString,
    DWord
}

/// <summary>
///     One thing a Shell package sets up. Which fields matter depends on <see cref="Kind" />: <see cref="ShellCompiler" /> says how each kind
///     becomes registry values and files, and <see cref="ShellCheck" /> what each needs.
/// </summary>
public sealed class ShellEntry
{
    /// <summary>What kind of entry this is.</summary>
    public ShellEntryKind Kind { get; set; }

    /// <summary>
    ///     Its name: the scheme (Protocol), the ProgID (FileType), the verb's key (Verb, Submenu, DefaultVerb), the file name without the extension
    ///     (SendTo), the extension (NewFile), the program's file name "tool.exe" (AppPath, Application).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     The class an action or icon belongs to: "*" (all files), an extension (".txt"), "Directory", "Directory\Background", "Folder", "Drive",
    ///     "AllFilesystemObjects", or a ProgID.
    /// </summary>
    public string? Target { get; set; }

    /// <summary>The text shown in the menu (or the friendly name of a program).</summary>
    public string? Label { get; set; }

    /// <summary>What it is, as the shell describes it (a protocol's or file type's description).</summary>
    public string? Description { get; set; }

    /// <summary>The command line to run, with %1 (the item) or %V (the folder) where it goes; or the path of the program, or of a SendTo shortcut's target.</summary>
    public string? Command { get; set; }

    /// <summary>The arguments of a SendTo shortcut.</summary>
    public string? Arguments { get; set; }

    /// <summary>An icon: a path to an .ico, .exe or .dll, optionally followed by ",index".</summary>
    public string? Icon { get; set; }

    /// <summary>Where a verb sits in its menu.</summary>
    public ShellPosition Position { get; set; }

    /// <summary>Only shown when Shift is held while right-clicking.</summary>
    public bool Extended { get; set; }

    /// <summary>For a verb: it becomes the class's default action. For a FileType: its ProgID becomes the default of its extensions.</summary>
    public bool MakeDefault { get; set; }

    /// <summary>The extensions (".md") a FileType or Application handles.</summary>
    public List<string> Extensions { get; set; } = [];

    /// <summary>For NewFile: what a new file contains (empty: an empty file).</summary>
    public string? Content { get; set; }

    /// <summary>For Registry: the whole key, from the hive ("HKCU\Software\Vendor\Product").</summary>
    public string? Key { get; set; }

    /// <summary>For Registry: the value's name (empty: the key's default value).</summary>
    public string? ValueName { get; set; }

    public ShellValueKind ValueKind { get; set; }

    /// <summary>For Registry: the value.</summary>
    public string? Data { get; set; }

    /// <summary>For a Submenu: its verbs.</summary>
    public List<ShellEntry> Children { get; set; } = [];

    /// <summary>False to keep the entry in the package without installing it.</summary>
    [System.ComponentModel.DefaultValue(true)]
    public bool Enabled { get; set; } = true;

    [JsonIgnore]
    public string Display
    {
        get
        {
            string text = !string.IsNullOrWhiteSpace(Label) ? ShellText.PlainLabel(Label) : Name;
            return string.IsNullOrWhiteSpace(text) ? "(" + Kind + ")" : text;
        }
    }

    public ShellEntry Clone() => ShellJson.FromJsonEntry(ShellJson.ToJson(this)) ?? new ShellEntry();
}

/// <summary>A Shell package: the shell extensions to set up on a Windows system, installed and removed together.</summary>
public sealed class ShellPackage
{
    /// <summary>Names the package everywhere: the Silo item, the record of what was installed.</summary>
    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = "1.0";

    public string? Description { get; set; }

    public string? Author { get; set; }

    public ShellScope Scope { get; set; }

    public List<ShellEntry> Entries { get; set; } = [];

    public ShellPackage Clone() => ShellJson.FromJson(ShellJson.ToJson(this), out var copy, out _) ? copy : new ShellPackage();

    /// <summary>The entries and, inside submenus, their verbs, each with the entry it is inside.</summary>
    public IEnumerable<(ShellEntry Entry, ShellEntry? Parent)> Walk()
    {
        foreach (var e in Entries)
        {
            yield return (e, null);
            foreach (var c in e.Children) yield return (c, e);
        }
    }
}

public static class ShellText
{
    /// <summary>A menu label without its access-key underscore ("_Open" is "Open"); "&amp;Open" is the registry's way of writing it.</summary>
    public static string PlainLabel(string label) => label.Replace("&", string.Empty).Replace("_", string.Empty);
}

/// <summary>A Shell package as JSON, which is how it is kept in the Silo, in a file, and written in the record of what was installed.</summary>
public static class ShellJson
{
    private static readonly JsonSerializerSettings Settings = new()
                                                              {
                                                                  Formatting            = Formatting.Indented,
                                                                  NullValueHandling     = NullValueHandling.Ignore,
                                                                  DefaultValueHandling  = DefaultValueHandling.Ignore,
                                                                  ContractResolver      = new CamelCasePropertyNamesContractResolver(),
                                                                  Converters            = { new StringEnumConverter(new CamelCaseNamingStrategy()) },
                                                                  MissingMemberHandling = MissingMemberHandling.Ignore
                                                              };

    public static string ToJson(ShellPackage package) => JsonConvert.SerializeObject(package, Settings);

    public static string ToJson(ShellEntry entry) => JsonConvert.SerializeObject(entry, Settings);

    internal static ShellEntry? FromJsonEntry(string text) => JsonConvert.DeserializeObject<ShellEntry>(text, Settings);

    /// <summary>Reads a package from JSON; fails, with what is wrong, for JSON that is not a Shell package.</summary>
    public static bool FromJson(string? text, out ShellPackage package, out string? error)
    {
        package = new ShellPackage();
        error   = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "There is no JSON.";
            return false;
        }

        try
        {
            var read = JsonConvert.DeserializeObject<ShellPackage>(text, Settings);
            if (read is null)
            {
                error = "That is not a Shell package.";
                return false;
            }

            read.Entries ??= [];
            read.Entries.RemoveAll(e => e is null);
            foreach (var e in read.Entries)
            {
                e.Extensions ??= [];
                e.Children   ??= [];
                e.Children.RemoveAll(c => c is null);
                foreach (var c in e.Children)
                {
                    c.Extensions ??= [];
                    c.Children   =   [];
                }
            }

            package = read;
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

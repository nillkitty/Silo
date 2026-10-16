using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace Silo.Toolbar;

/// <summary>
///     One place in the toolbar: a catalog item (<see cref="Id" />, see <see cref="ToolbarCatalog" />), or a separator
///     (<see cref="ToolbarLayout.SeparatorId" />), with the person's own label / icon / tooltip when they changed them.
/// </summary>
public sealed class ToolbarSlot
{
    public ToolbarSlot() { }

    public ToolbarSlot(string id) => Id = id;

    /// <summary>The catalog id this slot shows, or <see cref="ToolbarLayout.SeparatorId" />.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Replaces the button's label (shown as text, or as its tooltip title when it has an icon).</summary>
    public string? Header { get; set; }

    /// <summary>Replaces the button's icon with the geometry resource of this key.</summary>
    public string? IconKey { get; set; }

    /// <summary>Replaces the button's tooltip.</summary>
    public string? ToolTip { get; set; }

    /// <summary>Shows the label next to the icon (or instead of it, when there is no icon).</summary>
    public bool ShowText { get; set; }

    [JsonIgnore]
    public bool IsSeparator => Id == ToolbarLayout.SeparatorId;

    public ToolbarSlot Clone() => (ToolbarSlot)MemberwiseClone();
}

/// <summary>What the main toolbar shows: whether it is visible at all, and its buttons in order.</summary>
public sealed class ToolbarLayout
{
    /// <summary>The id of a separator slot.</summary>
    public const string SeparatorId = "-";

    public const string ToolsDropdownId   = "tools.menu";
    public const string ToolCommandsId    = "tools.commands";
    public const string NodeCommandsId    = "node.commands";
    public const string CustomizeId       = "toolbar.customize";

    public bool Visible { get; set; } = true;

    public List<ToolbarSlot> Slots { get; set; } = [];

    public ToolbarLayout Clone() => new() { Visible = Visible, Slots = Slots.Select(s => s.Clone()).ToList() };

    /// <summary>The layout a new install starts with (and Reset Toolbar returns to): every File / Edit / Help / Silo command, the node and tool groups, the Tools drop-down.</summary>
    public static ToolbarLayout Default()
    {
        string[] ids =
        [
            // File
            "file.new", "file.open", "file.openurl", "file.save", "file.saveas", "file.close", "file.exit", "-",
            // Edit
            "edit.undo", "edit.redo", "edit.cut", "edit.copy", "edit.paste", "edit.pastetable", "edit.delete", "edit.selectall", "-",
            // Silo
            "silo.newconnection", "silo.newcredential", "silo.archive", "silo.table.rich", "silo.table.clipboard", "silo.export.all",
            "silo.import", "-",
            // the selected Explorer node's own commands
            NodeCommandsId, "-",
            // tools: those that offer commands, then a drop-down for the rest
            ToolCommandsId, ToolsDropdownId, "-",
            // Help and the toolbar itself
            "help.about", CustomizeId
        ];

        return new ToolbarLayout { Slots = ids.Select(id => new ToolbarSlot(id)).ToList() };
    }

    /// <summary>
    ///     Drops separators that do not separate anything (at the ends, or two in a row) and slots with no id. Returns the
    ///     same layout for chaining.
    /// </summary>
    public ToolbarLayout Normalize()
    {
        Slots.RemoveAll(s => string.IsNullOrWhiteSpace(s.Id));

        var result = new List<ToolbarSlot>();
        foreach (var slot in Slots)
        {
            if (slot.IsSeparator && (result.Count == 0 || result[^1].IsSeparator)) continue;
            result.Add(slot);
        }

        while (result.Count > 0 && result[^1].IsSeparator) result.RemoveAt(result.Count - 1);
        Slots = result;
        return this;
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Reads a layout from JSON; null when the text is not a usable layout.</summary>
    public static ToolbarLayout? FromJson(string json)
    {
        try
        {
            var layout = JsonSerializer.Deserialize<ToolbarLayout>(json, Options);
            return layout is { Slots.Count: > 0 } ? layout.Normalize() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
///     The person's toolbar layout, kept in <c>%APPDATA%\Silo\toolbar.json</c>. The toolbar rebuilds itself when
///     <see cref="Changed" /> is raised.
/// </summary>
public static class ToolbarSettings
{
    private static readonly string SettingsFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Silo", "toolbar.json");

    private static ToolbarLayout? _layout;

    /// <summary>The current layout (do not change it in place: pass a changed copy to <see cref="Apply" />).</summary>
    public static ToolbarLayout Layout => _layout ??= Load();

    /// <summary>Raised on the calling thread after the layout changed.</summary>
    public static event EventHandler? Changed;

    /// <summary>Whether the toolbar is shown (View -> Toolbar).</summary>
    public static bool Visible
    {
        get => Layout.Visible;
        set
        {
            if (Layout.Visible == value) return;

            var copy = Layout.Clone();
            copy.Visible = value;
            Apply(copy);
        }
    }

    /// <summary>Makes <paramref name="layout" /> the layout: remembered, saved and shown.</summary>
    public static void Apply(ToolbarLayout layout)
    {
        _layout = layout.Normalize();
        Save();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Back to <see cref="ToolbarLayout.Default" />, and visible.</summary>
    public static void Reset() => Apply(ToolbarLayout.Default());

    private static ToolbarLayout Load()
    {
        try
        {
            if (File.Exists(SettingsFile) && ToolbarLayout.FromJson(File.ReadAllText(SettingsFile)) is { } layout)
                return layout;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Reading the toolbar layout {file} failed; using the default", SettingsFile);
        }

        return ToolbarLayout.Default();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, Layout.ToJson());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Saving the toolbar layout to {file} failed", SettingsFile);
        }
    }
}

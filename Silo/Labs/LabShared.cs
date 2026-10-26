using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Serilog;

namespace Silo.Labs;

// ---- what every Lab window offers besides the Lab's own panes --------------------------------------
//
// The window adds these panes itself, the way it adds the parameters: Library (what the Silo has that the Lab can open), Recent (what was
// opened lately), Presets (settings kept under a name) and Problems (what is wrong; only there when something is). A Lab joins in by
// implementing the interfaces below -- LabSession<T> already does, with nothing to say until a Lab overrides.

/// <summary>What a pane is about: the View menu lists the panes of the things a Lab edits and keeps first, the other panes after a line.</summary>
public enum LabPaneKind
{
    /// <summary>The pane is about the things the Lab works with and can keep in the Silo (the library, the samples, the tests...).</summary>
    Entity,

    /// <summary>The pane is a view of what is going on (a console, a terminal, the DOM...) or a tool.</summary>
    Other
}

// ---- problems ---------------------------------------------------------------------------------

/// <summary>How bad a <see cref="LabProblem" /> is.</summary>
public enum LabProblemSeverity
{
    /// <summary>Worth knowing.</summary>
    Info,

    /// <summary>Something is likely not as intended.</summary>
    Warning,

    /// <summary>Something failed or is wrong.</summary>
    Error
}

/// <summary>One thing that is wrong, as the Problems pane lists it.</summary>
/// <param name="Severity">How bad it is.</param>
/// <param name="Source">What found it ("Navigation", "JavaScript", "Menu", "Schema"...).</param>
/// <param name="Message">What is wrong.</param>
/// <param name="Where">Where in the value ("Entry File/Open", "Type Customer"); null when it is not about a part.</param>
/// <param name="Go">Takes the person to it (selects the entry...); null when there is nowhere to go.</param>
public sealed record LabProblem(LabProblemSeverity Severity, string Source, string Message, string? Where = null, Action? Go = null);

/// <summary>What a session says about its problems. The window shows the Problems pane and a count in its status bar when there are any.</summary>
public interface ILabProblems
{
    /// <summary>What is wrong now: what the session found by checking its value, then what was reported (newest last).</summary>
    IReadOnlyList<LabProblem> ProblemList { get; }

    /// <summary>Raised when <see cref="ProblemList" /> may have changed.</summary>
    event EventHandler? ProblemsChanged;

    /// <summary>Notes a problem that happened (a navigation failed, a script threw, a command failed).</summary>
    void ReportProblem(LabProblemSeverity severity, string source, string message, string? where = null, Action? go = null);

    /// <summary>Forgets the reported problems (of one source, or all).</summary>
    void ClearProblems(string? source = null);
}

// ---- library ----------------------------------------------------------------------------------

/// <summary>One entry of the Library pane.</summary>
/// <param name="Name">What it is called.</param>
/// <param name="Kind">What kind of thing it is (shown in a column of its own).</param>
/// <param name="Detail">More, at the end of the row.</param>
/// <param name="IconKey">The icon key of what it is.</param>
/// <param name="Open">Opens it (double-click, Enter).</param>
/// <param name="IsCurrent">Whether it is what the window is on now.</param>
/// <param name="IsTemplate">Whether it comes with the app rather than from the Silo.</param>
/// <param name="ToolTip">A fuller description.</param>
/// <param name="Delete">Removes it from the Silo; null when it cannot be removed.</param>
public sealed record LabLibraryEntry(string Name, string? Kind, string? Detail, string IconKey, Action Open, bool IsCurrent = false, bool IsTemplate = false, string? ToolTip = null, Action? Delete = null);

/// <summary>A group of entries of the Library pane under a heading.</summary>
/// <param name="Title">The heading.</param>
/// <param name="IconKey">Its icon.</param>
/// <param name="Entries">The entries.</param>
/// <param name="IsTemplate">Whether the entries are the ready-made ones that come with the app.</param>
/// <param name="Note">Shown instead of entries when there are none ("No Silo is open").</param>
public sealed record LabLibrarySection(string Title, string? IconKey, IReadOnlyList<LabLibraryEntry> Entries, bool IsTemplate = false, string? Note = null);

/// <summary>What a session says about the items in the Silo that its Lab can open.</summary>
public interface ILabLibrary
{
    /// <summary>The library for this filter (empty: all): groups of entries -- kept in the Silo, found in the Silo, ready-made.</summary>
    IReadOnlyList<LabLibrarySection> LibrarySections(string filter);

    /// <summary>Whether a window of the Lab opens with the Library showing (when it has no remembered arrangement).</summary>
    bool LibraryOpensWithWindow { get; }
}

// ---- recent -----------------------------------------------------------------------------------

/// <summary>What a session says about reopening what it opened lately.</summary>
public interface ILabRecent
{
    /// <summary>Opens again something the session put in the recent list (a web address, the name of a saved item).</summary>
    Task OpenRecentAsync(string text);
}

// ---- presets ----------------------------------------------------------------------------------

/// <summary>
///     A kind of settings a Lab can keep as presets: a tool's, a filter's, an item's property list. The Presets pane offers to save what
///     <see cref="Capture" /> gives under a name, and to put a saved one back with <see cref="Apply" />.
/// </summary>
/// <param name="Id">A short stable key ("parameters", "console-filter"); how the profile files the presets.</param>
/// <param name="Title">What the person calls it ("Lab parameters", "Console filter").</param>
/// <param name="Capture">The settings as they are now: names and values as text.</param>
/// <param name="Apply">Puts the settings back.</param>
/// <param name="IconKey">Its icon.</param>
public sealed record LabPresetKind(string Id, string Title, Func<Dictionary<string, string>> Capture, Action<Dictionary<string, string>> Apply, string? IconKey = null);

/// <summary>What a session says about the settings worth keeping as presets.</summary>
public interface ILabPresets
{
    /// <summary>The kinds of settings that can be kept (the Lab's parameters, a filter, a tool's options...).</summary>
    IReadOnlyList<LabPresetKind> PresetKinds { get; }
}

// ---- the profile ------------------------------------------------------------------------------

/// <summary>
///     What the Labs keep for the person rather than for a Silo: the recently opened values and the presets (<c>%APPDATA%\Silo\labs.json</c>).
///     The same in every Silo, so a preset made for one Silo is there in the next.
/// </summary>
public static class LabProfile
{
    public const int RecentLimit = 30;

    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static Data? _data;

    /// <summary>Where the profile is kept; tests point it somewhere else.</summary>
    public static string? PathOverride { get; set; }

    private static string FilePath => PathOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Silo", "labs.json");

    /// <summary>Raised (on the thread that changed it) when the recent values or the presets changed, so other windows of the Lab can show them.</summary>
    public static event Action? Changed;

    /// <summary>Forgets what was read (the next use reads the file again).</summary>
    public static void Reset()
    {
        lock (Gate) _data = null;
    }

    // ---- recent

    public static IReadOnlyList<string> Recent(string labId)
    {
        lock (Gate) return Load().Recent.TryGetValue(labId, out var list) ? list.ToList() : [];
    }

    /// <summary>Puts the value first in the Lab's recent list (it moves up when it is there already).</summary>
    public static void Remember(string labId, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        lock (Gate)
        {
            var data = Load();
            if (!data.Recent.TryGetValue(labId, out var list)) data.Recent[labId] = list = [];

            if (list.Count > 0 && list[0] == text) return;

            list.RemoveAll(t => t == text);
            list.Insert(0, text);
            if (list.Count > RecentLimit) list.RemoveRange(RecentLimit, list.Count - RecentLimit);
            Save(data);
        }

        Changed?.Invoke();
    }

    public static void Forget(string labId, string text)
    {
        lock (Gate)
        {
            var data = Load();
            if (!data.Recent.TryGetValue(labId, out var list) || list.RemoveAll(t => t == text) == 0) return;

            Save(data);
        }

        Changed?.Invoke();
    }

    public static void ClearRecent(string labId)
    {
        lock (Gate)
        {
            var data = Load();
            if (!data.Recent.Remove(labId)) return;

            Save(data);
        }

        Changed?.Invoke();
    }

    // ---- presets

    public static IReadOnlyList<string> PresetNames(string labId, string kind)
    {
        lock (Gate)
            return Load().Presets.TryGetValue(labId, out var kinds) && kinds.TryGetValue(kind, out var presets)
                       ? presets.Keys.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList()
                       : [];
    }

    public static Dictionary<string, string>? GetPreset(string labId, string kind, string name)
    {
        lock (Gate)
            return Load().Presets.TryGetValue(labId, out var kinds) && kinds.TryGetValue(kind, out var presets) && presets.TryGetValue(name, out var settings)
                       ? new Dictionary<string, string>(settings)
                       : null;
    }

    public static void SavePreset(string labId, string kind, string name, Dictionary<string, string> settings)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Give the preset a name first.");

        lock (Gate)
        {
            var data = Load();
            if (!data.Presets.TryGetValue(labId, out var kinds)) data.Presets[labId] = kinds = [];
            if (!kinds.TryGetValue(kind, out var presets)) kinds[kind] = presets = [];

            presets[name.Trim()] = new Dictionary<string, string>(settings);
            Save(data);
        }

        Changed?.Invoke();
    }

    public static bool DeletePreset(string labId, string kind, string name)
    {
        lock (Gate)
        {
            var data = Load();
            if (!data.Presets.TryGetValue(labId, out var kinds) || !kinds.TryGetValue(kind, out var presets) || !presets.Remove(name)) return false;

            Save(data);
        }

        Changed?.Invoke();
        return true;
    }

    // ---- macros: terminal lines kept under a name (a command's id is a line too)

    public static IReadOnlyList<string> MacroNames(string labId)
    {
        lock (Gate)
            return Load().Macros.TryGetValue(labId, out var macros) ? macros.Keys.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList() : [];
    }

    public static IReadOnlyList<string>? GetMacro(string labId, string name)
    {
        lock (Gate)
            return Load().Macros.TryGetValue(labId, out var macros) && macros.TryGetValue(name, out var lines) ? lines.ToList() : null;
    }

    public static void SaveMacro(string labId, string name, IEnumerable<string> lines)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Give the macro a name first.");

        lock (Gate)
        {
            var data = Load();
            if (!data.Macros.TryGetValue(labId, out var macros)) data.Macros[labId] = macros = [];

            macros[name.Trim()] = lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToList();
            Save(data);
        }

        Changed?.Invoke();
    }

    public static bool DeleteMacro(string labId, string name)
    {
        lock (Gate)
        {
            var data = Load();
            if (!data.Macros.TryGetValue(labId, out var macros) || !macros.Remove(name)) return false;

            Save(data);
        }

        Changed?.Invoke();
        return true;
    }

    // ---- the file

    private static Data Load()
    {
        if (_data is not null) return _data;

        try
        {
            if (File.Exists(FilePath)) _data = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath), Options);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Reading the Labs' profile failed");
        }

        return _data ??= new Data();
    }

    private static void Save(Data data)
    {
        try
        {
            string path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data, Options));
            File.Move(temp, path, true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Writing the Labs' profile failed");
        }
    }

    private sealed class Data
    {
        public Dictionary<string, List<string>> Recent { get; set; } = [];

        /// <summary>Lab, then kind of settings, then preset name, then the settings.</summary>
        public Dictionary<string, Dictionary<string, Dictionary<string, Dictionary<string, string>>>> Presets { get; set; } = [];

        /// <summary>Lab, then macro name, then its lines.</summary>
        public Dictionary<string, Dictionary<string, List<string>>> Macros { get; set; } = [];
    }
}

// ---- the window's icon ------------------------------------------------------------------------

/// <summary>Makes a Lab's icon into the picture a window shows in its title bar and the task bar.</summary>
internal static class LabWindowIcon
{
    /// <summary>The Lab's icon as white on a blue tile; null when the Lab has none (or the icon set is not loaded).</summary>
    public static ImageSource? For(string? iconKey)
    {
        var geometry = Silo.Toolbar.ToolbarIcons.Find(iconKey);
        if (geometry is null) return null;

        try
        {
            var bounds = geometry.Bounds;
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return null;

            const double tile = 32, glyph = 20;
            double scale = glyph / Math.Max(bounds.Width, bounds.Height);
            var transform = new TransformGroup();
            transform.Children.Add(new TranslateTransform(-bounds.X - bounds.Width / 2, -bounds.Y - bounds.Height / 2));
            transform.Children.Add(new ScaleTransform(scale, scale));
            transform.Children.Add(new TranslateTransform(tile / 2, tile / 2));

            var shape = geometry.Clone();
            shape.Transform = transform;

            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(LabUi.Blue, null, new RectangleGeometry(new Rect(0, 0, tile, tile), 6, 6)));
            group.Children.Add(new GeometryDrawing(Brushes.White, null, shape));
            group.Freeze();

            var image = new DrawingImage(group);
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Making the window icon {Icon} failed", iconKey);
            return null;
        }
    }
}

// ---- the panes --------------------------------------------------------------------------------

/// <summary>The base of the panes the window adds itself: redraws when asked, and only once the pane has been shown.</summary>
internal abstract class SharedPane : LabInspector
{
    private bool _pending;
    private bool _ready;

    public override LabPaneKind Kind => LabPaneKind.Entity;

    protected sealed override UIElement BuildView()
    {
        var view = Create();
        _ready = true;
        Refresh();
        return view;
    }

    protected abstract UIElement Create();

    protected abstract void Refresh();

    public override void OnShown() => Refresh();

    /// <summary>Draws again a moment from now (once, however many times it is asked).</summary>
    public void Poke()
    {
        if (!_ready || _pending) return;

        _pending = true;
        (Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher).InvokeAsync(() =>
                                                                                      {
                                                                                          _pending = false;
                                                                                          Refresh();
                                                                                      }, DispatcherPriority.Background);
    }
}

/// <summary>The items in the Silo that the Lab can open, and the ready-made ones: double-click one to open it.</summary>
internal sealed class LabLibraryInspector : SharedPane
{
    private readonly ILabLibrary _source;
    private          TextBox?    _filter;
    private          ListBox?    _list;

    public LabLibraryInspector(ILabLibrary source)
    {
        _source = source;
    }

    public const string InspectorId = "library";

    public override string Id => InspectorId;

    public override string Title => "Library";

    public override string IconKey => "Folder17";

    public override LabDock Dock => LabDock.Left;

    public override bool VisibleByDefault => _source.LibraryOpensWithWindow;

    protected override UIElement Create()
    {
        _filter = new TextBox { Margin = new Thickness(6, 4, 6, 4), ToolTip = "Filter the list" };
        _filter.TextChanged += (_, _) => Refresh();
        _list = LabUi.List();
        _list.MouseDoubleClick += (_, _) => OpenSelected();
        _list.KeyDown += (_, e) =>
                         {
                             if (e.Key == Key.Enter) OpenSelected();
                         };
        return LabUi.Layout(_filter, _list);
    }

    private void OpenSelected()
    {
        if ((_list?.SelectedItem as ListBoxItem)?.Tag is not LabLibraryEntry entry) return;

        try
        {
            entry.Open();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Opening {Name} from the library failed", entry.Name);
        }
    }

    protected override void Refresh()
    {
        if (_list is null || _filter is null) return;

        IReadOnlyList<LabLibrarySection> sections;
        try
        {
            sections = _source.LibrarySections(_filter.Text.Trim());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Listing the library failed");
            sections = [];
        }

        _list.Items.Clear();
        foreach (var section in sections)
        {
            if (section.Entries.Count == 0 && section.Note is null) continue;

            _list.Items.Add(LabUi.Section(section.Title, section.IsTemplate, section.IconKey));
            if (section.Note is not null) _list.Items.Add(LabUi.Note(section.Note));
            foreach (var entry in section.Entries) _list.Items.Add(Item(entry));
        }
    }

    private ListBoxItem Item(LabLibraryEntry entry)
    {
        var item = LabUi.Item(entry, LabUi.Row(entry.IconKey, entry.Name, entry.Kind, entry.Detail, entry.IsTemplate, bold: entry.IsCurrent, iconBrush: entry.IsCurrent ? LabUi.Blue : null),
                              entry.ToolTip ?? (entry.IsTemplate ? "Double-click to start with it" : "Double-click to open"));
        if (entry.Delete is not null)
        {
            var menu   = new ContextMenu();
            var delete = new MenuItem { Header = "Delete", Icon = LabUi.Icon("TbDelete") };
            delete.Click += (_, _) =>
                            {
                                if (!LabUi.Confirm($"Remove '{entry.Name}' from the Silo?", "Library")) return;

                                entry.Delete();
                                Refresh();
                            };
            menu.Items.Add(delete);
            item.ContextMenu = menu;
        }

        return item;
    }
}

/// <summary>What the Lab opened lately, newest first (the same in every window of the Lab, and in every Silo): double-click one to open it again.</summary>
internal sealed class LabRecentInspector : SharedPane
{
    private readonly ILabSession       _session;
    private readonly Action<string>    _open;
    private          ListBox?          _list;

    public LabRecentInspector(ILabSession session, Action<string> open)
    {
        _session = session;
        _open    = open;
        LabProfile.Changed += () => Application.Current?.Dispatcher.InvokeAsync(Poke);
    }

    public const string InspectorId = "recent";

    public override string Id => InspectorId;

    public override string Title => "Recent";

    public override string IconKey => "History";

    public override LabDock Dock => LabDock.Left;

    public override bool VisibleByDefault => false;

    protected override UIElement Create()
    {
        var bar = LabUi.Bar(LabUi.Tool("Clear", "TbDelete", () =>
                                                           {
                                                               if (LabUi.Confirm($"Forget everything {_session.Lab.Name} opened lately?", "Recent")) LabProfile.ClearRecent(_session.Lab.Id);
                                                           }, "Forget the list"));
        _list = LabUi.List();
        _list.MouseDoubleClick += (_, _) => OpenSelected();
        _list.KeyDown += (_, e) =>
                         {
                             if (e.Key == Key.Enter) OpenSelected();
                             else if (e.Key == Key.Delete && (_list.SelectedItem as ListBoxItem)?.Tag is string text) LabProfile.Forget(_session.Lab.Id, text);
                         };
        return LabUi.Layout(bar, _list);
    }

    private void OpenSelected()
    {
        if ((_list?.SelectedItem as ListBoxItem)?.Tag is string text) _open(text);
    }

    protected override void Refresh()
    {
        if (_list is null) return;

        var recent = LabProfile.Recent(_session.Lab.Id);
        _list.Items.Clear();
        if (recent.Count == 0)
        {
            _list.Items.Add(LabUi.Note($"Nothing yet: what {_session.Lab.Name} opens is listed here."));
            return;
        }

        foreach (string text in recent)
        {
            var item = LabUi.Item(text, LabUi.Row(_session.Lab.IconKey, LabUi.Shorten(text.ReplaceLineEndings(" "), 80), null, null), text);
            var menu = new ContextMenu();
            var open = new MenuItem { Header = "Open", Icon = LabUi.Icon("TbOpen") };
            open.Click += (_, _) => _open(text);
            var remove = new MenuItem { Header = "Remove from the list", Icon = LabUi.Icon("TbDelete") };
            remove.Click += (_, _) => LabProfile.Forget(_session.Lab.Id, text);
            menu.Items.Add(open);
            menu.Items.Add(remove);
            item.ContextMenu = menu;
            _list.Items.Add(item);
        }
    }
}

/// <summary>Settings kept under a name -- the Lab's parameters, a filter, a tool's options -- in the person's profile: pick the kind, save what it is now, apply one.</summary>
internal sealed class LabPresetsInspector : SharedPane
{
    private readonly ILabSession   _session;
    private readonly ILabPresets   _source;
    private          ComboBox?     _kinds;
    private          ListBox?      _list;
    private          TextBox?      _name;
    private          string?       _kind;

    public LabPresetsInspector(ILabSession session, ILabPresets source)
    {
        _session = session;
        _source  = source;
        LabProfile.Changed += () => Application.Current?.Dispatcher.InvokeAsync(Poke);
    }

    public const string InspectorId = "presets";

    public override string Id => InspectorId;

    public override string Title => "Presets";

    public override string IconKey => "Switches";

    public override LabDock Dock => LabDock.Left;

    public override bool VisibleByDefault => false;

    private LabPresetKind? Current => _source.PresetKinds.FirstOrDefault(k => k.Id == _kind) ?? _source.PresetKinds.FirstOrDefault();

    protected override UIElement Create()
    {
        var kinds = _source.PresetKinds;
        _kind = kinds.FirstOrDefault()?.Id;

        _kinds = LabUi.Combo(kinds.Select(k => k.Title), kinds.FirstOrDefault()?.Title, title =>
                                                                                      {
                                                                                          _kind = _source.PresetKinds.FirstOrDefault(k => k.Title == title)?.Id;
                                                                                          Refresh();
                                                                                      }, 200);
        _kinds.Margin = new Thickness(2, 0, 6, 0);
        _name         = LabUi.Line(string.Empty, _ => { }, 160, "The name to keep the settings under");
        var save = LabUi.Tool("Save", "TbSave", SaveCurrent, "Keep the settings as they are now under this name");

        var top = new StackPanel();
        top.Children.Add(LabUi.Bar(LabUi.Label("Settings of"), _kinds));
        top.Children.Add(LabUi.Bar(_name, save));

        _list = LabUi.List();
        _list.MouseDoubleClick += (_, _) => ApplySelected();
        _list.KeyDown += (_, e) =>
                         {
                             if (e.Key == Key.Enter) ApplySelected();
                             else if (e.Key == Key.Delete) DeleteSelected();
                         };

        var bottom = LabUi.Bar(LabUi.Tool("Apply", "Check", ApplySelected, "Put the settings back as the chosen preset has them"),
                               LabUi.Tool("Delete", "TbDelete", DeleteSelected, "Forget the chosen preset"));
        return LabUi.Layout(top, _list, bottom);
    }

    private void SaveCurrent()
    {
        if (Current is not { } kind || _name is null) return;

        try
        {
            LabProfile.SavePreset(_session.Lab.Id, kind.Id, _name.Text.Trim(), kind.Capture());
            _name.Clear();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Keeping a preset failed");
            (_session as ILabReporter)?.Report("Could not keep the preset: " + ex.Message);
        }
    }

    private void ApplySelected()
    {
        if (Current is not { } kind || (_list?.SelectedItem as ListBoxItem)?.Tag is not string name) return;

        try
        {
            if (LabProfile.GetPreset(_session.Lab.Id, kind.Id, name) is { } settings)
            {
                kind.Apply(settings);
                (_session as ILabReporter)?.Report($"Applied {name}");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Applying a preset failed");
            (_session as ILabReporter)?.Report("Could not apply the preset: " + ex.Message);
        }
    }

    private void DeleteSelected()
    {
        if (Current is not { } kind || (_list?.SelectedItem as ListBoxItem)?.Tag is not string name) return;

        if (LabUi.Confirm($"Forget the preset '{name}'?", "Presets")) LabProfile.DeletePreset(_session.Lab.Id, kind.Id, name);
    }

    protected override void Refresh()
    {
        if (_list is null) return;

        _list.Items.Clear();
        if (Current is not { } kind)
        {
            _list.Items.Add(LabUi.Note($"{_session.Lab.Name} has no settings to keep."));
            return;
        }

        var names = LabProfile.PresetNames(_session.Lab.Id, kind.Id);
        if (names.Count == 0)
        {
            _list.Items.Add(LabUi.Note($"None yet. Set {kind.Title.ToLowerInvariant()} as you like, give it a name and press Save."));
            return;
        }

        foreach (string name in names)
            _list.Items.Add(LabUi.Item(name, LabUi.Row(kind.IconKey ?? "Switches", name, null, null), "Double-click to apply"));
    }
}

/// <summary>What is wrong, like the Problems of an editor: validation errors, failed navigations, script errors, commands that failed. Double-click one to go to it.</summary>
internal sealed class LabProblemsInspector : SharedPane
{
    private readonly ILabProblems _source;
    private          ListBox?     _list;
    private          TextBlock?   _summary;

    public LabProblemsInspector(ILabProblems source)
    {
        _source = source;
    }

    public const string InspectorId = "problems";

    public override string Id => InspectorId;

    public override string Title => "Problems";

    public override string IconKey => "StateFailed";

    public override LabDock Dock => LabDock.Bottom;

    public override bool VisibleByDefault => false;

    public override LabPaneKind Kind => LabPaneKind.Other;

    protected override UIElement Create()
    {
        _summary        = LabUi.Label(string.Empty, muted: true);
        _summary.Margin = new Thickness(8, 6, 8, 4);
        var bar = LabUi.Bar(LabUi.Tool("Clear", "TbDelete", () => _source.ClearProblems(), "Forget the problems that were reported (the ones found by checking the value stay while they are true)"),
                            LabUi.Tool("Copy", "TbCopy", () => LabUi.Copy(string.Join(Environment.NewLine, _source.ProblemList.Select(Describe))), "Copy the list as text"));
        var top = new StackPanel();
        top.Children.Add(bar);
        top.Children.Add(_summary);

        _list = LabUi.List();
        _list.MouseDoubleClick += (_, _) => GoToSelected();
        _list.KeyDown += (_, e) =>
                         {
                             if (e.Key == Key.Enter) GoToSelected();
                         };
        return LabUi.Layout(top, _list);
    }

    private static string Describe(LabProblem p) => $"{p.Severity}: {p.Source}{(p.Where is null ? string.Empty : " " + p.Where)}: {p.Message}";

    private void GoToSelected()
    {
        if ((_list?.SelectedItem as ListBoxItem)?.Tag is LabProblem { Go: { } go }) go();
    }

    protected override void Refresh()
    {
        if (_list is null || _summary is null) return;

        var problems = _source.ProblemList;
        int errors   = problems.Count(p => p.Severity == LabProblemSeverity.Error);
        int warnings = problems.Count(p => p.Severity == LabProblemSeverity.Warning);
        _summary.Text = problems.Count == 0 ? "Nothing is wrong." : $"{errors} error(s), {warnings} warning(s), {problems.Count - errors - warnings} note(s)";

        _list.Items.Clear();
        foreach (var p in problems.OrderByDescending(x => x.Severity))
        {
            var tint = p.Severity switch { LabProblemSeverity.Error => LabUi.Red, LabProblemSeverity.Warning => LabUi.Amber, _ => LabUi.Gray };
            var icon = p.Severity == LabProblemSeverity.Error ? "StateFailed" : "Check";
            var entry = new StackPanel();
            entry.Children.Add(LabUi.Row(icon, p.Where is null ? p.Source : $"{p.Source}: {p.Where}", p.Severity.ToString(), null, iconBrush: tint, bold: true));
            entry.Children.Add(LabUi.Label(p.Message, wrap: true).Also(l => l.Margin = new Thickness(24, 0, 4, 3)));
            _list.Items.Add(LabUi.Item(p, entry, p.Go is null ? null : "Double-click to go to it"));
        }
    }
}

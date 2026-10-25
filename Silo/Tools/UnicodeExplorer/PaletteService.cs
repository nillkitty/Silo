using System.Windows.Media;
using Silo.Hotkeys;
using Silo.Storage;
using Silo.Ui.Windows;
using Silo.Unicode;

namespace Silo.Tools.UnicodeExplorer;

/// <summary>
///     Keeps the palettes of the open Silo and their windows. Palettes belong to the Silo (so each repository has its own); the ones marked "Show on repo
///     load" open when a Silo is loaded, whether or not the Unicode Explorer itself is open.
/// </summary>
internal sealed class PaletteService
{
    private const string PalettesKey = "unicodeexplorer:palettes";
    private const string SettingsKey = "unicodeexplorer:settings";
    private const string ColorsKey   = "unicodeexplorer:colors";

    public static PaletteService Current { get; } = new();

    private readonly List<PaletteDefinition>                     _palettes = [];
    private readonly Dictionary<PaletteDefinition, PaletteWindow> _open     = [];
    private bool                                                  _started;
    private bool                                                  _loading;

    private PaletteService()
    {
    }

    /// <summary>Raised when palettes were added, removed, renamed or opened or closed (the explorer's Palette menu reads its state when it opens, so this is for code that wants to know).</summary>
    public event EventHandler? Changed;

    /// <summary>The palettes of the open Silo.</summary>
    public IReadOnlyList<PaletteDefinition> All => _palettes;

    /// <summary>The palette the Palette menu's choices (rename, on top, on screen, show on load) apply to: the one that was used last.</summary>
    public PaletteDefinition? Selected { get; private set; }

    public bool IsOpen(PaletteDefinition palette) => _open.ContainsKey(palette);

    /// <summary>Starts watching for the Silo to change; palettes marked for it are shown. Safe to call more than once.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;

        if (App.Instance is { } app) app.ActiveSiloChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(Reload);
        Application.Current?.Dispatcher.BeginInvoke(Reload);
    }

    /// <summary>Closes the windows and reads the palettes of the Silo that is open now; the ones marked for it open.</summary>
    public void Reload()
    {
        _loading = true;
        try
        {
            foreach (var w in _open.Values.ToList()) w.Close();
            _open.Clear();
            _palettes.Clear();
            Selected = null;

            try
            {
                var stored = Stored.At<List<PaletteDefinition>>(PalettesKey);
                if (stored.IsAvailable && stored.Get() is { } kept) _palettes.AddRange(kept.Where(p => p is not null));
            }
            catch (Exception)
            {
                // unreadable: no palettes rather than a failure at start
            }

            foreach (var p in _palettes) PaletteRules.Normalize(p);
            Selected = _palettes.FirstOrDefault();
        }
        finally
        {
            _loading = false;
        }

        foreach (var p in _palettes.Where(p => p.ShowOnLoad)) Open(p);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Keeps the palettes in the Silo; skipped when none is open.</summary>
    public void Save()
    {
        if (_loading) return;

        try
        {
            var stored = Stored.At<List<PaletteDefinition>>(PalettesKey);
            if (!stored.IsAvailable) return;

            if (_palettes.Count == 0) stored.Delete();
            else stored.Set(_palettes);
        }
        catch (Exception)
        {
            // the palettes still work this session
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Select(PaletteDefinition palette)
    {
        if (!ReferenceEquals(Selected, palette)) Selected = palette;
    }

    /// <summary>Makes a palette, opens its window and returns it.</summary>
    public PaletteDefinition Create(string? name, IEnumerable<int> codePoints)
    {
        var palette = new PaletteDefinition
                      {
                          Name  = PaletteRules.UniqueName(_palettes.Select(p => p.Name), name),
                          Pages = [new PalettePage { Name = "Glyphs", CodePoints = codePoints.Distinct().ToList() }]
                      };
        _palettes.Add(palette);
        Selected = palette;
        Save();
        Open(palette);
        return palette;
    }

    public void Open(PaletteDefinition palette)
    {
        if (_open.TryGetValue(palette, out var existing))
        {
            existing.Apply();
            return;
        }

        var window = new PaletteWindow(this, palette);
        window.Closed += (_, _) =>
                         {
                             _open.Remove(palette);
                             Changed?.Invoke(this, EventArgs.Empty);
                         };
        _open[palette] = window;
        window.Show();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Close(PaletteDefinition palette)
    {
        if (_open.TryGetValue(palette, out var w)) w.Close();
    }

    /// <summary>Saves and redraws a palette's window (after its characters or pages changed).</summary>
    public void Touched(PaletteDefinition palette)
    {
        Save();
        if (_open.TryGetValue(palette, out var w)) w.Rebuild();
    }

    /// <summary>Saves and applies a palette's window choices (on top, taskbar, locks, tile size).</summary>
    public void Restyled(PaletteDefinition palette)
    {
        Save();
        if (_open.TryGetValue(palette, out var w))
        {
            w.Apply();
            w.Rebuild();
        }
    }

    /// <summary>Redraws every open palette (the colors or the color mode changed).</summary>
    public void RefreshAll()
    {
        foreach (var w in _open.Values) w.Rebuild();
    }

    // ---- characters (on the page shown) --------------------------------------------------------------------------

    /// <summary>Adds characters at the end of the page shown (ones already on it are left out).</summary>
    public void Add(PaletteDefinition palette, IEnumerable<int> codePoints)
    {
        var page = palette.Page();
        PaletteRules.InsertNew(page.CodePoints, codePoints, page.CodePoints.Count);
        Touched(palette);
    }

    /// <summary>Inserts characters on the page shown at <paramref name="at" /> (ones already on it are left out); returns how many were added.</summary>
    public int Insert(PaletteDefinition palette, IEnumerable<int> codePoints, int at)
    {
        int added = PaletteRules.InsertNew(palette.Page().CodePoints, codePoints, at);
        Touched(palette);
        return added;
    }

    /// <summary>Moves tiles of the page shown to <paramref name="at" /> (an index into the page as it was).</summary>
    public void MoveTiles(PaletteDefinition palette, IEnumerable<int> indexes, int at)
    {
        PaletteRules.Move(palette.Page().CodePoints, indexes, at);
        Touched(palette);
    }

    public void Remove(PaletteDefinition palette, int codePoint)
    {
        palette.Page().CodePoints.Remove(codePoint);
        Touched(palette);
    }

    // ---- pages -------------------------------------------------------------------------------------------------

    public void ShowPage(PaletteDefinition palette, int index)
    {
        if (index < 0 || index >= palette.Pages.Count || index == palette.CurrentPage) return;

        palette.CurrentPage = index;
        Touched(palette);
    }

    public void AddPage(PaletteDefinition palette, Window? owner = null)
    {
        string? name = PromptDialog.Ask("Add tab page", $"What is the new page of \"{palette.Name}\" called?", "Add", "Page " + (palette.Pages.Count + 1));
        if (name is null) return;

        palette.Pages.Add(new PalettePage { Name = PaletteRules.UniqueName(palette.Pages.Select(p => p.Name), name, "Page") });
        palette.CurrentPage = palette.Pages.Count - 1;
        Touched(palette);
    }

    public void RenamePage(PaletteDefinition palette, Window? owner = null)
    {
        var page = palette.Page();
        string? name = PromptDialog.Ask("Rename page", $"A new name for the page \"{page.Name}\":", "Rename", page.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == page.Name) return;

        page.Name = PaletteRules.UniqueName(palette.Pages.Where(p => !ReferenceEquals(p, page)).Select(p => p.Name), name, "Page");
        Touched(palette);
    }

    public void DeletePage(PaletteDefinition palette, Window? owner = null)
    {
        if (palette.Pages.Count <= 1)
        {
            PromptDialog.Show("Delete page", "A palette keeps at least one page. Delete the palette instead, or add another page first.", ["OK"]);
            return;
        }

        var page = palette.Page();
        if (page.CodePoints.Count > 0)
        {
            var (button, _) = PromptDialog.Show("Delete page", $"Delete the page \"{page.Name}\" and its {page.CodePoints.Count:N0} character{(page.CodePoints.Count == 1 ? "" : "s")}?", ["Delete", "Cancel"]);
            if (button != 0) return;
        }

        palette.Pages.RemoveAt(palette.CurrentPage);
        palette.CurrentPage = Math.Clamp(palette.CurrentPage, 0, palette.Pages.Count - 1);
        Touched(palette);
    }

    /// <summary>Moves a page to another place in the tab order; the page shown stays shown.</summary>
    public void MovePage(PaletteDefinition palette, int from, int to)
    {
        if (from < 0 || from >= palette.Pages.Count || to < 0 || to >= palette.Pages.Count || from == to) return;

        var shown = palette.Page();
        var page  = palette.Pages[from];
        palette.Pages.RemoveAt(from);
        palette.Pages.Insert(to, page);
        palette.CurrentPage = palette.Pages.IndexOf(shown);
        Touched(palette);
    }

    // ---- the palette ----------------------------------------------------------------------------------------------

    /// <summary>Asks for a new name (the owner window keeps the dialog in front of a palette that is always on top).</summary>
    public void Rename(PaletteDefinition palette, Window? owner = null)
    {
        string? name = PromptDialog.Ask("Rename palette", $"A new name for \"{palette.Name}\":", "Rename", palette.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == palette.Name) return;

        palette.Name = PaletteRules.UniqueName(_palettes.Where(p => !ReferenceEquals(p, palette)).Select(p => p.Name), name);
        Save();
        if (_open.TryGetValue(palette, out var w)) w.Apply();
    }

    public void Delete(PaletteDefinition palette, Window? owner = null)
    {
        var (button, _) = PromptDialog.Show("Delete palette", $"Delete the palette \"{palette.Name}\"? The characters themselves are not affected.", ["Delete", "Cancel"]);
        if (button != 0) return;

        Close(palette);
        _palettes.Remove(palette);
        if (ReferenceEquals(Selected, palette)) Selected = _palettes.FirstOrDefault();
        Save();
    }

    /// <summary>A character was clicked: it goes on the clipboard and, unless the Preferences say not to, is typed into the window that has the focus.</summary>
    public void Pick(string text)
    {
        UiKit.TryCopy(text);
        if (!TypesIntoWindow()) return;

        try
        {
            NativeKeyboard.Type(text);
        }
        catch (Exception)
        {
            // it is on the clipboard: paste it
        }
    }

    private static bool TypesIntoWindow()
    {
        try
        {
            return Stored.At<ExplorerSettings>(SettingsKey).Get()?.PaletteTypes ?? true;
        }
        catch (Exception)
        {
            return true;
        }
    }

    // ---- colors (the explorer's marks, shown on the tiles) -------------------------------------------------------

    /// <summary>The explorer's color marks and color mode, read fresh (they are small): a function giving a character's color, or null for none.</summary>
    public (Func<int, Color?> ColorOf, ColorMode Mode) Colors()
    {
        var mode = ColorMode.Glyph;
        try
        {
            mode = Stored.At<ExplorerSettings>(SettingsKey).Get()?.ColorMode ?? ColorMode.Glyph;
        }
        catch (Exception)
        {
            // the default mode
        }

        CharacterColorMap map;
        try
        {
            var stored = Stored.At<Dictionary<int, int>>(ColorsKey);
            map = stored.IsAvailable && stored.Get() is { } kept ? CharacterColorMap.From(kept) : new CharacterColorMap();
        }
        catch (Exception)
        {
            map = new CharacterColorMap();
        }

        return (cp => map.ColorOf(cp) is { } c ? UiKit.ToColor(c) : null, mode);
    }
}

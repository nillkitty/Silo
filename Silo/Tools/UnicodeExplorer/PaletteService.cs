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

            foreach (var p in _palettes)
            {
                p.CodePoints ??= [];
                p.Name       =  string.IsNullOrWhiteSpace(p.Name) ? "Palette" : p.Name;
            }

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
        var palette = new PaletteDefinition { Name = PaletteRules.UniqueName(_palettes.Select(p => p.Name), name), CodePoints = codePoints.Distinct().ToList() };
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
            existing.Topmost = palette.AlwaysOnTop;
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

    public void Add(PaletteDefinition palette, IEnumerable<int> codePoints)
    {
        var have = palette.CodePoints.ToHashSet();
        foreach (int cp in codePoints)
            if (have.Add(cp))
                palette.CodePoints.Add(cp);

        Save();
        if (_open.TryGetValue(palette, out var w)) w.Rebuild();
    }

    public void Remove(PaletteDefinition palette, int codePoint)
    {
        palette.CodePoints.Remove(codePoint);
        Save();
        if (_open.TryGetValue(palette, out var w)) w.Rebuild();
    }

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
}

using System.Windows.Controls;
using Silo.Storage;
using Silo.Unicode;

namespace Silo.Tools.UnicodeExplorer;

/// <summary>
///     The explorer's own choices (see <see cref="ExplorerSettings" />): which blocks are switched off, how marks are colored, list or grid, which panes
///     are shown. Kept in the Silo; what the menus show is read from here when they open.
/// </summary>
public sealed partial class UnicodeExplorerView
{
    private const string SettingsKey        = "unicodeexplorer:settings";
    private const string LegacyHideGlyphKey = "unicodeexplorer:hideglyphless"; // before the Glyph Filter menu: "yes" or "no"
    private const double DefaultInspectorWidth = 340;
    private const double DefaultBottomHeight   = 210;

    private ExplorerSettings _settings = new();
    private CodePointSet?    _disabledBlocks;     // the code points of the blocks that are switched off; null when none is
    private bool             _settingsLoading;
    private double           _inspectorWidth = DefaultInspectorWidth;
    private double           _bottomHeight   = DefaultBottomHeight;

    // ---- keeping them --------------------------------------------------------------------------------------------

    /// <summary>Reads the settings from the Silo (the starting ones when none are kept) and applies them.</summary>
    private void LoadSettings()
    {
        _settingsLoading = true;
        try
        {
            ExplorerSettings? loaded = null;
            try
            {
                var stored = Stored.At<ExplorerSettings>(SettingsKey);
                if (stored.IsAvailable) loaded = stored.Get();

                if (loaded is null)
                {
                    loaded = new ExplorerSettings();
                    // the one choice an older version kept: whether to hide the characters no font can draw
                    if (Stored.At<string>(LegacyHideGlyphKey).Get() == "no") loaded.Kind = GlyphKind.AssignedOnly;
                }
            }
            catch (Exception)
            {
                loaded = new ExplorerSettings(); // unreadable: start over rather than fail to open
            }

            loaded.Blocks ??= new BlockOverrides();
            loaded.Blocks.ForceOn  ??= [];
            loaded.Blocks.ForceOff ??= [];
            loaded.Page            ??= new PageSetup();
            _settings    = loaded;
            _filter.Kind = _settings.Kind;
        }
        finally
        {
            _settingsLoading = false;
        }

        ApplySettings();
        UpdateFilterNote();
        RebuildBlockPolicy();
        Schedule(immediate: true);
    }

    private void SaveSettings()
    {
        if (_settingsLoading) return;

        _settings.Kind     = _filter.Kind;
        _settings.ListView = _view.ListMode;
        try
        {
            var stored = Stored.At<ExplorerSettings>(SettingsKey);
            if (stored.IsAvailable) stored.Set(_settings);
        }
        catch (Exception)
        {
            // the choices still hold for this session
        }
    }

    /// <summary>Makes the window show what the settings say.</summary>
    private void ApplySettings()
    {
        _view.ListMode  = _settings.ListView;
        _view.ColorMode = _settings.ColorMode;
        ApplyPanes();
        if (_tray.Count > 0) RefreshTray();
        UpdateColorStrip();
    }

    // ---- blocks switched off -------------------------------------------------------------------------------------

    /// <summary>Works out the code points of the blocks that are switched off, after the choices or the data changed.</summary>
    private void RebuildBlockPolicy()
    {
        if (_db is null) return;

        var set = BlockPolicy.DisabledCodePoints(_db.Blocks, _settings.Blocks);
        _disabledBlocks = set.Count == 0 ? null : set;
    }

    /// <summary>
    ///     The blocks to leave out of a list. For "go to" the block of the code point asked for is not among them, so the character is there to be
    ///     found even when its block is switched off.
    /// </summary>
    private CodePointSet? DisabledBlocksFor(int? goTo)
    {
        if (_db is null) return null;
        if (goTo is not { } cp || _disabledBlocks?.Contains(cp) != true) return _disabledBlocks;

        var set = BlockPolicy.DisabledCodePoints(_db.Blocks.Where(b => !b.Contains(cp)), _settings.Blocks);
        return set.Count == 0 ? null : set;
    }

    private void ManageBlocks()
    {
        if (_db is null)
        {
            Say("The Unicode data is still loading.");
            return;
        }

        if (ManageBlocksDialog.Ask(DialogOwner, _db.Blocks, _settings.Blocks) is not { } chosen) return;

        _settings.Blocks = chosen;
        SaveSettings();
        RebuildBlockPolicy();
        Schedule(immediate: true);
        int off = BlockPolicy.DisabledNames(_db.Blocks, chosen).Count;
        Say(off == 0 ? "All blocks are on." : $"{off:N0} block{(off == 1 ? " is" : "s are")} switched off.");
    }

    /// <summary>Switches one block on or off (from the Blocks menu's context menu).</summary>
    private void SetBlockEnabled(string block, bool on)
    {
        if (_db is null) return;

        var overrides = _settings.Blocks;
        overrides.ForceOn.RemoveAll(n => n.Equals(block, StringComparison.OrdinalIgnoreCase));
        overrides.ForceOff.RemoveAll(n => n.Equals(block, StringComparison.OrdinalIgnoreCase));
        bool byDefault = !BlockPolicy.IsOffByDefault(block);
        if (on && !byDefault) overrides.ForceOn.Add(block);
        if (!on && byDefault) overrides.ForceOff.Add(block);

        SaveSettings();
        RebuildBlockPolicy();
        Schedule(immediate: true);
    }

    // ---- color mode ----------------------------------------------------------------------------------------------

    private void SetColorMode(ColorMode mode)
    {
        if (_settings.ColorMode == mode) return;

        _settings.ColorMode = mode;
        SaveSettings();
        _view.ColorMode = mode;
        PaletteService.Current.RefreshAll();
        if (_tray.Count > 0) RefreshTray();
        UpdateColorStrip();
    }

    // ---- panes ---------------------------------------------------------------------------------------------------

    /// <summary>Shows and hides the parts of the window the View menu switches: the code point list, the details, and the tabs below.</summary>
    private void ApplyPanes()
    {
        if (_middle is null) return;

        bool list = _settings.ShowCodepoints, details = _settings.ShowDetails;

        // remember how wide the details were before the list goes (it takes the whole width then, and the width comes back with the list)
        if (_inspector.Visibility == Visibility.Visible && _middle.ColumnDefinitions[0].Width.IsStar && _middle.ColumnDefinitions[2].ActualWidth > 0)
            _inspectorWidth = _middle.ColumnDefinitions[2].ActualWidth;

        _browser.Visibility   = list ? Visibility.Visible : Visibility.Collapsed;
        _inspector.Visibility = details ? Visibility.Visible : Visibility.Collapsed;
        _vsplit.Visibility    = list && details ? Visibility.Visible : Visibility.Collapsed;

        var browserColumn   = _middle.ColumnDefinitions[0];
        var inspectorColumn = _middle.ColumnDefinitions[2];
        browserColumn.Width       = list ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        browserColumn.MinWidth    = list ? 260 : 0;
        _middle.ColumnDefinitions[1].Width = GridLength.Auto;
        inspectorColumn.Width     = !details ? new GridLength(0) : list ? new GridLength(_inspectorWidth) : new GridLength(1, GridUnitType.Star);
        inspectorColumn.MinWidth  = details && list ? 220 : 0;

        // the tabs below
        // a tab whose contents are undocked has a pane of its own (see .Dock)
        bool anyTab = false;
        foreach (var part in _parts)
        {
            bool here = part.IsShown(_settings) && part.Pane is null;
            part.Tab.Visibility = here ? Visibility.Visible : Visibility.Collapsed;
            anyTab |= here;
        }

        if (_bottomTabs.SelectedItem is TabItem { Visibility: not Visibility.Visible } || _bottomTabs.SelectedItem is null)
            _bottomTabs.SelectedItem = _parts.Select(p => p.Tab).FirstOrDefault(t => t.Visibility == Visibility.Visible);

        bool anyTop = list || details;
        var bottomRow = _rootGrid.RowDefinitions[3];
        if (bottomRow.ActualHeight > 0 && anyTop && anyTab && bottomRow.Height.IsAbsolute) _bottomHeight = bottomRow.Height.Value;

        _bottomTabs.Visibility = anyTab ? Visibility.Visible : Visibility.Collapsed;
        _hsplit.Visibility     = anyTop && anyTab ? Visibility.Visible : Visibility.Collapsed;
        _middle.Visibility     = anyTop ? Visibility.Visible : Visibility.Collapsed;
        _rootGrid.RowDefinitions[1].Height    = anyTop ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        _rootGrid.RowDefinitions[1].MinHeight = anyTop ? 120 : 0;
        bottomRow.Height    = !anyTab ? new GridLength(0) : anyTop ? new GridLength(_bottomHeight) : new GridLength(1, GridUnitType.Star);
        bottomRow.MinHeight = anyTab && anyTop ? 110 : 0;
    }

    private void SetPane(Action<ExplorerSettings> change)
    {
        change(_settings);
        SaveSettings();
        ApplyPanes();
    }

    /// <summary>View &gt; Reset Inner Layout: every pane shown, at the sizes it starts with.</summary>
    private void ResetInnerLayout()
    {
        _settings.ShowCodepoints = _settings.ShowDetails = _settings.ShowCollection = _settings.ShowGenerators = _settings.ShowSaved = true;
        _inspectorWidth = DefaultInspectorWidth;
        _bottomHeight   = DefaultBottomHeight;
        SaveSettings();
        ApplyPanes();
        _middle.ColumnDefinitions[2].Width = new GridLength(DefaultInspectorWidth);
        _rootGrid.RowDefinitions[3].Height = new GridLength(DefaultBottomHeight);
        Say("The panes are back at their starting sizes.");
    }
}

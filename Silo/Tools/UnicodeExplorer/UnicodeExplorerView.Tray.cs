using System.IO;
using System.Text;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Silo.Storage;
using Silo.Ui.Windows;
using Silo.Unicode;

namespace Silo.Tools.UnicodeExplorer;

public sealed partial class UnicodeExplorerView
{
    private const string TrayKey = "unicodeexplorer:tray";

    /// <summary>The most characters the collection holds; every one is drawn, so it has to stay reasonable.</summary>
    private const int TrayLimit = 5000;

    private const double TrayCell = 44;

    private readonly List<CollectedCharacter> _tray    = [];
    private readonly HashSet<int>             _traySel = [];
    private readonly List<Border>             _trayCells = [];
    private readonly WrapPanel                _trayPanel = new() { Margin = new Thickness(2) };
    private readonly ScrollViewer             _trayScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = true };
    private readonly TextBlock                _trayTitle = UiKit.Text("Collection", weight: FontWeights.SemiBold);
    private readonly TextBlock                _trayHint  = UiKit.Text("Double-click a character above (or press Enter) to collect it. Paste text to collect its characters.", muted: true, wrap: true);
    private int                               _trayAnchor = -1;
    private bool                              _trayLoading;

    // ---- building ------------------------------------------------------------------------------------------------

    private UIElement BuildTray()
    {
        var root = new DockPanel();

        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        _trayTitle.Margin = new Thickness(0, 0, 12, 0);
        bar.Children.Add(_trayTitle);
        bar.Children.Add(UiKit.MenuBtn("Copy", CopyMenuItems, "Copy the selected characters (or all of them) in a form you choose"));
        bar.Children.Add(UiKit.Btn("Paste text", PasteIntoTray, "Collect every character of the text on the clipboard"));
        bar.Children.Add(UiKit.Btn("Save As…", SaveTrayAs, "Save the collection to a file (text, JSON, TSV, CSV or a list of code points)"));
        bar.Children.Add(UiKit.Btn("Open…", OpenIntoTray, "Load characters from a file"));
        bar.Children.Add(UiKit.MenuBtn("Silo", SiloMenuItems, "Keep the collection in your Silo: as a list, as a constant, or on a hotkey"));
        bar.Children.Add(UiKit.Btn("Remove", RemoveSelected, "Remove the selected characters (Delete)"));
        bar.Children.Add(UiKit.Btn("Clear", ClearTray, "Remove all of them"));
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);

        var frame = new Border { BorderThickness = new Thickness(1) };
        frame.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
        frame.SetResourceReference(Border.BackgroundProperty, "Theme.BackgroundAlt");
        var inner = new Grid();
        _trayHint.Margin = new Thickness(8);
        _trayHint.VerticalAlignment = VerticalAlignment.Center;
        _trayHint.HorizontalAlignment = HorizontalAlignment.Center;
        inner.Children.Add(_trayHint);
        _trayScroll.Content = _trayPanel;
        inner.Children.Add(_trayScroll);
        frame.Child = inner;
        root.Children.Add(frame);

        _trayScroll.PreviewKeyDown += OnTrayKeyDown;
        _trayScroll.MouseDown      += (_, _) => _trayScroll.Focus();
        _trayScroll.MouseLeftButtonDown += (_, e) =>
                                           {
                                               if (ReferenceEquals(e.OriginalSource, _trayScroll) || ReferenceEquals(e.OriginalSource, _trayPanel)) SelectTray(-1);
                                           };
        _trayScroll.ContextMenu = new ContextMenu();
        _trayScroll.ContextMenuOpening += (_, e) =>
                                          {
                                              // the cells have menus of their own; this one is for the empty area
                                              _trayScroll.ContextMenu!.Items.Clear();
                                              _trayScroll.ContextMenu.Items.Add(UiKit.Item("Paste text", PasteIntoTray));
                                              _trayScroll.ContextMenu.Items.Add(UiKit.Item("Open…", OpenIntoTray));
                                          };
        RefreshTray();
        return root;
    }

    // ---- the collection ------------------------------------------------------------------------------------------

    /// <summary>The characters to act on: the selected ones, or all of them when none is selected.</summary>
    private IReadOnlyList<CollectedCharacter> TrayTarget()
    {
        return _traySel.Count == 0 ? _tray.ToList() : _traySel.OrderBy(i => i).Where(i => i < _tray.Count).Select(i => _tray[i]).ToList();
    }

    private bool HasTrayRoom(int adding)
    {
        if (_tray.Count + adding <= TrayLimit) return true;
        Say($"The collection holds up to {TrayLimit:N0} characters.");
        return false;
    }

    /// <summary>Adds a character to the collection; one that is already there is selected instead.</summary>
    private void AddToTray(CodePoint cp)
    {
        int at = _tray.FindIndex(c => c.CodePoint == cp);
        if (at >= 0)
        {
            SelectTray(at);
            Say($"{cp.Hex} is already in the collection.");
            return;
        }

        if (!HasTrayRoom(1)) return;

        _tray.Add(new CollectedCharacter(cp));
        RefreshTray();
        SelectTray(_tray.Count - 1);
        _trayScroll.ScrollToEnd();
        SaveTray();
    }

    private void AddPageToTray()
    {
        int first = _page * _pageSize;
        AddMany(_results.Skip(first).Take(_pageSize).Where(IsCollectable), "this page");
    }

    private void AddAllResultsToTray()
    {
        var all = _results.Where(IsCollectable).ToArray();
        if (all.Length > 1000)
        {
            var (button, _) = PromptDialog.Show("Add all results", $"Add {all.Length:N0} characters to the collection?", ["Add", "Cancel"]);
            if (button != 0) return;
        }

        AddMany(all, "the results");
    }

    private bool IsCollectable(int cp) => _db?.Get(cp).IsAssigned ?? true;

    private void AddMany(IEnumerable<int> values, string from)
    {
        var have  = _tray.Select(c => c.CodePoint.Value).ToHashSet();
        var fresh = values.Where(v => have.Add(v)).ToList();
        if (fresh.Count == 0)
        {
            Say($"Everything from {from} is already in the collection.");
            return;
        }

        if (!HasTrayRoom(fresh.Count)) return;

        foreach (int v in fresh) _tray.Add(new CollectedCharacter(new CodePoint(v)));
        RefreshTray();
        SaveTray();
        Say($"Added {fresh.Count:N0} character{(fresh.Count == 1 ? "" : "s")} from {from}.");
    }

    private void PasteIntoTray()
    {
        string? text;
        try
        {
            text = Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            Say("The clipboard is busy.");
            return;
        }

        if (string.IsNullOrEmpty(text))
        {
            Say("There is no text on the clipboard.");
            return;
        }

        // pasting keeps the text as it is, order and repeats included
        var items = CodePoint.Enumerate(text).Select(c => new CollectedCharacter(c)).ToList();
        if (!HasTrayRoom(items.Count)) return;

        _tray.AddRange(items);
        RefreshTray();
        SaveTray();
        Say($"Added {items.Count:N0} character{(items.Count == 1 ? "" : "s")} from the clipboard.");
    }

    private void RemoveSelected()
    {
        if (_traySel.Count == 0)
        {
            Say("Select the characters to remove first.");
            return;
        }

        int first = _traySel.Min();
        foreach (int i in _traySel.OrderByDescending(i => i)) if (i < _tray.Count) _tray.RemoveAt(i);
        _traySel.Clear();
        RefreshTray();
        SelectTray(_tray.Count == 0 ? -1 : Math.Min(first, _tray.Count - 1));
        SaveTray();
    }

    private void ClearTray()
    {
        if (_tray.Count == 0) return;
        if (_tray.Count > 1)
        {
            var (button, _) = PromptDialog.Show("Clear the collection", $"Remove all {_tray.Count:N0} characters from the collection?", ["Clear", "Cancel"]);
            if (button != 0) return;
        }

        _tray.Clear();
        _traySel.Clear();
        RefreshTray();
        SaveTray();
    }

    private void MoveSelected(int by)
    {
        if (_traySel.Count == 0) return;

        var order = by < 0 ? _traySel.OrderBy(i => i).ToList() : _traySel.OrderByDescending(i => i).ToList();
        if (order.Any(i => i + by < 0 || i + by >= _tray.Count)) return;

        foreach (int i in order) (_tray[i], _tray[i + by]) = (_tray[i + by], _tray[i]);
        var moved = _traySel.Select(i => i + by).ToList();
        _traySel.Clear();
        foreach (int i in moved) _traySel.Add(i);
        _trayAnchor += by;
        RefreshTray();
        SaveTray();
    }

    private void SortTray()
    {
        var sorted = _tray.OrderBy(c => c.CodePoint.Value).ToList();
        _tray.Clear();
        _tray.AddRange(sorted);
        _traySel.Clear();
        RefreshTray();
        SaveTray();
    }

    private void RemoveTrayDuplicates()
    {
        var seen  = new HashSet<int>();
        int before = _tray.Count;
        _tray.RemoveAll(c => !seen.Add(c.CodePoint.Value));
        _traySel.Clear();
        RefreshTray();
        SaveTray();
        Say(before == _tray.Count ? "There were no repeated characters." : $"Removed {before - _tray.Count:N0} repeated character{(before - _tray.Count == 1 ? "" : "s")}.");
    }

    private void LabelSelected()
    {
        if (_traySel.Count != 1) return;

        int at = _traySel.First();
        var item = _tray[at];
        string? label = PromptDialog.Ask("Label", $"Your own name for {item.CodePoint.Hex} in this collection (leave empty for none):", "OK", item.Label ?? string.Empty);
        if (label is null) return;

        _tray[at] = item with { Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim() };
        RefreshTray();
        SaveTray();
    }

    // ---- showing it ----------------------------------------------------------------------------------------------

    private void RefreshTray()
    {
        _trayPanel.Children.Clear();
        _trayCells.Clear();
        _traySel.RemoveWhere(i => i >= _tray.Count);

        for (int i = 0; i < _tray.Count; i++)
        {
            int index = i;
            var item  = _tray[i];
            var info  = _db?.Get(item.CodePoint);
            var cell  = UiKit.Cell(info?.Glyph ?? item.CodePoint.Text, 20, TrayCell, TrayCell, string.IsNullOrEmpty(item.Label) ? null : item.Label);
            cell.ToolTip = $"{item.CodePoint.Hex}  {info?.Name ?? string.Empty}" + (string.IsNullOrEmpty(item.Label) ? string.Empty : $"\nLabel: {item.Label}");
            cell.MouseEnter += (_, _) => UiKit.Paint(cell, _traySel.Contains(index), hot: true);
            cell.MouseLeave += (_, _) => UiKit.Paint(cell, _traySel.Contains(index));
            cell.MouseLeftButtonDown += (_, e) =>
                                        {
                                            TrayClick(index);
                                            _trayScroll.Focus();
                                            if (e.ClickCount == 2) LabelSelected();
                                            e.Handled = true;
                                        };
            cell.ContextMenu = new ContextMenu();
            cell.ContextMenuOpening += (_, _) =>
                                       {
                                           if (!_traySel.Contains(index)) SelectTray(index);
                                           FillTrayMenu(cell.ContextMenu);
                                       };
            _trayCells.Add(cell);
            _trayPanel.Children.Add(cell);
        }

        PaintTray();
        _trayHint.Visibility = _tray.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _trayScroll.Visibility = _tray.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _trayTitle.Text = _tray.Count == 0 ? "Collection" : $"Collection ({_tray.Count:N0})";
    }

    private void PaintTray()
    {
        for (int i = 0; i < _trayCells.Count; i++) UiKit.Paint(_trayCells[i], _traySel.Contains(i));
    }

    private void SelectTray(int index)
    {
        _traySel.Clear();
        if (index >= 0 && index < _tray.Count)
        {
            _traySel.Add(index);
            _trayAnchor = index;
            if (index < _trayCells.Count) _trayCells[index].BringIntoView();
            if (_db is not null) Inspect(_tray[index].CodePoint.Value);
        }

        PaintTray();
    }

    private void TrayClick(int index)
    {
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Shift) && _trayAnchor >= 0)
        {
            _traySel.Clear();
            for (int i = Math.Min(_trayAnchor, index); i <= Math.Max(_trayAnchor, index); i++) _traySel.Add(i);
            PaintTray();
        }
        else if (mods.HasFlag(ModifierKeys.Control))
        {
            if (!_traySel.Remove(index)) _traySel.Add(index);
            _trayAnchor = index;
            PaintTray();
        }
        else SelectTray(index);
    }

    private void FillTrayMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        bool one = _traySel.Count == 1;
        menu.Items.Add(UiKit.Item("Copy character" + (_traySel.Count > 1 ? "s" : string.Empty), () => CopyTray(CollectionFormat.Text, "characters"), gesture: "Ctrl+C"));
        menu.Items.Add(UiKit.Item("Label…", LabelSelected, one, "Your own name for this character (kept with the collection, saved in files, used as the constant name suggestion)"));
        menu.Items.Add(UiKit.Rule());
        menu.Items.Add(UiKit.Item("Move left", () => MoveSelected(-1), gesture: "Alt+Left"));
        menu.Items.Add(UiKit.Item("Move right", () => MoveSelected(1), gesture: "Alt+Right"));
        menu.Items.Add(UiKit.Item("Sort by code point", SortTray));
        menu.Items.Add(UiKit.Item("Remove repeated characters", RemoveTrayDuplicates));
        menu.Items.Add(UiKit.Rule());
        menu.Items.Add(UiKit.Item("Remove", RemoveSelected, gesture: "Delete"));
    }

    private void OnTrayKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.Delete:
                RemoveSelected();
                break;
            case Key.A when mods == ModifierKeys.Control:
                for (int i = 0; i < _tray.Count; i++) _traySel.Add(i);
                PaintTray();
                break;
            case Key.C when mods == ModifierKeys.Control:
                CopyTray(CollectionFormat.Text, "characters");
                break;
            case Key.V when mods == ModifierKeys.Control:
                PasteIntoTray();
                break;
            case Key.Left when mods == ModifierKeys.Alt:
                MoveSelected(-1);
                break;
            case Key.Right when mods == ModifierKeys.Alt:
                MoveSelected(1);
                break;
            case Key.Left when mods == ModifierKeys.None && _tray.Count > 0:
                SelectTray(Math.Max(0, (_traySel.Count == 0 ? _tray.Count : _traySel.Min()) - 1));
                break;
            case Key.Right when mods == ModifierKeys.None && _tray.Count > 0:
                SelectTray(Math.Min(_tray.Count - 1, (_traySel.Count == 0 ? -1 : _traySel.Max()) + 1));
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // ---- copy ----------------------------------------------------------------------------------------------------

    private IEnumerable<Control> CopyMenuItems()
    {
        bool any = _tray.Count > 0;
        string which = _traySel.Count > 0 ? "selected" : "all";
        yield return UiKit.Item($"Characters ({which})", () => CopyTray(CollectionFormat.Text, "characters"), any);
        yield return UiKit.Item("Code points, one per line", () => CopyTray(CollectionFormat.CodePointList, "code points"), any);
        yield return UiKit.Item("JSON", () => CopyTray(CollectionFormat.Json, "JSON"), any);
        yield return UiKit.Item("Table (tab-separated)", () => CopyTray(CollectionFormat.Tsv, "table"), any);
        yield return UiKit.Item("Table (comma-separated)", () => CopyTray(CollectionFormat.Csv, "table"), any);
        yield return UiKit.Rule();
        yield return UiKit.Item("C# string literal", () => CopyText(StringLiterals.CSharp(TrayTarget().Select(c => c.CodePoint)), "C# string"), any);
        yield return UiKit.Item("JSON / JavaScript string", () => CopyText(StringLiterals.Json(TrayTarget().Select(c => c.CodePoint)), "JSON string"), any);
        yield return UiKit.Item("Code points on one line (U+25B6 U+221A …)", () => CopyText(string.Join(' ', TrayTarget().Select(c => c.CodePoint.Hex)), "code points"), any);
    }

    private void CopyTray(CollectionFormat format, string what)
    {
        var items = TrayTarget();
        if (items.Count == 0)
        {
            Say("The collection is empty.");
            return;
        }

        CopyText(CollectionFormats.Write(items, format, _db), what);
    }

    private void CopyText(string text, string what)
    {
        if (_tray.Count == 0)
        {
            Say("The collection is empty.");
            return;
        }

        Copy(text, what);
    }

    // ---- files ---------------------------------------------------------------------------------------------------

    private void SaveTrayAs()
    {
        var items = TrayTarget();
        if (items.Count == 0)
        {
            Say("The collection is empty.");
            return;
        }

        var dialog = new SaveFileDialog { Title = "Save the collection", Filter = CollectionFormats.SaveFilter, FileName = "characters", AddExtension = true, OverwritePrompt = true };
        if (dialog.ShowDialog() != true) return;

        // Text, JSON, TSV, CSV, code point list — the filter order; a typed extension that disagrees with the chosen filter wins when it is a known one
        var format = dialog.FilterIndex switch
                     {
                         2 => CollectionFormat.Json,
                         3 => CollectionFormat.Tsv,
                         4 => CollectionFormat.Csv,
                         5 => CollectionFormat.CodePointList,
                         _ => CollectionFormat.Text
                     };
        if (CollectionFormats.FromExtension(dialog.FileName) is { } typed && typed != CollectionFormat.Text && typed != format) format = typed;

        try
        {
            // Excel and Notepad need the byte-order mark to read the characters back as UTF-8; JSON must not have one
            bool bom = format != CollectionFormat.Json;
            File.WriteAllText(dialog.FileName, CollectionFormats.Write(items, format, _db), new UTF8Encoding(bom));
            Say($"Saved {items.Count:N0} character{(items.Count == 1 ? "" : "s")} to {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PromptDialog.Show("Save the collection", "The file could not be written: " + ex.Message, ["OK"]);
        }
    }

    private void OpenIntoTray()
    {
        var dialog = new OpenFileDialog { Title = "Open a character collection", Filter = CollectionFormats.OpenFilter };
        if (dialog.ShowDialog() != true) return;

        IReadOnlyList<CollectedCharacter> items;
        try
        {
            items = CollectionFormats.Read(File.ReadAllText(dialog.FileName), CollectionFormats.FromExtension(dialog.FileName) is { } f && f != CollectionFormat.Text ? f : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or Newtonsoft.Json.JsonException)
        {
            PromptDialog.Show("Open a character collection", "The file could not be read: " + ex.Message, ["OK"]);
            return;
        }

        if (items.Count == 0)
        {
            Say("There were no characters in that file.");
            return;
        }

        bool replace = false;
        if (_tray.Count > 0)
        {
            var (button, _) = PromptDialog.Show("Open a character collection", $"The file has {items.Count:N0} character{(items.Count == 1 ? "" : "s")}. Add them to the collection, or replace it?", ["Add", "Replace", "Cancel"]);
            if (button is < 0 or 2) return;
            replace = button == 1;
        }

        if (replace)
        {
            _tray.Clear();
            _traySel.Clear();
        }

        if (!HasTrayRoom(items.Count)) return;

        _tray.AddRange(items);
        RefreshTray();
        SaveTray();
        Say($"Loaded {items.Count:N0} character{(items.Count == 1 ? "" : "s")} from {Path.GetFileName(dialog.FileName)}.");
    }

    // ---- keeping it ----------------------------------------------------------------------------------------------

    /// <summary>Writes the collection to the Silo so it is still there next time; silently skipped when no Silo is open.</summary>
    private void SaveTray()
    {
        if (_trayLoading) return;
        try
        {
            var stored = Stored.At<CollectedCharacter[]>(TrayKey);
            if (!stored.IsAvailable) return;

            if (_tray.Count == 0) stored.Delete();
            else stored.Set(_tray.ToArray());
        }
        catch (Exception ex)
        {
            Say("The collection could not be kept in the Silo: " + ex.Message);
        }
    }

    private void LoadTray()
    {
        bool keep = false;
        _trayLoading = true;
        try
        {
            _traySel.Clear();
            if (Stored.At<CollectedCharacter[]>(TrayKey).Get() is { } saved)
            {
                _tray.Clear();
                _tray.AddRange(saved.Take(TrayLimit));
            }
            else keep = true; // this Silo has none yet: what was collected goes with the person into it
        }
        catch (Exception)
        {
            // an unreadable value: start empty rather than fail to open the tool
        }
        finally
        {
            _trayLoading = false;
        }

        RefreshTray();
        if (keep && _tray.Count > 0) SaveTray();
    }
}

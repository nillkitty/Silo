using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Silo.Contracts.Core;
using Silo.Contracts.Providers.Data;
using Silo.Expressions;
using Silo.Hotkeys;
using Silo.Storage;
using Silo.Ui.Windows;
using Silo.Unicode;

namespace Silo.Tools.UnicodeExplorer;

/// <summary>
///     What the person has saved in the Silo from the explorer, as a list to get it back from: characters (<c>your:green</c>), strings (<c>your:mychars</c>,
///     a list of characters) and hotkey bindings. Shown in the explorer's "Saved" tab and in the manager window.
/// </summary>
/// <remarks>
///     Lists itself again when something stored changes. The owner calls <see cref="Attach" /> while the list is wanted up to date and <see cref="Detach" />
///     when it is not (a tab that is not shown is unloaded, so the panel does not use Loaded/Unloaded for it).
/// </remarks>
internal sealed class SavedItemsPanel : UserControl
{
    private readonly Func<Window?>                      _owner;
    private readonly Action<string, IReadOnlyList<CodePoint>> _collect;
    private readonly Action<string>?                    _lookUp;
    private readonly Action<string>                     _say;

    private readonly StackPanel      _list    = new();
    private readonly TextBox         _filter  = new() { MinWidth = 160, Margin = new Thickness(0, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Show only what has this in its name, its characters (or their U+ codes), its key or its action" };
    private readonly TextBlock       _summary = UiKit.Text(string.Empty, muted: true);
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool                     _attached;

    /// <param name="owner">The window dialogs belong to, asked each time (the explorer can be floated or docked).</param>
    /// <param name="collect">Puts characters in the explorer's collection: what they are called, and the characters.</param>
    /// <param name="lookUp">Lists the characters in the explorer's grid; null where there is no grid (the manager window).</param>
    /// <param name="say">Shows a short message.</param>
    public SavedItemsPanel(Func<Window?> owner, Action<string, IReadOnlyList<CodePoint>> collect, Action<string>? lookUp, Action<string> say)
    {
        _owner   = owner;
        _collect = collect;
        _lookUp  = lookUp;
        _say     = say;

        var root = new DockPanel();

        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        bar.Children.Add(UiKit.Text("Filter", muted: true).WithMargin(0, 0, 6, 0));
        bar.Children.Add(_filter);
        bar.Children.Add(_summary);
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);

        var frame = new Border { BorderThickness = new Thickness(1) };
        frame.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
        frame.SetResourceReference(Border.BackgroundProperty, "Theme.BackgroundAlt");
        frame.Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _list };
        root.Children.Add(frame);
        Content = root;

        _filter.TextChanged += (_, _) => Rebuild();
        _refresh.Tick += (_, _) =>
                         {
                             _refresh.Stop();
                             Rebuild();
                         };
        Rebuild();
    }

    /// <summary>How many things are saved (constants and hotkeys), whatever the filter shows.</summary>
    public int Count { get; private set; }

    /// <summary>Raised when <see cref="Count" /> changed.</summary>
    public event EventHandler? CountChanged;

    /// <summary>Starts keeping the list up to date, and lists again now.</summary>
    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        Stored.Changed                            += OnStoredChanged;
        GlobalHotkeyService.Current.StatusChanged += OnStatusChanged;
        Rebuild();
    }

    /// <summary>Stops keeping the list up to date.</summary>
    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _refresh.Stop();
        Stored.Changed                            -= OnStoredChanged;
        GlobalHotkeyService.Current.StatusChanged -= OnStatusChanged;
    }

    /// <summary>Lists again after a moment (a burst of changes makes one listing).</summary>
    public void RefreshSoon() => Dispatcher.BeginInvoke(() =>
                                                        {
                                                            _refresh.Stop();
                                                            _refresh.Start();
                                                        });

    private void OnStoredChanged(object? sender, StoredChangedEventArgs e)
    {
        // the collection and other things the explorer keeps change all the time; only constants and hotkeys are listed here
        if (e.Key.StartsWith(YourConstants.Prefix + ":", StringComparison.OrdinalIgnoreCase) || e.Key.StartsWith(GlobalShortcutPref.KeyPrefix, StringComparison.OrdinalIgnoreCase))
            RefreshSoon();
    }

    private void OnStatusChanged(object? sender, EventArgs e) => RefreshSoon();

    // ---- listing -------------------------------------------------------------------------------------------------

    private void Rebuild()
    {
        _list.Children.Clear();

        if (!YourConstants.Default.IsAvailable)
        {
            SetCount(0, "No Silo is open");
            _list.Children.Add(Hint("Open a Silo to see what you have saved: constants and hotkeys are kept in it."));
            return;
        }

        IReadOnlyList<ConstantValue>       constants;
        IReadOnlyList<IGlobalShortcutPref> hotkeys;
        try
        {
            constants = YourConstants.Default.List();
            hotkeys   = GlobalHotkeyService.Current.Prefs.Shortcuts;
        }
        catch (Exception ex)
        {
            SetCount(0, string.Empty);
            _list.Children.Add(Hint("What you saved could not be read: " + ex.Message));
            return;
        }

        string filter = _filter.Text.Trim();
        var chars     = constants.Where(c => !c.IsList && Matches(c, filter)).ToList();
        var strings   = constants.Where(c => c.IsList && Matches(c, filter)).ToList();
        var keys      = hotkeys.Where(h => Matches(h, filter)).ToList();

        int total = constants.Count + hotkeys.Count;
        int shown = chars.Count + strings.Count + keys.Count;
        SetCount(total, total == 0 ? "Nothing saved yet" : filter.Length == 0 ? $"{total:N0} saved" : $"{shown:N0} of {total:N0}");

        if (total == 0)
        {
            _list.Children.Add(Hint("Nothing saved yet. Collect characters, then Silo ▾ → Save chars (a string), Save as Global Constant (a character) or Bind to global hotkey."));
            return;
        }

        if (shown == 0)
        {
            _list.Children.Add(Hint("Nothing saved matches the filter."));
            return;
        }

        AddSection("Characters", chars.Count);
        foreach (var c in chars) _list.Children.Add(ConstantRow(c));
        AddSection("Strings", strings.Count);
        foreach (var c in strings) _list.Children.Add(ConstantRow(c));
        AddSection("Hotkeys", keys.Count);
        foreach (var h in keys) _list.Children.Add(HotkeyRow(h));
    }

    private void SetCount(int count, string summary)
    {
        _summary.Text = summary;
        if (count == Count) return;
        Count = count;
        CountChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool Matches(ConstantValue c, string filter)
    {
        return filter.Length == 0
               || c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || c.Text.Contains(filter, StringComparison.Ordinal)
               || c.CodePoints.Any(p => p.Hex.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    private static bool Matches(IGlobalShortcutPref h, string filter)
    {
        return filter.Length == 0
               || h.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || h.Action.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || h.Shortcut.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void AddSection(string title, int count)
    {
        if (count == 0) return;
        _list.Children.Add(UiKit.Text($"{title} ({count:N0})", weight: FontWeights.SemiBold).WithMargin(6, 8, 6, 2));
    }

    private static TextBlock Hint(string text) => UiKit.Text(text, muted: true, wrap: true).WithMargin(10, 10, 10, 10);

    private static Border RowFrame(UIElement content)
    {
        var b = new Border { Child = content, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(6, 3, 6, 3), Background = Brushes.Transparent };
        b.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
        return b;
    }

    // ---- characters and strings ----------------------------------------------------------------------------------

    private Border ConstantRow(ConstantValue constant)
    {
        var row     = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(UiKit.Btn("Collect", () => Collect(constant), "Put its characters in the collection (double-click the row does the same)"));
        buttons.Children.Add(UiKit.Btn("Copy", () => CopyText(constant.Text, constant.FullName), "Copy the characters"));
        buttons.Children.Add(UiKit.MenuBtn("More", () => ConstantMenu(constant)));
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);

        var glyph = new TextBlock
                    {
                        Text = constant.Text, FontFamily = UiKit.GlyphFont, FontSize = 20, MinWidth = 56, MaxWidth = 220, VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 10, 0),
                        ToolTip = string.Join(' ', constant.CodePoints.Take(40).Select(p => p.Hex)) + (constant.CodePoints.Count > 40 ? " …" : string.Empty)
                    };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text");
        DockPanel.SetDock(glyph, Dock.Left);
        row.Children.Add(glyph);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(UiKit.Text(constant.FullName, weight: FontWeights.SemiBold));
        text.Children.Add(UiKit.Text(constant.IsList
                                         ? $"{constant.CodePoints.Count:N0} character{(constant.CodePoints.Count == 1 ? "" : "s")} · saved {constant.Modified.ToLocalTime():g}"
                                         : $"{constant.CodePoints[0].Hex} · saved {constant.Modified.ToLocalTime():g}", muted: true));
        row.Children.Add(text);

        var frame = RowFrame(row);
        frame.MouseLeftButtonDown += (_, e) =>
                                     {
                                         if (e.ClickCount != 2 || e.OriginalSource is Button) return;
                                         Collect(constant);
                                         e.Handled = true;
                                     };
        frame.ContextMenu = new ContextMenu();
        frame.ContextMenuOpening += (_, _) =>
                                    {
                                        frame.ContextMenu.Items.Clear();
                                        frame.ContextMenu.Items.Add(UiKit.Item("Collect", () => Collect(constant)));
                                        frame.ContextMenu.Items.Add(UiKit.Item("Copy", () => CopyText(constant.Text, constant.FullName)));
                                        frame.ContextMenu.Items.Add(UiKit.Rule());
                                        foreach (var item in ConstantMenu(constant)) frame.ContextMenu.Items.Add(item);
                                    };
        return frame;
    }

    private IEnumerable<Control> ConstantMenu(ConstantValue constant)
    {
        if (_lookUp is not null) yield return UiKit.Item("Look up in the grid", () => _lookUp(constant.Text), tip: "List these characters in the results above");
        yield return UiKit.Item("Copy ${" + constant.FullName + "}", () => CopyText("${" + constant.FullName + "}", "the reference"), tip: "Copy the reference to paste into a hotkey's action or an expression");
        yield return UiKit.Item("Copy code points", () => CopyText(string.Join(' ', constant.CodePoints.Select(p => p.Hex)), "the code points"));
        yield return UiKit.Rule();
        yield return UiKit.Item("Delete…", () => DeleteConstant(constant));
    }

    private void Collect(ConstantValue constant) => _collect(constant.FullName, constant.CodePoints);

    private void CopyText(string text, string what) => _say(UiKit.TryCopy(text) ? $"Copied {what}." : "The clipboard is busy; nothing was copied.");

    private void DeleteConstant(ConstantValue constant)
    {
        // a constant a hotkey still uses would leave that hotkey failing; say so
        var users = GlobalHotkeyService.Current.Prefs.Shortcuts.Where(p => p.Action.Contains(constant.FullName, StringComparison.OrdinalIgnoreCase)).Select(p => p.Name).ToList();
        string message = $"Delete {constant.FullName}?" + (users.Count > 0 ? $"\n\nThese hotkeys use it and will stop working: {string.Join(", ", users)}." : string.Empty);
        if (PromptDialog.Show("Delete constant", message, ["Delete", "Cancel"]).Button != 0) return;

        try
        {
            YourConstants.Default.Delete(constant.Name);
            _say($"Deleted {constant.FullName}.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or StoredDataException or Microsoft.Data.Sqlite.SqliteException)
        {
            _say(ex.Message);
        }
    }

    // ---- hotkeys -------------------------------------------------------------------------------------------------

    private Border HotkeyRow(IGlobalShortcutPref pref)
    {
        var service = GlobalHotkeyService.Current;
        var row     = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(UiKit.Btn("Run", () => RunNow(pref), "Do the action now (a typing action types into the window that has the focus)"));
        buttons.Children.Add(UiKit.Btn("Edit", () => Edit(pref), "Change the key or the action (double-click the row does the same)"));
        buttons.Children.Add(UiKit.MenuBtn("More", () => HotkeyMenu(pref)));
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);

        string? preview = Preview(pref.Action);
        if (preview is { Length: > 0 })
        {
            var glyph = new TextBlock
                        {
                            Text = preview, FontFamily = UiKit.GlyphFont, FontSize = 20, MinWidth = 56, MaxWidth = 220, VerticalAlignment = VerticalAlignment.Center,
                            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 10, 0), ToolTip = "What the action comes to now"
                        };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text");
            DockPanel.SetDock(glyph, Dock.Left);
            row.Children.Add(glyph);
        }

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(UiKit.Text(pref.Name, weight: FontWeights.SemiBold));
        var key = UiKit.Text("  " + (pref.Shortcut.IsSet ? pref.Shortcut.ToString() : "no key"));
        key.FontFamily = UiKit.Mono;
        head.Children.Add(key);
        head.Children.Add(UiKit.Text($"   {(pref.Scope == ShortcutScope.Global ? "everywhere" : "in Silo")} · {(pref.Storage == StoredScope.Shared ? "shared" : "mine")}{(pref.Enabled ? string.Empty : " · off")}", muted: true));
        text.Children.Add(head);
        var action = UiKit.Text(pref.Action, muted: true);
        action.FontFamily = UiKit.Mono;
        text.Children.Add(action);
        if (service.Failures.TryGetValue(pref.Name, out string? failure))
        {
            var warn = UiKit.Text("⚠ " + failure, wrap: true);
            warn.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Accent");
            text.Children.Add(warn);
        }

        row.Children.Add(text);

        var frame = RowFrame(row);
        frame.MouseLeftButtonDown += (_, e) =>
                                     {
                                         if (e.ClickCount != 2 || e.OriginalSource is Button) return;
                                         Edit(pref);
                                         e.Handled = true;
                                     };
        frame.ContextMenu = new ContextMenu();
        frame.ContextMenuOpening += (_, _) =>
                                    {
                                        frame.ContextMenu.Items.Clear();
                                        foreach (var item in HotkeyMenu(pref)) frame.ContextMenu.Items.Add(item);
                                    };
        return frame;
    }

    private IEnumerable<Control> HotkeyMenu(IGlobalShortcutPref pref)
    {
        yield return UiKit.Item(pref.Enabled ? "Turn off" : "Turn on", () => Toggle(pref));
        if (_lookUp is not null && Preview(pref.Action) is { Length: > 0 } typed)
            yield return UiKit.Item("Look up what it types", () => _lookUp(typed));
        yield return UiKit.Item("Copy action", () => CopyText(pref.Action, "the action"));
        yield return UiKit.Rule();
        yield return UiKit.Item("Delete…", () => Delete(pref));
    }

    /// <summary>What an action's text comes to now (the characters a <c>type ${your:blue}</c> types); null when it cannot be worked out.</summary>
    private static string? Preview(string action)
    {
        try
        {
            return ShortcutActionText.TryParse(action, out var parsed, out _) ? parsed!.Template.Evaluate(YourConstants.Default.AsScope()) : null;
        }
        catch (ExpressionException)
        {
            return null;
        }
    }

    private void Edit(IGlobalShortcutPref pref)
    {
        var prefs = GlobalHotkeyService.Current.Prefs;
        if (HotkeyDialog.Ask(_owner(), prefs, pref.Value, pref.Storage, pref.Name) is not { } chosen) return;

        try
        {
            prefs.Save(chosen.Binding, chosen.Storage, pref.Name);
            _say($"Saved {chosen.Binding.Name}.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _say(ex.Message);
        }
    }

    private void RunNow(IGlobalShortcutPref pref)
    {
        var result = GlobalHotkeyService.Current.RunNow(pref);
        _say(result.Success ? $"Ran {pref.Name}." : $"{pref.Name} failed: {result.Error}");
    }

    private void Toggle(IGlobalShortcutPref pref)
    {
        try
        {
            GlobalHotkeyService.Current.Prefs.Save(pref.Value with { Enabled = !pref.Enabled }, pref.Storage, pref.Name);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _say(ex.Message);
        }
    }

    private void Delete(IGlobalShortcutPref pref)
    {
        if (PromptDialog.Show("Delete hotkey", $"Delete the hotkey {pref.Name} ({pref.Shortcut})?", ["Delete", "Cancel"]).Button != 0) return;

        GlobalHotkeyService.Current.Prefs.Remove(pref.Name);
        _say($"Deleted {pref.Name}.");
    }
}

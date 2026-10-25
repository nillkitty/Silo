using System.Collections;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Silo.Converters;
using Silo.Ui.Windows;

namespace Silo.Toolbar;

/// <summary>
///     The Customize Toolbar dialog, in the manner of Visual Studio's: the commands that can be added on the left (by
///     category), the toolbar's buttons on the right, and what can be done to a button -- add, remove, move, rename,
///     change its
///     icon, show its text. OK saves and applies; Cancel leaves the toolbar as it was.
/// </summary>
public sealed class CustomizeToolbarWindow : Window
{
    private const    string   AllCategories = "All";
    private readonly Button   _add          = Make("_Add >");
    private readonly Button   _addSep       = Make("Add _Separator");
    private readonly ListBox  _available    = new();
    private readonly ComboBox _category     = new();
    private readonly ListBox  _current      = new();
    private readonly Button   _down         = Make("Move _Down");
    private readonly Button   _icon         = Make("Change _Icon...");
    private readonly Button   _remove       = Make("_Remove");
    private readonly Button   _rename       = Make("Re_name...");
    private readonly Button   _resetOne     = Make("Reset Bu_tton");
    private readonly CheckBox _showText     = new() { Content = "Show _text on this button", Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button   _up           = Make("Move _Up");
    private readonly CheckBox _visible      = new() { Content = "Show the _toolbar", VerticalAlignment = VerticalAlignment.Center };

    private readonly ToolbarLayout _work;

    private bool _refreshing;

    private int SelectedIndex => SelectedSlot is { } s ? _work.Slots.IndexOf(s) : -1;

    private ToolbarSlot? SelectedSlot => (_current.SelectedItem as ListBoxItem)?.Tag as ToolbarSlot;

    /// <summary>Shows the dialog; if OK is pressed the layout is saved and the toolbar rebuilds itself.</summary>
    public static void ShowFor(Window? owner)
    {
        var dialog = new CustomizeToolbarWindow(ToolbarSettings.Layout.Clone());
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        else dialog.WindowStartupLocation              = WindowStartupLocation.CenterScreen;

        if (dialog.ShowDialog() == true) ToolbarSettings.Apply(dialog._work);
    }

    // ---- layout ---------------------------------------------------------------------------------

    private static Button Make(string text)
    {
        return new Button { Content = text, Margin = new Thickness(0, 0, 0, 6), MinWidth = 110 };
    }

    private UIElement BuildContent()
    {
        var root = new Grid { Margin                       = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(new TextBlock
                          {
                              Text = "Pick a command on the left and Add it; arrange the toolbar's buttons on the right. " +
                                     "Node commands and tool commands are groups that fill in by themselves.",
                              TextWrapping = TextWrapping.Wrap,
                              Margin       = new Thickness(0, 0, 0, 10)
                          });

        var body = new Grid { Margin = new Thickness(0) };
        Grid.SetRow(body, 1);
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.Children.Add(body);

        // available commands
        var left       = new DockPanel();
        var leftHeader = new StackPanel { Margin     = new Thickness(0,                                                         0, 0, 6) };
        leftHeader.Children.Add(new TextBlock { Text = "Commands", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        leftHeader.Children.Add(_category);
        DockPanel.SetDock(leftHeader, Dock.Top);
        left.Children.Add(leftHeader);
        left.Children.Add(_available);
        Grid.SetColumn(left, 0);
        body.Children.Add(left);

        // Add / Add Separator
        var middle = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
        middle.Children.Add(_add);
        middle.Children.Add(_addSep);
        Grid.SetColumn(middle, 1);
        body.Children.Add(middle);

        // the toolbar's buttons
        var right       = new DockPanel();
        var rightHeader = new TextBlock { Text = "Toolbar buttons", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(rightHeader, Dock.Top);
        right.Children.Add(rightHeader);
        right.Children.Add(_current);
        Grid.SetColumn(right, 2);
        body.Children.Add(right);

        var side = new StackPanel { Margin = new Thickness(10, 22, 0, 0) };
        side.Children.Add(_up);
        side.Children.Add(_down);
        side.Children.Add(_remove);
        side.Children.Add(new Separator { Margin = new Thickness(0, 4, 0, 10) });
        side.Children.Add(_rename);
        side.Children.Add(_icon);
        side.Children.Add(_resetOne);
        side.Children.Add(_showText);
        Grid.SetColumn(side, 3);
        body.Children.Add(side);

        // bottom row
        var bottom = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        Grid.SetRow(bottom, 2);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok      = new Button { Content         = "OK", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel  = new Button { Content         = "Cancel", IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) => { DialogResult = true; };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);

        var resetAll = new Button
                       {
                           Content = "Reset _Toolbar", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(16, 0, 0, 0)
                       };
        resetAll.Click += (_, _) =>
                          {
                              var d = ToolbarLayout.Default();
                              _work.Slots        = d.Slots;
                              _work.Visible      = true;
                              _visible.IsChecked = true;
                              FillCurrent(null);
                          };
        _visible.IsChecked =  _work.Visible;
        _visible.Checked   += (_, _) => _work.Visible = true;
        _visible.Unchecked += (_, _) => _work.Visible = false;

        var leftBottom = new StackPanel { Orientation = Orientation.Horizontal };
        leftBottom.Children.Add(_visible);
        leftBottom.Children.Add(resetAll);
        bottom.Children.Add(leftBottom);
        root.Children.Add(bottom);

        return root;
    }

    private void Wire()
    {
        _category.SelectionChanged  += (_, _) => FillAvailable();
        _available.MouseDoubleClick += (_, _) => AddSelected();
        _add.Click                  += (_, _) => AddSelected();
        _addSep.Click               += (_, _) => Insert(new ToolbarSlot(ToolbarLayout.SeparatorId));

        _current.SelectionChanged += (_, _) => UpdateButtons();
        _up.Click                 += (_, _) => Move(-1);
        _down.Click               += (_, _) => Move(+1);
        _remove.Click             += (_, _) => RemoveSelected();
        _rename.Click             += (_, _) => RenameSelected();
        _icon.Click               += (_, _) => ChangeIconOfSelected();
        _resetOne.Click           += (_, _) => ResetSelected();

        _showText.Checked   += (_, _) => SetShowText(true);
        _showText.Unchecked += (_, _) => SetShowText(false);
    }

    // ---- the two lists -----------------------------------------------------------------------------

    private void FillCategories()
    {
        _category.Items.Add(AllCategories);
        foreach (var c in ToolbarCatalog.All().Select(d => d.Category).Distinct())
            _category.Items.Add(c);
        _category.SelectedIndex = 0;
    }

    private void FillAvailable()
    {
        string? category = _category.SelectedItem as string;
        _available.Items.Clear();

        foreach (var def in ToolbarCatalog.All())
        {
            if (category is not null && category != AllCategories && def.Category != category) continue;
            _available.Items.Add(new ListBoxItem
                                 {
                                     Content = Row(def.IconKey,
                                                   def.PlainHeader +
                                                   (category == AllCategories ? "   (" + def.Category + ")" : string.Empty)),
                                     Tag = def, ToolTip = def.ToolTip
                                 });
        }
    }

    private void FillCurrent(ToolbarSlot? select)
    {
        _refreshing = true;
        _current.Items.Clear();

        foreach (var slot in _work.Slots)
        {
            object content;
            if (slot.IsSeparator)
            {
                content = new TextBlock { Text = "─────  Separator  ─────", Foreground = Brushes.Gray };
            }
            else if (ToolbarCatalog.Find(slot.Id) is { } def)
            {
                string label = ToolbarText.Plain(slot.Header ?? def.Header);
                content = Row(slot.IconKey ?? def.IconKey, label + (slot.ShowText ? "   [text]" : string.Empty));
            }
            else
            {
                content = new TextBlock { Text = slot.Id + "  (not available now)", Foreground = Brushes.Gray };
            }

            var item = new ListBoxItem { Content = content, Tag = slot };
            _current.Items.Add(item);
            if (ReferenceEquals(slot, select)) item.IsSelected = true;
        }

        _refreshing = false;
        UpdateButtons();
    }

    private static UIElement Row(string? iconKey, string label)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var icon  = ToolbarIcons.Find(iconKey);
        panel.Children.Add(icon is null ? new Border { Width = 16, Height = 16 } : new AppIcon { Icon = icon, Size = 16 });
        panel.Children.Add(new TextBlock
                           {
                               Text = label, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
                           });
        return panel;
    }

    private void UpdateButtons()
    {
        if (_refreshing) return;

        var  slot      = SelectedSlot;
        bool has       = slot is not null;
        bool isCommand = has && !slot!.IsSeparator;
        int  index     = SelectedIndex;

        _up.IsEnabled       = has && index > 0;
        _down.IsEnabled     = has && index < _work.Slots.Count - 1;
        _remove.IsEnabled   = has;
        _rename.IsEnabled   = isCommand;
        _icon.IsEnabled     = isCommand;
        _resetOne.IsEnabled = isCommand;
        _showText.IsEnabled = isCommand;

        _refreshing         = true;
        _showText.IsChecked = isCommand && slot!.ShowText;
        _refreshing         = false;
    }

    // ---- edits ------------------------------------------------------------------------------------

    private void Insert(ToolbarSlot slot)
    {
        int at = SelectedIndex;
        _work.Slots.Insert(at >= 0 ? at + 1 : _work.Slots.Count, slot);
        FillCurrent(slot);
        _current.ScrollIntoView(_current.SelectedItem);
    }

    private void AddSelected()
    {
        if ((_available.SelectedItem as ListBoxItem)?.Tag is not ToolbarItemDef def) return;

        // a group (node commands, tool commands, the Tools drop-down, Customize) is only worth having once
        if (def.Kind != ToolbarItemKind.Command && _work.Slots.FirstOrDefault(s => s.Id == def.Id) is { } existing)
        {
            FillCurrent(existing);
            return;
        }

        Insert(new ToolbarSlot(def.Id));
    }

    private void Move(int by)
    {
        int i = SelectedIndex, j = i + by;
        if (i < 0 || j < 0 || j >= _work.Slots.Count) return;

        var slot = _work.Slots[i];
        _work.Slots.RemoveAt(i);
        _work.Slots.Insert(j, slot);
        FillCurrent(slot);
    }

    private void RemoveSelected()
    {
        int i = SelectedIndex;
        if (i < 0) return;

        _work.Slots.RemoveAt(i);
        FillCurrent(_work.Slots.Count > 0 ? _work.Slots[Math.Min(i, _work.Slots.Count - 1)] : null);
    }

    private void RenameSelected()
    {
        if (SelectedSlot is not { IsSeparator: false } slot || ToolbarCatalog.Find(slot.Id) is not { } def) return;

        string current = ToolbarText.Plain(slot.Header ?? def.Header);
        if (PromptDialog.Ask("Rename Button", "The label of this button (its tooltip, and its text when it shows text):", "OK", current) is
            { } text)
        {
            slot.Header = text;
            FillCurrent(slot);
        }
    }

    private void ChangeIconOfSelected()
    {
        if (SelectedSlot is not { IsSeparator: false } slot || ToolbarCatalog.Find(slot.Id) is not { } def) return;

        var picker = new IconPickerWindow(slot.IconKey ?? def.IconKey) { Owner = this };
        if (picker.ShowDialog() != true) return;

        slot.IconKey = picker.UseDefault ? null : picker.SelectedKey;
        FillCurrent(slot);
    }

    private void ResetSelected()
    {
        if (SelectedSlot is not { IsSeparator: false } slot) return;

        slot.Header   = null;
        slot.IconKey  = null;
        slot.ToolTip  = null;
        slot.ShowText = false;
        FillCurrent(slot);
    }

    private void SetShowText(bool on)
    {
        if (_refreshing || SelectedSlot is not { IsSeparator: false } slot) return;

        slot.ShowText = on;
        FillCurrent(slot);
    }

    private CustomizeToolbarWindow(ToolbarLayout work)
    {
        _work = work;

        Title                 = "Customize Toolbar";
        Width                 = 860;
        Height                = 560;
        MinWidth              = 700;
        MinHeight             = 420;
        ShowInTaskbar         = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Theme.Background");
        SetResourceReference(ForegroundProperty, "Theme.Text");

        Content = BuildContent();
        Wire();
        FillCategories();
        FillAvailable();
        FillCurrent(null);
    }
}

/// <summary>Picks an icon (a geometry resource key) for a toolbar button.</summary>
internal sealed class IconPickerWindow : Window
{
    private readonly TextBox   _filter = new() { Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock _name   = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly WrapPanel _panel  = new();

    /// <summary>The key chosen.</summary>
    public string? SelectedKey { get; private set; }

    /// <summary>The person asked for the button's own icon back.</summary>
    public bool UseDefault { get; private set; }

    /// <summary>The icon keys the app draws its own icons with (Resources/NavIcons.xaml).</summary>
    internal static IEnumerable<string> IconKeys()
    {
        var keys = new List<string>();
        if (Application.Current?.Resources is { } resources) Collect(resources, keys);
        return keys.Distinct().OrderBy(k => k, StringComparer.OrdinalIgnoreCase);
    }

    private static void Collect(ResourceDictionary dictionary, List<string> keys)
    {
        if (dictionary.Source?.OriginalString.EndsWith("NavIcons.xaml", StringComparison.OrdinalIgnoreCase) == true)
            foreach (DictionaryEntry entry in dictionary)
                if (entry.Key is string key && entry.Value is Geometry)
                    keys.Add(key);

        foreach (var merged in dictionary.MergedDictionaries) Collect(merged, keys);
    }

    private void Fill()
    {
        _panel.Children.Clear();
        string filter = _filter.Text.Trim();

        foreach (var key in IconKeys())
        {
            if (filter.Length > 0 && !key.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

            var geometry = ToolbarIcons.Find(key);
            if (geometry is null) continue;

            var button = new ToggleButton
                         {
                             Content   = new AppIcon { Icon = geometry, Size = 22 },
                             Width     = 38,
                             Height    = 38,
                             Margin    = new Thickness(2),
                             ToolTip   = key,
                             IsChecked = key == SelectedKey
                         };
            string k = key;
            button.Click += (_, _) =>
                            {
                                SelectedKey = k;
                                _name.Text  = k;
                                foreach (var other in _panel.Children.OfType<ToggleButton>())
                                    other.IsChecked = ReferenceEquals(other, button);
                            };
            _panel.Children.Add(button);
        }

        _name.Text = SelectedKey ?? string.Empty;
    }

    public IconPickerWindow(string? current)
    {
        Title                 = "Change Icon";
        Width                 = 520;
        Height                = 460;
        ShowInTaskbar         = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Theme.Background");
        SetResourceReference(ForegroundProperty, "Theme.Text");

        SelectedKey = current;

        var root = new DockPanel { Margin = new Thickness(14) };

        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "Choose an icon:", Margin = new Thickness(0, 0, 0, 6) });
        top.Children.Add(_filter);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var def     = new Button { Content = "Use _Default", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 4, 10, 4) };
        var ok      = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel  = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        def.Click += (_, _) =>
                     {
                         UseDefault   = true;
                         DialogResult = true;
                     };
        ok.Click += (_, _) => { DialogResult = SelectedKey is not null; };
        buttons.Children.Add(def);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_name);
        root.Children.Add(bottom);

        root.Children.Add(new ScrollViewer { Content = _panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;

        _filter.TextChanged += (_, _) => Fill();
        Fill();
    }
}
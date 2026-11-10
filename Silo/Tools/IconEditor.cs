using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

using Silo.Converters;
using Silo.Design;
using Silo.Toolbar;

using Telefrag.DI;

namespace Silo.Tools;

/// <summary>
///     Author-mode tool for choosing the icons the app uses: the left list has every <see cref="IconRole" /> (a registry key, a kind of
///     node...) with the icon it shows now; pick one, then click an icon on the right to remap it. Remaps are kept in the
///     <see cref="IconMap" /> (<c>%APPDATA%\Silo\iconmap.json</c>); nodes already on screen keep their icon until they are refreshed.
/// </summary>
[Transient(typeof(ITool))]
public class IconEditor : ToolBase
{
    private const double TileSize = 38;

    private readonly WrapPanel  _palette   = new();
    private readonly ListView   _roles     = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBox    _roleFilter = new();
    private readonly TextBox    _iconFilter = new();
    private readonly TextBlock  _status    = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button     _reset     = new() { Content = "Use _Default", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
    private readonly Button     _resetAll  = new() { Content = "Reset _All", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 0, 0, 0) };

    public IconEditor() : base("iconeditor")
    {
        DisplayName = "_Icon Editor";
    }

    protected override UIElement OnBuildContent()
    {
        var root = new Grid { Margin = new Thickness(8) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star), MinWidth = 220 });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star), MinWidth = 200 });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // left: what can be remapped
        var left = new DockPanel();
        var leftHead = new StackPanel();
        leftHead.Children.Add(new TextBlock { Text = "1. Choose what to change:", Margin = new Thickness(0, 0, 0, 4) });
        leftHead.Children.Add(_roleFilter);
        DockPanel.SetDock(leftHead, Dock.Top);
        left.Children.Add(leftHead);
        left.Children.Add(_roles);
        Grid.SetColumn(left, 0);
        root.Children.Add(left);

        var splitter = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        Grid.SetColumn(splitter, 1);
        root.Children.Add(splitter);

        // right: what to change it to
        var right = new DockPanel();
        var rightHead = new StackPanel();
        rightHead.Children.Add(new TextBlock { Text = "2. Click the icon to use:", Margin = new Thickness(0, 0, 0, 4) });
        rightHead.Children.Add(_iconFilter);
        DockPanel.SetDock(rightHead, Dock.Top);
        right.Children.Add(rightHead);
        right.Children.Add(new ScrollViewer { Content = _palette, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 0) });
        Grid.SetColumn(right, 2);
        root.Children.Add(right);

        var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_reset);
        buttons.Children.Add(_resetAll);
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_status);
        Grid.SetRow(bottom, 1);
        Grid.SetColumnSpan(bottom, 3);
        root.Children.Add(bottom);

        _roles.SelectionMode = SelectionMode.Single;
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(FillRoles());
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RoleRow.Category)));
        _roles.ItemsSource = view;
        _roles.GroupStyle.Add(new GroupStyle { HeaderTemplate = GroupHeader() });
        _roles.ItemTemplate = RowTemplate();
        _roles.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);

        _roleFilter.TextChanged += (_, _) =>
                                   {
                                       string text = _roleFilter.Text.Trim();
                                       view.Filter = text.Length == 0 ? null : o => o is RoleRow r && (r.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || r.Category.Contains(text, StringComparison.OrdinalIgnoreCase) || r.Key.Contains(text, StringComparison.OrdinalIgnoreCase));
                                   };
        _roles.SelectionChanged += (_, _) => OnRoleChanged();
        _iconFilter.TextChanged += (_, _) => FillPalette();
        _reset.Click            += (_, _) => Assign(null);
        _resetAll.Click         += (_, _) =>
                                   {
                                       if (MessageBox.Show("Put every remapped icon back to its default?", "Icon Editor", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

                                       IconMap.ResetAll();
                                       RefreshRows();
                                   };

        FillPalette();
        ShowStatus();
        return root;
    }

    private List<RoleRow> _rows = [];

    private System.Collections.IEnumerable FillRoles()
    {
        _rows = IconMap.Roles.Select(r => new RoleRow(r)).ToList();
        return _rows;
    }

    private void RefreshRows()
    {
        foreach (var row in _rows) row.Refresh();
        OnRoleChanged();
        ShowStatus();
    }

    private RoleRow? Selected => _roles.SelectedItem as RoleRow;

    private void OnRoleChanged()
    {
        _reset.IsEnabled = Selected is { IsOverridden: true };
        FillPalette();
    }

    private void Assign(string? key)
    {
        if (Selected is not { } row) return;

        IconMap.Set(row.Role.Id, key);
        row.Refresh();
        _reset.IsEnabled = row.IsOverridden;
        FillPalette();
        ShowStatus();
    }

    private void ShowStatus()
    {
        int n = IconMap.OverrideCount;
        _status.Text = n == 0 ? "Nothing remapped. Nodes read their icons when they are made: refresh a node to see a change." : $"{n} remapped. Nodes read their icons when they are made: refresh a node to see a change.";
    }

    /// <summary>The icons to choose from, the selected role's current one marked.</summary>
    private void FillPalette()
    {
        _palette.Children.Clear();
        string filter  = _iconFilter.Text.Trim();
        string current = Selected?.Key ?? string.Empty;

        foreach (string key in IconPickerWindow.IconKeys())
        {
            if (filter.Length > 0 && !key.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            if (ToolbarIcons.Find(key) is not { } geometry) continue;

            var tile = new ToggleButton
                       {
                           Content   = new AppIcon { Icon = geometry, Size = 22 },
                           Width     = TileSize,
                           Height    = TileSize,
                           Margin    = new Thickness(2),
                           ToolTip   = key,
                           IsChecked = string.Equals(key, current, StringComparison.OrdinalIgnoreCase),
                           IsEnabled = Selected is not null
                       };
            string captured = key;
            tile.Click += (_, _) => Assign(captured);
            _palette.Children.Add(tile);
        }
    }

    private static DataTemplate GroupHeader()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(CollectionViewGroup.Name)));
        text.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        text.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 8, 0, 2));
        return new DataTemplate { VisualTree = text };
    }

    private static DataTemplate RowTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

        var icon = new FrameworkElementFactory(typeof(AppIcon));
        icon.SetBinding(AppIcon.IconProperty, new Binding(nameof(RoleRow.Geometry)));
        icon.SetValue(AppIcon.SizeProperty, 20.0);
        icon.SetValue(FrameworkElement.WidthProperty, 20.0);
        icon.SetValue(FrameworkElement.HeightProperty, 20.0);
        icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
        panel.AppendChild(icon);

        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(RoleRow.Name)));
        name.SetBinding(TextBlock.FontWeightProperty, new Binding(nameof(RoleRow.Weight)));
        name.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        panel.AppendChild(name);

        var key = new FrameworkElementFactory(typeof(TextBlock));
        key.SetBinding(TextBlock.TextProperty, new Binding(nameof(RoleRow.KeyText)));
        key.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 0, 0));
        key.SetValue(UIElement.OpacityProperty, 0.6);
        key.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        panel.AppendChild(key);

        return new DataTemplate { VisualTree = panel };
    }

    /// <summary>A role in the list; follows the map as it changes.</summary>
    private sealed class RoleRow(IconRole role) : INotifyPropertyChanged
    {
        public IconRole Role { get; } = role;

        public string Category => Role.Category;

        public string Name => Role.Name;

        public string Key => IconMap.KeyOf(Role.Id, Role.DefaultKey);

        public bool IsOverridden => IconMap.IsOverridden(Role.Id);

        public Geometry? Geometry => ToolbarIcons.Find(Key) ?? ToolbarIcons.Find(Role.DefaultKey);

        public FontWeight Weight => IsOverridden ? FontWeights.Bold : FontWeights.Normal;

        public string KeyText => IsOverridden ? $"{Key}  (was {Role.DefaultKey})" : Key;

        public void Refresh()
        {
            foreach (string p in new[] { nameof(Key), nameof(IsOverridden), nameof(Geometry), nameof(Weight), nameof(KeyText) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}

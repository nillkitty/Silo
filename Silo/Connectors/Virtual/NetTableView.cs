using System.Windows.Controls;
using System.Windows.Data;

namespace Silo.Connectors.Virtual;

/// <summary>
///     The view of a network node: its table as a read-only grid (several tables as tabs), read from the system in the background
///     and again whenever Refresh is pressed.
/// </summary>
internal sealed class NetTableView : UserControl
{
    private readonly NetTable  _table;
    private readonly DataGrid  _grid;
    private readonly TextBlock _status = new() { Margin = new Thickness(8, 4, 8, 4), Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
    private int                _generation;

    public NetTableView(NetTable table)
    {
        _table = table;
        _grid  = new DataGrid
                 {
                     IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column,
                     SelectionUnit = DataGridSelectionUnit.FullRow, ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader
                 };

        // the rows are string arrays and the columns bind by index, so a name such as "Age (s)" never has to be a binding path
        for (var i = 0; i < table.ColumnNames.Count; i++)
            _grid.Columns.Add(new DataGridTextColumn { Header = table.ColumnNames[i].Replace("_", "__"), Binding = new Binding($"[{i}]") });

        var refresh = new Button { Content = "Refresh", Margin = new Thickness(8, 6, 0, 6), Padding = new Thickness(10, 2, 10, 2) };
        refresh.Click += (_, _) => Load();

        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        bar.Children.Add(refresh);
        bar.Children.Add(_status);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(_grid);
        Content = root;

        Load();
    }

    /// <summary>The view for a provider's tables: the grid itself for one, tabs for several.</summary>
    public static UIElement For(IEnumerable<NetTable> tables)
    {
        var list = tables.ToList();
        if (list.Count == 1) return new NetTableView(list[0]);

        var tabs = new TabControl();
        foreach (var t in list) tabs.Items.Add(new TabItem { Header = t.CanonicalName, Content = new NetTableView(t) });
        return tabs;
    }

    private async void Load()
    {
        var generation = ++_generation;
        _status.Text = "Reading...";
        try
        {
            var rows = await Task.Run(() => _table.GetRows().Select(r => r.ToArray()).ToList());
            if (generation != _generation) return;

            _grid.ItemsSource = rows;
            _status.Text      = $"{rows.Count:N0} row(s)";
        }
        catch (Exception ex)
        {
            if (generation == _generation) _status.Text = "Could not read: " + ex.Message;
        }
    }
}

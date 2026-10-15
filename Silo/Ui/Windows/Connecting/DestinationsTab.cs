using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Serilog;
using Silo.Connectors;
using Silo.Settings;

namespace Silo.Ui.Windows.Connecting;

/// <summary>One row of the Destinations tab.</summary>
public sealed class DestinationItem
{
    public required string  Group      { get; init; }
    public required int     GroupOrder { get; init; }
    public required int     Sequence   { get; init; }
    public required string  Name       { get; init; }
    public required Uri     Uri        { get; init; }
    public          string? Detail     { get; init; }

    /// <summary>False when no connector handles the destination's scheme: the row is shown but cannot be chosen.</summary>
    public bool IsAvailable { get; init; }

    public string ToolTip { get; init; } = string.Empty;

    /// <summary>The URI as the person knows it: a path for a file: URI.</summary>
    public string Location => Uri.IsFile ? Uri.LocalPath : Uri.ToString();

    /// <summary>The history entry, for a destination in the "Recent" group.</summary>
    public RecentDestination? Recent { get; init; }
}

/// <summary>
///     The Destinations tab: places to connect to, in three groups -- ones connected to before, ones found on this
///     computer by the destination discoveries (<see cref="IDiscovery{TComponent}" />), and ones connectors list themselves
///     (<see cref="IDestinationSource" />). A destination no connector handles is listed greyed out.
/// </summary>
public sealed class DestinationsTab : ConnectTab
{
    public const string RecentGroup = "Recent destinations";

    /// <summary>The group of what a discovery found: one group per discovery ("Discovered: Drives").</summary>
    public static string DiscoveredGroup(string discovery) => $"Discovered: {discovery}";

    /// <summary>The group of what a connector lists itself: one group per connector.</summary>
    public static string RegisteredGroup(string connector) => $"Registered by the {connector} connector";

    private readonly ConnectionHistory                         _history;
    private readonly IReadOnlyList<IUriConnector>              _uriConnectors;
    private readonly IReadOnlyList<IDiscovery<DestinationEntry>> _discoveries;
    private readonly ObservableCollection<DestinationItem>     _items = [];
    private readonly HashSet<string>                           _seen  = new(StringComparer.OrdinalIgnoreCase);
    private readonly ListView                                  _list;
    private readonly TextBlock                                 _status;
    private readonly Button                                    _more;
    private readonly CancellationTokenSource                   _cancel = new();
    private          bool                                      _started;
    private          bool                                      _searchedMore;

    public DestinationsTab(IReadOnlyList<IConnector> connectors, IReadOnlyList<IDiscovery<DestinationEntry>> discoveries, ConnectionHistory history)
    {
        _history       = history;
        _discoveries   = discoveries;
        _uriConnectors = ConnectorCatalog.UriConnectors(connectors);

        _list = new ListView { ItemsSource = _items, SelectionMode = SelectionMode.Single, Margin = new Thickness(8, 8, 8, 0) };
        _list.View = BuildColumns();
        _list.ItemContainerStyle = BuildItemStyle();
        _list.GroupStyle.Add(new GroupStyle { HeaderTemplate = BuildGroupHeader() });
        _list.SelectionChanged += (_, _) => OnSelected();
        _list.KeyDown          += OnKeyDown;
        _list.ContextMenu      =  BuildMenu();

        var view = CollectionViewSource.GetDefaultView(_items);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DestinationItem.Group)));
        view.SortDescriptions.Add(new SortDescription(nameof(DestinationItem.GroupOrder), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(DestinationItem.Sequence),   ListSortDirection.Ascending));

        _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.65 };
        _more   = new Button { Content = "Search further...", Padding = new Thickness(10, 2, 10, 2), Visibility = Visibility.Collapsed };
        _more.Click += async (_, _) => await SearchFurtherAsync();

        var footer = new DockPanel { Margin = new Thickness(8, 6, 8, 6) };
        DockPanel.SetDock(_more, Dock.Right);
        footer.Children.Add(_more);
        footer.Children.Add(_status);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(footer, 1);
        grid.Children.Add(_list);
        grid.Children.Add(footer);
        View = grid;

        AddRecent();
        AddRegistered(connectors);
        Set(null, "Select a destination to connect to.");
    }

    public override string Header => "Destinations";

    public override UIElement View { get; }

    /// <summary>Starts the quick discoveries (once). Results arrive while the person looks at the list.</summary>
    public override void Start()
    {
        if (_started) return;

        _started = true;
        _more.Visibility = _discoveries.Any(d => d.Type == DiscoveryType.Extensive) ? Visibility.Visible : Visibility.Collapsed;
        _ = RunAsync(_discoveries.Where(d => d.Type == DiscoveryType.Quick).ToList());
    }

    public override void Connected(ConnectionTarget target)
    {
        // the dialog records every connection by its URI; nothing more to remember per tab
    }

    public override void Stop()
    {
        _cancel.Cancel();
    }

    // ---- filling the list --------------------------------------------------------------------------

    private void AddRecent()
    {
        int n = 0;
        foreach (var recent in _history.Destinations)
            if (Uri.TryCreate(recent.Uri, UriKind.Absolute, out var uri))
                Add(RecentGroup, 0, n++, recent.Name, uri, $"Last used {recent.LastUsedUtc.ToLocalTime():g}", recent);
    }

    private void AddRegistered(IEnumerable<IConnector> connectors)
    {
        int n = 0;
        foreach (var (provider, entry) in ConnectorCatalog.RegisteredDestinations(connectors))
            Add(RegisteredGroup(provider), 2, n++, entry.Name, entry.Uri, entry.Detail);
    }

    private void Add(string group, int groupOrder, int sequence, string name, Uri uri, string? detail, RecentDestination? recent = null)
    {
        if (!_seen.Add($"{groupOrder}|{uri.AbsoluteUri}")) return;

        var    connector = UriRules.FindConnector(uri, _uriConnectors);
        string toolTip   = connector is IConnector c
                               ? $"Connects with the {c.DisplayName} connector"
                               : connector is null
                                   ? $"No connector handles \"{uri.Scheme}:\" destinations yet"
                                   : "Connects with a connector";

        _items.Add(new DestinationItem
                   {
                       Group       = group,
                       GroupOrder  = groupOrder,
                       Sequence    = sequence,
                       Name        = name,
                       Uri         = uri,
                       Detail      = detail,
                       IsAvailable = connector is not null,
                       ToolTip     = toolTip,
                       Recent      = recent
                   });
    }

    private async Task RunAsync(IReadOnlyList<IDiscovery<DestinationEntry>> discoveries)
    {
        if (discoveries.Count == 0) return;

        _status.Text = "Looking for destinations...";
        var dispatcher = _list.Dispatcher;
        var token      = _cancel.Token;

        var tasks = new List<Task>();
        for (int i = 0; i < discoveries.Count; i++)
        {
            var discovery = discoveries[i];
            int baseSeq   = (_discoveryRuns++ * 1000) + (i * 100);
            tasks.Add(Task.Run(async () =>
                               {
                                   int n = 0;
                                   try
                                   {
                                       await foreach (var found in discovery.DiscoverAsync(token).WithCancellation(token))
                                       {
                                           int    sequence = baseSeq + n++;
                                           var    entry    = found.Component;
                                           await dispatcher.InvokeAsync(() => Add(DiscoveredGroup(discovery.DisplayName), 1, sequence, entry.Name, found.Uri, entry.Detail));
                                       }
                                   }
                                   catch (OperationCanceledException)
                                   {
                                       /* the dialog was closed */
                                   }
                                   catch (Exception ex)
                                   {
                                       Log.Warning(ex, "Destination discovery {discovery} failed", discovery.DisplayName);
                                   }
                               }, token));
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!token.IsCancellationRequested)
            _status.Text = _items.Any(i => i.GroupOrder == 1) ? string.Empty : "Nothing was found on this computer.";
    }

    private int _discoveryRuns;

    private async Task SearchFurtherAsync()
    {
        if (_searchedMore) return;

        _searchedMore = true;
        _more.IsEnabled = false;
        await RunAsync(_discoveries.Where(d => d.Type == DiscoveryType.Extensive).ToList());
    }

    // ---- selection ---------------------------------------------------------------------------------

    private void OnSelected()
    {
        if (_list.SelectedItem is not DestinationItem item)
        {
            Set(null, "Select a destination to connect to.");
            return;
        }

        var check = UriRules.CheckUri(item.Uri, _uriConnectors);
        if (!check.IsValid || check.Connector is not { } connector)
        {
            Set(null, check.Message, true);
            return;
        }

        string name = item.Name;
        var    uri  = item.Uri;
        Set(new ConnectionTarget(name, uri, cancel => ConnectionCreation.CreateAsync(connector, new ConnectionRequest(uri) { Connector = connector, DisplayName = name }, cancel)),
            check.Message);
    }

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { IsSelected: true }) RequestConnect();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _list.SelectedItem is DestinationItem { Recent: not null } item)
        {
            RemoveRecent(item);
            e.Handled = true;
        }
    }

    private void RemoveRecent(DestinationItem item)
    {
        if (item.Recent is null) return;

        _history.RemoveDestination(item.Recent);
        _items.Remove(item);
        _seen.Remove($"0|{item.Uri.AbsoluteUri}");
    }

    // ---- building the list's look ------------------------------------------------------------------

    private static GridView BuildColumns()
    {
        var grid = new GridView();
        grid.Columns.Add(new GridViewColumn { Header = "Name",     Width = 220, DisplayMemberBinding = new Binding(nameof(DestinationItem.Name)) });
        grid.Columns.Add(new GridViewColumn { Header = "Details",  Width = 230, DisplayMemberBinding = new Binding(nameof(DestinationItem.Detail)) });
        grid.Columns.Add(new GridViewColumn { Header = "Location", Width = 260, DisplayMemberBinding = new Binding(nameof(DestinationItem.Location)) });
        return grid;
    }

    private Style BuildItemStyle()
    {
        var style = new Style(typeof(ListViewItem)) { BasedOn = Application.Current?.TryFindResource(typeof(ListViewItem)) as Style };
        style.Setters.Add(new Setter(UIElement.IsEnabledProperty, new Binding(nameof(DestinationItem.IsAvailable))));
        style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(nameof(DestinationItem.ToolTip))));
        style.Setters.Add(new Setter(ToolTipService.ShowOnDisabledProperty, true));
        style.Setters.Add(new EventSetter(Control.MouseDoubleClickEvent, new MouseButtonEventHandler(OnItemDoubleClick)));
        return style;
    }

    private static DataTemplate BuildGroupHeader()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding("Name"));
        text.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        text.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 8, 0, 3));
        return new DataTemplate { VisualTree = text };
    }

    private ContextMenu BuildMenu()
    {
        var remove = new MenuItem { Header = "Remove from recent destinations" };
        remove.Click += (_, _) =>
                        {
                            if (_list.SelectedItem is DestinationItem { Recent: not null } item) RemoveRecent(item);
                        };

        var menu = new ContextMenu();
        menu.Items.Add(remove);
        menu.Opened += (_, _) => remove.IsEnabled = _list.SelectedItem is DestinationItem { Recent: not null };
        return menu;
    }
}

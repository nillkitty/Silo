using System.Windows.Controls;
using System.Windows.Data;
using Serilog;
using Silo.Connectors;
using Silo.Contracts.ConnectorServices;

namespace Silo.Ui.Windows.Connecting;

/// <summary>One connector in the list on the left of the Connectors tab.</summary>
public sealed class ConnectorItem
{
    public required IConnector Connector   { get; init; }
    public required string     Name        { get; init; }
    public          string?    Description { get; init; }
}

/// <summary>
///     The Connectors tab: every registered connector, and for the selected one its own setup page (<see cref="IConnectorUi" />),
///     like a data source's page in the Windows Data Sources control panel. A connector without a page of its own gets a
///     location box when it takes URIs, or just Connect when it can connect by itself (<see cref="IAutoConnectable" />).
///     Connect is available once the page says it has everything the connector needs.
/// </summary>
public sealed class ConnectorsTab : ConnectTab
{
    private readonly ConnectorSetupContext                  _context;
    private readonly Dictionary<IConnector, IConnectorSetup> _setups = new();
    private readonly ListBox                                _list;
    private readonly TextBlock                              _title;
    private readonly TextBlock                              _description;
    private readonly ContentControl                         _page;
    private          IConnector?                            _selected;

    public ConnectorsTab(IReadOnlyList<IConnector> connectors, ConnectorSetupContext context)
    {
        _context = context;

        _list = new ListBox
                {
                    ItemsSource  = connectors.Select(c => new ConnectorItem
                                                          {
                                                              Connector   = c,
                                                              Name        = c.DisplayName,
                                                              Description = (c as IConnectorUi)?.Description
                                                          }).ToList(),
                    ItemTemplate = BuildItemTemplate(),
                    Margin       = new Thickness(8)
                };
        _list.SelectionChanged += (_, _) => OnSelected();

        _title       = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 12, 12, 0) };
        _description = new TextBlock { Opacity = 0.65, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 2, 12, 0) };
        _page        = new ContentControl { Margin = new Thickness(0, 6, 0, 0), VerticalAlignment = VerticalAlignment.Top };

        var right = new StackPanel();
        right.Children.Add(_title);
        right.Children.Add(_description);
        right.Children.Add(_page);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230), MinWidth = 140 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(_list);
        grid.Children.Add(splitter);
        grid.Children.Add(right);
        View = grid;

        _title.Text = "Connectors";
        _description.Text = connectors.Count == 0
                                ? "No connectors are registered."
                                : "Choose a connector on the left, then say what to connect to.";
        Set(null, connectors.Count == 0 ? "No connectors are registered." : "Select a connector, then fill in what it needs.");
    }

    public override string Header => "Connectors";

    public override UIElement View { get; }

    // ---- selection ---------------------------------------------------------------------------------

    private void OnSelected()
    {
        if (_list.SelectedItem is not ConnectorItem item)
        {
            _selected = null;
            Refresh();
            return;
        }

        _selected     = item.Connector;
        _title.Text   = item.Name;
        _description.Text = item.Description ?? string.Empty;

        var setup = SetupFor(item.Connector);
        _page.Content = setup.View;
        Refresh();
    }

    private IConnectorSetup SetupFor(IConnector connector)
    {
        if (_setups.TryGetValue(connector, out var existing)) return existing;

        var setup = Build(connector);
        setup.StateChanged += (_, _) =>
                              {
                                  if (ReferenceEquals(_selected, connector)) Refresh();
                              };
        _setups[connector] = setup;
        return setup;
    }

    private IConnectorSetup Build(IConnector connector)
    {
        try
        {
            if (connector is IConnectorUi ui) return ui.CreateSetup(_context);

            if (connector is IUriConnector uriConnector)
                return new UriFieldSetup(connector, text => UriRules.Check(text, [uriConnector], UriRules.AnyScope), "Location",
                                         "Enter a URI this connector understands.");

            if (connector is IAutoConnectable auto) return new AutoSetup(connector, auto);

            return new MessageSetup("This connector has no setup page yet, so it cannot make a connection from here.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The setup page of the {connector} connector could not be built", connector.GetType().Name);
            return new MessageSetup($"The setup page of this connector could not be built: {ex.Message}");
        }
    }

    private void Refresh()
    {
        if (_selected is not { } connector)
        {
            Set(null, "Select a connector, then fill in what it needs.");
            return;
        }

        var setup = _setups[connector];
        if (!setup.IsValid)
        {
            Set(null, setup.Problem ?? "Fill in what this connector needs.", setup.Problem is not null);
            return;
        }

        try
        {
            if (setup is AutoSetup auto)
            {
                Set(new ConnectionTarget(auto.SuggestedName, null, auto.Create), null);
                return;
            }

            var    request = setup.BuildRequest();
            string name    = string.IsNullOrWhiteSpace(setup.SuggestedName) ? connector.DisplayName : setup.SuggestedName;
            request.Connector   ??= connector;
            request.DisplayName ??= name;
            Set(new ConnectionTarget(name, request.RequestedUri, cancel => ConnectionCreation.CreateAsync(connector, request, cancel)), null);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The {connector} setup page could not build its request", connector.GetType().Name);
            Set(null, ex.Message, true);
        }
    }

    private static DataTemplate BuildItemTemplate()
    {
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(ConnectorItem.Name)));
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);

        var description = new FrameworkElementFactory(typeof(TextBlock));
        description.SetBinding(TextBlock.TextProperty, new Binding(nameof(ConnectorItem.Description)));
        description.SetValue(TextBlock.OpacityProperty, 0.65);
        description.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);

        var stack = new FrameworkElementFactory(typeof(StackPanel));
        stack.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 3, 2, 3));
        stack.AppendChild(name);
        stack.AppendChild(description);
        return new DataTemplate { VisualTree = stack };
    }

    // ---- setups for connectors without a page of their own -----------------------------------------

    /// <summary>For a connector that connects by itself: nothing to fill in.</summary>
    private sealed class AutoSetup(IConnector connector, IAutoConnectable auto) : IConnectorSetup
    {
        public UIElement View { get; } = new TextBlock
                                         {
                                             Text         = "This connector connects by itself: there is nothing to fill in.",
                                             Margin       = new Thickness(12),
                                             TextWrapping = TextWrapping.Wrap
                                         };

        public bool   IsValid       => true;
        public string? Problem      => null;
        public string SuggestedName => connector.DisplayName;

        public event EventHandler? StateChanged
        {
            add { }
            remove { }
        }

        public ConnectionRequest BuildRequest()
        {
            return new ConnectionRequest { Connector = connector, DisplayName = SuggestedName };
        }

        public Task<IConnection> Create(CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            return Task.FromResult(auto.CreateAutoConnection() ?? throw new InvalidOperationException("The connector could not make its connection."));
        }
    }

    /// <summary>For a connector that cannot be set up from here: says so.</summary>
    private sealed class MessageSetup(string message) : IConnectorSetup
    {
        public UIElement View { get; } = new TextBlock { Text = message, Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };

        public bool    IsValid       => false;
        public string? Problem       => null;
        public string  SuggestedName => string.Empty;

        public event EventHandler? StateChanged
        {
            add { }
            remove { }
        }

        public ConnectionRequest BuildRequest()
        {
            throw new InvalidOperationException(message);
        }
    }
}

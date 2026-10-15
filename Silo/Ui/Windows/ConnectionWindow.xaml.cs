using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Serilog;
using Silo.Connectors;
using Silo.Model;
using Silo.Settings;
using Silo.Ui.Windows.Connecting;

namespace Silo.Ui.Windows;

/// <summary>
///     The New Connection dialog (modal). Three tabs are three ways to choose a connection -- a destination
///     (<see cref="DestinationsTab" />), a connector with its own setup page (<see cref="ConnectorsTab" />), or a URI
///     (<see cref="UriTab" />) -- and the Connect button works for whichever tab is showing: it is enabled only when that tab
///     has something usable chosen. Connect makes the connection (the dialog stays open, saying why, if the connector
///     cannot); the caller adds it to the Silo.
/// </summary>
public partial class ConnectionWindow : Window
{
    private readonly List<ConnectTab> _tabs = [];
    private readonly TabControl       _tabControl = new() { Margin = new Thickness(8, 8, 8, 0) };
    private readonly Button           _connect;
    private readonly TextBlock        _hint;
    private          string?          _error;

    public ConnectionWindow() : this(null)
    {
    }

    public ConnectionWindow(OpenSilo? silo)
    {
        InitializeComponent();

        var connectors = ConnectorCatalog.Connectors();
        var history    = ConnectionHistory.Instance;
        _tabs.Add(new DestinationsTab(connectors, ConnectorCatalog.Discoveries(), history));
        _tabs.Add(new ConnectorsTab(connectors, new ConnectorSetupContext(silo)));
        _tabs.Add(new UriTab(ConnectorCatalog.UriConnectors(connectors), history));

        foreach (var tab in _tabs)
        {
            _tabControl.Items.Add(new TabItem { Header = tab.Header, Content = tab.View });
            tab.Changed          += OnTabChanged;
            tab.ConnectRequested += (_, _) => Connect();
        }

        _tabControl.SelectionChanged += OnTabSelected;

        _connect = new Button { Content = "Connect", IsDefault = true, IsEnabled = false, MinWidth = 90, Padding = new Thickness(14, 4, 14, 4) };
        _connect.Click += (_, _) => Connect();
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0) };

        _hint = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0), Opacity = 0.7 };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_connect);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Right);

        var footer = new DockPanel { Margin = new Thickness(12, 10, 12, 12) };
        footer.Children.Add(buttons);
        footer.Children.Add(_hint);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(footer, 1);
        layout.Children.Add(_tabControl);
        layout.Children.Add(footer);
        Root.Children.Add(layout);

        _tabControl.SelectedIndex = 0;
        UpdateState();

        WindowPlacements.Track(this, silo, WindowPlacements.Dialog, "silo:dialog/new-connection");

        Loaded += (_, _) =>
                  {
                      foreach (var tab in _tabs) tab.Start();
                      Current?.FocusInput();
                  };
        Closed += (_, _) =>
                  {
                      foreach (var tab in _tabs) tab.Stop();
                  };
    }

    /// <summary>What was connected, once the dialog was closed with Connect.</summary>
    public ConnectionResult? Result { get; private set; }

    /// <summary>The connection that was made, once the dialog was closed with Connect.</summary>
    public IConnection? Connection => Result?.Connection;

    /// <summary>Shows the dialog. Returns what was connected, or null when it was cancelled.</summary>
    public static ConnectionResult? Modal(OpenSilo? silo = null)
    {
        var dialog = new ConnectionWindow(silo);
        if (Application.Current?.MainWindow is { IsVisible: true } owner && !ReferenceEquals(owner, dialog))
            dialog.Owner = owner;

        return dialog.ShowDialog() is true ? dialog.Result : null;
    }

    private ConnectTab? Current => _tabControl.SelectedIndex >= 0 && _tabControl.SelectedIndex < _tabs.Count ? _tabs[_tabControl.SelectedIndex] : null;

    private void OnTabSelected(object sender, SelectionChangedEventArgs e)
    {
        // the lists inside the tabs raise SelectionChanged too, and it bubbles up to here
        if (!ReferenceEquals(e.OriginalSource, _tabControl)) return;

        _error = null;
        UpdateState();
        Dispatcher.BeginInvoke(new Action(() => Current?.FocusInput()), DispatcherPriority.Input);
    }

    private void OnTabChanged(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, Current)) return;

        _error = null;
        UpdateState();
    }

    private void UpdateState()
    {
        var  tab     = Current;
        bool problem = _error is not null || tab?.HintIsProblem == true;

        _connect.IsEnabled = tab?.Target is not null;
        _hint.Text         = _error ?? tab?.Hint ?? string.Empty;
        _hint.Opacity      = problem ? 1 : 0.7;
        if (problem)
            _hint.Foreground = Brushes.IndianRed;
        else
            _hint.ClearValue(TextBlock.ForegroundProperty);
    }

    private bool _connecting;

    /// <summary>
    ///     Connect: makes the connection without freezing the dialog. If that takes more than a second (a network share
    ///     that does not answer) a progress window with Cancel shows; cancelling leaves the dialog as it was.
    /// </summary>
    private async void Connect()
    {
        var tab    = Current;
        var target = tab?.Target;
        if (tab is null || target is null || _connecting) return;

        _connecting = true;
        try
        {
            string where = target.Uri is { } place ? (place.IsFile ? place.LocalPath : place.ToString()) : target.Name;
            var connection = await BusyDialog.RunAsync(this, "Connecting", $"Connecting to {where}...", target.Create);
            Result = new ConnectionResult(connection, target.Name, target.Uri);

            if (target.Uri is { } uri) ConnectionHistory.Instance.RecordDestination(uri, target.Name);
            tab.Connected(target);

            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            _error = $"Cancelled: not connected to {target.Name}.";
            UpdateState();
        }
        catch (NotImplementedException ex)
        {
            Log.Debug(ex, "The connector for {name} cannot create connections yet", target.Name);
            _error = "That connector cannot create connections yet.";
            UpdateState();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not connect to {name}", target.Name);
            _error = $"Could not connect to {target.Name}: {ex.Message}";
            UpdateState();
        }
        finally
        {
            _connecting = false;
        }
    }
}

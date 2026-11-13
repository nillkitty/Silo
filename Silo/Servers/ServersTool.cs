using System.Linq;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Silo.Certificates;
using Silo.DbModel.Global;
using Silo.Model;
using Silo.Providers.Network;
using Silo.Tabular;
using Silo.Tools;
using Telefrag.DI;

namespace Silo.Servers;

/// <summary>
///     The Servers pane: the servers of the active Silo, one row each (where it listens, what it runs, which table it uses),
///     and below them the selected server's settings and the log of every running server. Servers are kept in the Silo, so
///     they come back when it is opened again; they run until stopped, or until their Silo closes.
/// </summary>
[Singleton(typeof(ITool))]
public sealed class ServersTool : ToolBase
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");

    private readonly ListBox    _list   = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, MinHeight = 90, BorderThickness = new Thickness(0, 1, 0, 1) };
    private readonly StackPanel _editor = new() { Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBox    _log    = new() { IsReadOnly = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap, Height = 140, FontFamily = Mono };
    private readonly TextBlock  _empty  = new() { Margin = new Thickness(0, 8, 0, 8), TextWrapping = TextWrapping.Wrap };

    private List<ServerConfig> _servers = [];
    private OpenSilo?          _silo;
    private string?            _editing;
    private bool               _rebuilding;

    public ServersTool() : base("servers")
    {
        DisplayName = "_Servers";
        GroupName   = "Network";
        Icon        = IconRef.FromKey("Server");
    }

    protected override bool UsesPane => true;

    protected override UIElement OnBuildContent()
    {
        var root = new DockPanel { Margin = new Thickness(8) };

        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        bar.Children.Add(Button("_Add", AddMenu));
        bar.Children.Add(Button("_Remove", () => Remove(Selected)));
        bar.Children.Add(Button("_Start", () => _ = StartAsync(Selected)));
        bar.Children.Add(Button("S_top", () => _ = StopAsync(Selected)));
        bar.Children.Add(Button("Start a_ll", () => _ = StartAllAsync()));
        bar.Children.Add(Button("Stop all", () => _ = ServerHost.Instance.StopAllAsync(_silo)));
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);

        DockPanel.SetDock(_log, Dock.Bottom);
        root.Children.Add(_log);

        var middle = new StackPanel();
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        middle.Children.Add(_empty);
        middle.Children.Add(_list);
        middle.Children.Add(_editor);
        root.Children.Add(new ScrollViewer { Content = middle, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        _list.SelectionChanged += (_, _) =>
                                  {
                                      if (!_rebuilding && Selected?.Id != _editing) BuildEditor();
                                  };
        _list.MouseDoubleClick += (_, _) =>
                                  {
                                      if (Selected is { } s) _ = ServerHost.Instance.IsRunning(s.Id) ? StopAsync(s) : StartAsync(s);
                                  };

        foreach (var line in ServerHost.Instance.RecentLog) _log.AppendText(line + Environment.NewLine);
        ServerHost.Instance.Logged  += line => _log.Dispatcher.BeginInvoke(() => Say(line));
        ServerHost.Instance.Changed += (_, _) => _list.Dispatcher.BeginInvoke(new Action(RebuildList));
        TableEditors.Changed        += (_, _) => _list.Dispatcher.BeginInvoke(new Action(RebuildList));
        if (App.Instance is { } app) app.ActiveSiloChanged += (_, _) => _list.Dispatcher.BeginInvoke(new Action(Load));

        Load();
        return root;
    }

    private ServerConfig? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as ServerConfig;

    private static Button Button(string text, Action click)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
        b.Click += (_, _) => click();
        return b;
    }

    private void Say(string line)
    {
        _log.AppendText(line + Environment.NewLine);
        if (_log.LineCount > 1000) _log.Text = string.Join(Environment.NewLine, _log.Text.Split(Environment.NewLine).TakeLast(500));
        _log.ScrollToEnd();
    }

    // ---- the Silo's servers --------------------------------------------------------------------------------------

    private void Load()
    {
        _silo    = App.Instance?.ActiveSilo;
        _servers = ServerStore.Load(_silo?.Data as IStoredStore);
        _editing = null;
        RebuildList();
        BuildEditor();
    }

    /// <summary>Writes the servers into the Silo; false (with the reason shown) when it cannot keep them.</summary>
    private bool Persist()
    {
        if (_silo?.Data is not IStoredStore store)
        {
            App.Status("Open a Silo first: servers are kept in it");
            return false;
        }

        if (!_silo.IsWritable)
        {
            App.Status("This Silo is read-only");
            return false;
        }

        return TableEditors.Guard(() => ServerStore.Save(store, _servers), "save the servers");
    }

    private void AddMenu()
    {
        var menu = new ContextMenu();
        foreach (var service in ServerServices.All)
        {
            var item = new MenuItem { Header = service.DisplayName, ToolTip = service.Description };
            item.Click += (_, _) => Add(service);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private void Add(IServerService service)
    {
        var config = ServerConfig.For(service);

        // TLS needs a keypair: use the Silo's first one, or leave out the endpoints that cannot work without one
        if (config.Endpoints.Any(e => e.Tls != ServerTls.None))
        {
            if (FirstKeyPair() is { } cert) config.CertificateId = cert.Id;
            else config.Endpoints.RemoveAll(e => e.Tls == ServerTls.Implicit);
        }

        _servers.Add(config);
        if (!Persist())
        {
            _servers.Remove(config);
            return;
        }

        RebuildList(config.Id);
    }

    private void Remove(ServerConfig? config)
    {
        if (config is null) return;
        if (MessageBox.Show($"Remove the server \"{config.Name}\"?", AppIdentity.AppName, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        _ = ServerHost.Instance.StopAsync(config.Id);
        _servers.Remove(config);
        Persist();
        _editing = null;
        RebuildList();
        BuildEditor();
    }

    private async Task StartAsync(ServerConfig? config)
    {
        if (config is null || _silo is null) return;

        await ServerHost.Instance.StartAsync(_silo, config);
    }

    private static async Task StopAsync(ServerConfig? config)
    {
        if (config is not null) await ServerHost.Instance.StopAsync(config.Id);
    }

    private async Task StartAllAsync()
    {
        foreach (var config in _servers.Where(s => s.Enabled && !ServerHost.Instance.IsRunning(s.Id)).ToList()) await StartAsync(config);
    }

    private VaultEntry? FirstKeyPair()
    {
        try
        {
            return CertificateActions.OpenVault(_silo)?.List().FirstOrDefault(e => e.HasKey);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    // ---- the list ------------------------------------------------------------------------------------------------

    private void RebuildList() => RebuildList(Selected?.Id);

    private void RebuildList(string? select)
    {
        _rebuilding = true;
        _list.Items.Clear();
        foreach (var config in _servers)
        {
            var item = new ListBoxItem { Content = Row(config), Tag = config, Padding = new Thickness(4, 6, 4, 6) };
            _list.Items.Add(item);
            if (config.Id == select) item.IsSelected = true;
        }

        _rebuilding = false;
        if (Selected?.Id != _editing) BuildEditor();

        _list.Visibility = _servers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _empty.Text = _silo is null
                          ? "Open a Silo: its servers are kept in it."
                          : _servers.Count == 0
                              ? "No servers yet. Add one: a DNS stub answering from a table, an SMTP stub keeping mail in one, or a TCP relay that adds or removes TLS."
                              : "Double-click a server to start or stop it.";
    }

    private UIElement Row(ServerConfig config)
    {
        var  service = ServerServices.Find(config.Service);
        bool running = ServerHost.Instance.IsRunning(config.Id);
        var  error   = ServerHost.Instance.ErrorOf(config.Id);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var endpoints = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var bound     = ServerHost.Instance.RunnerOf(config.Id)?.Bound;
        foreach (var e in config.Endpoints)
        {
            var text = new TextBlock { Text = EndpointText.Format([e]), FontFamily = Mono, FontWeight = running ? FontWeights.Bold : FontWeights.Normal };
            if (!config.Enabled) text.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
            if (bound?.FirstOrDefault(b => b.Endpoint == e) is { Bound: { } at }) text.ToolTip = $"Listening on {at}";
            endpoints.Children.Add(text);
        }

        if (config.Endpoints.Count == 0) endpoints.Children.Add(new TextBlock { Text = "(no endpoints)", FontFamily = Mono });
        grid.Children.Add(endpoints);

        var what = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontFamily = Mono };
        what.Inlines.Add(new Run(config.Name) { ToolTip = service?.Description });
        var dots = new Hyperlink(new Run(" [...]")) { ToolTip = "Settings" };
        dots.Click += (_, _) =>
                      {
                          if (_list.Items.Cast<ListBoxItem>().FirstOrDefault(i => i.Tag == config) is { } item) item.IsSelected = true;
                          _editor.BringIntoView();
                      };
        what.Inlines.Add(dots);
        Grid.SetColumn(what, 1);
        grid.Children.Add(what);

        var tables = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontFamily = Mono };
        foreach (var setting in service?.Settings.Where(s => s.Kind == ServerSettingKind.Table) ?? [])
        {
            string name = config.Settings.GetValueOrDefault(setting.Key) is { Length: > 0 } v ? v : setting.Default ?? "";
            if (name.Length == 0) continue;

            if (tables.Inlines.Count > 0) tables.Inlines.Add(new Run(", "));
            tables.Inlines.Add(new Run(config.Service == "smtp-stub" ? "into " : "from "));
            var link = new Hyperlink(new Run(name)) { ToolTip = TableOf(name) is null ? "There is no such table yet: click to make it" : "Open the table" };
            link.Click += (_, _) => OpenTable(name, setting);
            tables.Inlines.Add(link);
        }

        Grid.SetColumn(tables, 2);
        grid.Children.Add(tables);

        var status = new TextBlock
                     {
                         Text              = running ? "running" : error is not null ? "failed" : config.Enabled ? "stopped" : "disabled",
                         ToolTip           = error,
                         VerticalAlignment = VerticalAlignment.Center,
                         Margin            = new Thickness(12, 0, 4, 0)
                     };
        if (error is not null && !running) status.Foreground = Brushes.Firebrick;
        else if (!running) status.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        Grid.SetColumn(status, 3);
        grid.Children.Add(status);
        return grid;
    }

    private AuthoredTable? TableOf(string name)
    {
        return (_silo?.Data as ITableStore)?.ListTables().FirstOrDefault(t => string.Equals(t.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Opens the table a server uses, making it (with the columns the service wants) when it is not there yet.</summary>
    private void OpenTable(string name, ServerSetting setting)
    {
        if (TableOf(name) is { } table)
        {
            TableEditors.OpenExisting(_silo, table.Id);
            return;
        }

        if (TableEditors.StoreOf(_silo) is not { } store) return;

        AuthoredTable? made = null;
        if (!TableEditors.Guard(() =>
                                {
                                    var sheet = new SheetData();
                                    foreach (var column in setting.Columns ?? ["Name", "Value"]) sheet.Columns.Add(new SheetColumn { Name = column, Type = CellType.Text });
                                    for (int i = 0; i < 5; i++) sheet.Rows.Add(sheet.Columns.Select(_ => string.Empty).ToArray());
                                    var created = new AuthoredTable { Name = name };
                                    created.WriteSheet(sheet);
                                    made = store.SaveTable(created);
                                }, "make the table"))
            return;

        TableEditors.RaiseChanged();
        TableEditors.OpenExisting(_silo, made!.Id);
    }

    // ---- the editor ----------------------------------------------------------------------------------------------

    private void BuildEditor()
    {
        _editor.Children.Clear();
        var config = Selected;
        _editing = config?.Id;
        if (config is null) return;

        var service = ServerServices.Find(config.Service);
        _editor.Children.Add(new TextBlock
                             {
                                 Text         = service is null ? $"The service \"{config.Service}\" is not available in this copy of Silo." : service.Description,
                                 TextWrapping = TextWrapping.Wrap,
                                 Margin       = new Thickness(0, 0, 0, 6)
                             });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 520 });
        _editor.Children.Add(grid);

        var name = new TextBox { Text = config.Name };
        Commit(name, () =>
                     {
                         config.Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : service?.DisplayName ?? config.Service;
                         return null;
                     });
        Field(grid, "Name", name);

        var enabled = new CheckBox { IsChecked = config.Enabled, Content = "Started by Start all", VerticalAlignment = VerticalAlignment.Center };
        enabled.Click += (_, _) =>
                         {
                             config.Enabled = enabled.IsChecked == true;
                             Saved();
                         };
        Field(grid, "Enabled", enabled);

        var endpoints = new TextBox { Text = EndpointText.Format(config.Endpoints), FontFamily = Mono, ToolTip = "Like UDP:53; TCP:53 or TCP:25,587+starttls; TCP:465+tls or 127.0.0.1/TCP:2525" };
        Commit(endpoints, () =>
                          {
                              var parsed = EndpointText.Parse(endpoints.Text);
                              if (service is not null && parsed.FirstOrDefault(e => !service.Protocols.Contains(e.Protocol)) is { } bad)
                                  return $"{service.DisplayName} does not speak {bad.Protocol.ToString().ToUpperInvariant()}.";

                              config.Endpoints = parsed;
                              return null;
                          });
        Field(grid, "Endpoints", endpoints, "+tls wraps the connection in TLS from the start; +starttls lets the protocol upgrade it. TLS needs the certificate below.");

        Field(grid, "TLS certificate", CertificatePicker(config));

        foreach (var setting in service?.Settings ?? [])
        {
            string value = config.Settings.GetValueOrDefault(setting.Key) ?? setting.Default ?? "";
            switch (setting.Kind)
            {
                case ServerSettingKind.Boolean:
                {
                    var box = new CheckBox { IsChecked = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase), VerticalAlignment = VerticalAlignment.Center };
                    box.Click += (_, _) =>
                                 {
                                     config.Settings[setting.Key] = box.IsChecked == true ? "true" : "false";
                                     Saved();
                                 };
                    Field(grid, setting.DisplayName, box, setting.Description);
                    break;
                }
                case ServerSettingKind.Table:
                {
                    var box = new ComboBox { IsEditable = true, Text = value, ItemsSource = (_silo?.Data as ITableStore)?.ListTables().Select(t => t.Name).ToList() };
                    box.SelectionChanged += (_, _) =>
                                            {
                                                if (box.SelectedItem is string chosen) SetText(config, setting, chosen);
                                            };
                    box.LostKeyboardFocus += (_, _) => SetText(config, setting, box.Text);
                    Field(grid, setting.DisplayName, box, setting.Description);
                    break;
                }
                default:
                {
                    var box = new TextBox { Text = value };
                    Commit(box, () =>
                                {
                                    if (setting.Kind == ServerSettingKind.Number && box.Text.Trim().Length > 0 && !double.TryParse(box.Text, out _)) return "Write a number.";

                                    config.Settings[setting.Key] = box.Text.Trim();
                                    return null;
                                });
                    Field(grid, setting.DisplayName, box, setting.Description);
                    break;
                }
            }
        }

        var note = new TextBlock { Text = "Changes take effect when the server is next started.", Margin = new Thickness(0, 6, 0, 0) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        _editor.Children.Add(note);
    }

    private void SetText(ServerConfig config, ServerSetting setting, string text)
    {
        if (config.Settings.GetValueOrDefault(setting.Key) == text.Trim()) return;

        config.Settings[setting.Key] = text.Trim();
        Saved();
    }

    private ComboBox CertificatePicker(ServerConfig config)
    {
        var entries = new List<(string? Id, string Text)> { (null, "(none)") };
        try
        {
            if (CertificateActions.OpenVault(_silo) is { } vault)
                entries.AddRange(vault.List().Where(e => e.HasKey).Select(e => ((string?)e.Id, $"{e.Name} (until {e.Certificate.NotAfter:yyyy-MM-dd})")));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            entries.Add((config.CertificateId, "(the Silo's certificates cannot be read)"));
        }

        if (config.CertificateId is { } id && entries.All(e => e.Id != id)) entries.Add((id, "(a certificate that is no longer in the Silo)"));

        var box = new ComboBox { ItemsSource = entries.Select(e => e.Text).ToList(), SelectedIndex = Math.Max(0, entries.FindIndex(e => e.Id == config.CertificateId)) };
        box.ToolTip = entries.Count == 1 ? "The Silo holds no certificate with its private key: make or import one under Security." : "The keypair TLS endpoints present";
        box.SelectionChanged += (_, _) =>
                                {
                                    if (box.SelectedIndex < 0) return;

                                    config.CertificateId = entries[box.SelectedIndex].Id;
                                    Saved();
                                };
        return box;
    }

    /// <summary>Applies a text box when it loses focus or Enter is pressed; the apply returns an error to show, or null when it took.</summary>
    private void Commit(TextBox box, Func<string?> apply)
    {
        var original = box.Text;
        void Run()
        {
            if (box.Text == original) return;

            string? error;
            try
            {
                error = apply();
            }
            catch (FormatException ex)
            {
                error = ex.Message;
            }

            if (error is not null)
            {
                box.BorderBrush = Brushes.Firebrick;
                box.ToolTip     = error;
                App.Status(error);
                return;
            }

            box.ClearValue(Control.BorderBrushProperty);
            original = box.Text;
            Saved();
        }

        box.LostKeyboardFocus += (_, _) => Run();
        box.KeyDown += (_, e) =>
                       {
                           if (e.Key == System.Windows.Input.Key.Enter) Run();
                       };
    }

    private void Saved()
    {
        if (Persist()) RebuildList();
    }

    private static void Field(Grid grid, string label, UIElement editor, string? hint = null)
    {
        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 8, 3) };
        Grid.SetRow(text, row);
        grid.Children.Add(text);

        if (editor is FrameworkElement fe)
        {
            fe.Margin = new Thickness(0, 3, 0, 3);
            if (hint is not null && fe.ToolTip is null) fe.ToolTip = hint;
        }

        Grid.SetRow(editor, row);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
    }
}

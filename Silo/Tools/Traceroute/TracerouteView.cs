using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Input;
using Serilog;
using Silo.Drawio;
using Silo.Drawio.Extensions;
using Silo.Drawio.Host;
using Silo.Drawio.Live;
using Silo.Drawio.Mx;
using Silo.Drawio.Options;
using Diagram = Silo.DbModel.Global.Diagram;

namespace Silo.Tools.Traceroute;

/// <summary>
///     The Visual Traceroute pane: a bar with the destination and the trace options above an embedded draw.io diagram
///     (<see cref="DrawioView" />, profile <see cref="DrawioProfiles.Traceroute" />) that <see cref="TracerouteDiagramSource" />
///     fills while the trace runs.
/// </summary>
/// <remarks>
///     Besides the bar, the editor's Silo menu has New trace... (asks for a destination inside the editor), Stop and Save to Silo,
///     and a hop's right-click menu can copy its address or name or start a trace to it. Save to Silo keeps a snapshot of the
///     diagram (bindings evaluated, cells unlocked) as a canvas diagram of the active Silo, listed in the Explorer.
/// </remarks>
internal sealed class TracerouteView : DockPanel
{
    private const int HistorySize = 15;

    private const string MenuNew    = "trace.new";
    private const string MenuStop   = "trace.stop";
    private const string MenuSave   = "trace.save";
    private const string CopyAddr   = "trace.copyAddress";
    private const string CopyName   = "trace.copyName";
    private const string TraceThere = "trace.traceTo";

    private readonly ILogger _log = Log.ForContext<TracerouteView>();

    private readonly TracerouteDiagramSource _source = new();
    private readonly DrawioView              _view;

    private readonly ComboBox  _destination = new() { IsEditable = true, MinWidth = 220, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Button    _run         = new() { Content = "Trace", MinWidth = 64, Padding = new Thickness(10, 2, 10, 2) };
    private readonly Button    _save        = new() { Content = "Save to Silo", Padding = new Thickness(10, 2, 10, 2) };
    private readonly TextBox   _maxHops     = NumberBox("30", "Highest TTL probed (1-255)");
    private readonly TextBox   _probes      = NumberBox("3", "Echo requests per hop (1-10)");
    private readonly TextBox   _timeout     = NumberBox("2000", "How long a probe waits, in milliseconds (100-30000)", 52);
    private readonly TextBox   _inFlight    = NumberBox("4", "Hops probed at the same time (1 = one after another, like tracert)");
    private readonly ComboBox  _family      = new() { ItemsSource = new[] { "Any", "IPv4", "IPv6" }, SelectedIndex = 0, MinWidth = 58 };
    private readonly CheckBox  _names       = new() { Content = "Names", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Look up the host name of every address (reverse DNS)" };
    private readonly CheckBox  _collapse    = new() { Content = "Collapse non-responsive", IsChecked = false, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Fold two or more consecutive hops that never answered into one box (expand it with its +)" };
    private readonly CheckBox  _follow      = new() { Content = "Follow", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Scroll to each new hop as it appears" };
    private readonly TextBlock _status      = new() { Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly List<string> _history  = [];

    private CancellationTokenSource? _cancel;
    private int                      _runId;

    public TracerouteView()
    {
        // the boxes a trace draws are the person's to move and delete: the profile is unlocked whenever the pane opens
        var profile = DrawioProfiles.Instance.Get(DrawioProfiles.Traceroute);
        if (profile.Options.LockGeneratedCells)
        {
            profile.Options.LockGeneratedCells = false;
            profile.NotifyChanged();
        }

        _view = new DrawioView(DrawioProfiles.Traceroute) { Source = _source, Title = "Visual Traceroute" };

        _view.PluginMenu.InsertRange(0,
        [
            new DrawioPluginMenuItem(MenuNew, "New trace..."),
            new DrawioPluginMenuItem(MenuStop, "Stop trace"),
            new DrawioPluginMenuItem(MenuSave, "Save to Silo"),
            DrawioPluginMenuItem.Divider
        ]);
        _view.PluginContextItems.AddRange(
        [
            new DrawioPluginMenuItem(CopyAddr, "Copy address"),
            new DrawioPluginMenuItem(CopyName, "Copy host name"),
            new DrawioPluginMenuItem(TraceThere, "Trace route to this address")
        ]);

        _view.EventReceived    += OnEditorEvent;
        _view.LiveEditsApplied += OnLiveEditsApplied;

        var bar = new WrapPanel { Margin = new Thickness(6, 4, 6, 4) };
        DockPanel.SetDock(bar, Dock.Top);

        bar.Children.Add(Caption("Destination"));
        bar.Children.Add(_destination);
        bar.Children.Add(Spaced(_run, 6));
        bar.Children.Add(Caption("Max hops", 14));
        bar.Children.Add(_maxHops);
        bar.Children.Add(Caption("Probes", 10));
        bar.Children.Add(_probes);
        bar.Children.Add(Caption("Timeout ms", 10));
        bar.Children.Add(_timeout);
        bar.Children.Add(Caption("In flight", 10));
        bar.Children.Add(_inFlight);
        bar.Children.Add(Spaced(_family, 10));
        bar.Children.Add(Spaced(_names, 10));
        bar.Children.Add(Spaced(_collapse, 10));
        bar.Children.Add(Spaced(_follow, 10));
        bar.Children.Add(Spaced(_save, 14));
        bar.Children.Add(_status);

        _destination.ToolTip = "A host name, an IPv4 or IPv6 address, or a URL";
        _destination.ItemsSource = _history;
        _destination.PreviewKeyDown += (_, e) =>
                                       {
                                           if (e.Key != Key.Enter) return;
                                           e.Handled = true;
                                           Start();
                                       };

        _source.CollapseSilent =  _collapse.IsChecked == true;
        _collapse.Checked      += (_, _) => _source.CollapseSilent = true;
        _collapse.Unchecked    += (_, _) => _source.CollapseSilent = false;

        _run.Click  += (_, _) => { if (_cancel is null) Start(); else Stop(); };
        _save.Click += async (_, _) => await SaveToSiloAsync();

        Children.Add(bar);
        Children.Add(_view);

        DrawioDesign.Attach(this, _view);

        _status.Text = _source.Status;
        Loaded += (_, _) =>
                  {
                      if (string.IsNullOrWhiteSpace(_destination.Text)) _destination.Focus();
                  };
    }

    /// <summary>Whether a trace is running.</summary>
    public bool IsRunning => _cancel is not null;

    // ---- running ---------------------------------------------------------------------------------

    /// <summary>Starts a trace to <paramref name="destination" /> (null: the one in the bar), stopping any trace still running.</summary>
    public async void Start(string? destination = null)
    {
        if (destination is not null) _destination.Text = destination;

        string target = _destination.Text?.Trim() ?? "";
        if (target.Length == 0)
        {
            _status.Text = "Enter a host name or address to trace the route to.";
            _destination.Focus();
            return;
        }

        if (!TryReadOptions(out var options, out string? problem))
        {
            _status.Text = problem;
            return;
        }

        Remember(target);
        _cancel?.Cancel();

        var cancel = new CancellationTokenSource();
        int runId  = ++_runId;
        _cancel = cancel;
        SetRunning(true);

        _view.ForgetGeneratedEdits(); // a new trace: hop ids start over, so earlier moves and deletions no longer apply
        _source.Begin(target);
        _status.Text = _source.Status;

        try
        {
            var engine = new TraceRoute();
            await Task.Run(() => engine.RunAsync(target, options, update => Dispatcher.BeginInvoke(new Action(() => OnUpdate(runId, update))), cancel.Token));
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "The trace to {destination} failed", target);
            if (runId == _runId) _status.Text = "The trace failed: " + ex.Message;
        }
        finally
        {
            cancel.Dispose();
            if (runId == _runId)
            {
                _cancel = null;
                SetRunning(false);
            }
        }
    }

    /// <summary>Stops the running trace (what was found stays on the diagram).</summary>
    public void Stop()
    {
        try
        {
            _cancel?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // it had just finished
        }
    }

    private void OnUpdate(int runId, TraceUpdate update)
    {
        if (runId != _runId) return; // a late update of a trace that was replaced

        _source.Apply(update);
        _status.Text = _source.Status;
    }

    private void SetRunning(bool running)
    {
        _run.Content = running ? "Stop" : "Trace";
        foreach (var box in new Control[] { _maxHops, _probes, _timeout, _inFlight, _family, _names }) box.IsEnabled = !running;
    }

    private bool TryReadOptions(out TraceOptions options, out string? problem)
    {
        options = new TraceOptions();
        problem = null;

        if (!Read(_maxHops, 1, 255, "Max hops", out int maxHops, ref problem) ||
            !Read(_probes, 1, 10, "Probes", out int probes, ref problem) ||
            !Read(_timeout, 100, 30000, "Timeout", out int timeout, ref problem) ||
            !Read(_inFlight, 1, 32, "In flight", out int inFlight, ref problem))
            return false;

        options = new TraceOptions
                  {
                      MaxHops      = maxHops,
                      ProbesPerHop = probes,
                      Timeout      = TimeSpan.FromMilliseconds(timeout),
                      HopsInFlight = inFlight,
                      ResolveNames = _names.IsChecked == true,
                      Family       = _family.SelectedIndex switch { 1 => TraceAddressFamily.IPv4, 2 => TraceAddressFamily.IPv6, _ => TraceAddressFamily.Any }
                  };
        return true;
    }

    private static bool Read(TextBox box, int min, int max, string what, out int value, ref string? problem)
    {
        if (int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out value) && value >= min && value <= max) return true;

        problem = $"{what} must be a whole number from {min} to {max}.";
        box.Focus();
        box.SelectAll();
        return false;
    }

    private void Remember(string destination)
    {
        _history.RemoveAll(h => string.Equals(h, destination, StringComparison.OrdinalIgnoreCase));
        _history.Insert(0, destination);
        if (_history.Count > HistorySize) _history.RemoveRange(HistorySize, _history.Count - HistorySize);

        _destination.Items.Refresh();
        _destination.Text = destination;
    }

    // ---- the editor --------------------------------------------------------------------------------

    private void OnLiveEditsApplied(object? sender, IReadOnlyList<DiagramEdit> edits)
    {
        if (_follow.IsChecked != true) return;
        if (edits.OfType<DiagramEdit.AddNode>().LastOrDefault() is { } added) _ = _view.PluginScrollToCellAsync(added.Node.Id);
    }

    private async void OnEditorEvent(object? sender, DrawioMessage m)
    {
        try
        {
            if (m.Name == SiloPluginEvent.Menu)
            {
                switch (m.GetString("id"))
                {
                    case MenuNew:
                        if (await _view.PromptAsync("Trace the route to (host name or address)", "Trace", _destination.Text) is { Length: > 0 } typed)
                            Start(typed);
                        break;

                    case MenuStop:
                        Stop();
                        break;

                    case MenuSave:
                        await SaveToSiloAsync();
                        break;
                }
            }
            else if (m.Name == SiloPluginEvent.ContextAction)
            {
                OnContextAction(m.GetString("id"), m.Get("attributes"));
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Handling the traceroute editor event {event} failed", m.Name);
        }
    }

    private void OnContextAction(string? id, JsonElement? attributes)
    {
        string? kind = AttributeOf(attributes, "siloKind");
        string? address;
        string? name;

        if (kind == TracerouteDiagramSource.LocalKind)
        {
            address = _source.Target?.LocalAddress?.ToString();
            name    = _source.Target?.LocalName;
        }
        else if (int.TryParse(AttributeOf(attributes, TracerouteDiagramSource.HopAttribute), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) &&
                 _source.HopAt(n) is { } hop)
        {
            address = hop.Address?.ToString();
            name    = hop.Name;
        }
        else
        {
            return;
        }

        switch (id)
        {
            case CopyAddr when address is not null:
                Copy(address);
                break;

            case CopyName:
                if (name is not null) Copy(name);
                else _status.Text = "That hop has no host name.";
                break;

            case TraceThere when address is not null:
                Start(address);
                break;

            default:
                _status.Text = "That hop has not answered yet.";
                break;
        }
    }

    private static string? AttributeOf(JsonElement? attributes, string name)
    {
        return attributes is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private void Copy(string text)
    {
        try
        {
            Clipboard.SetText(text);
            _status.Text = $"Copied {text}";
        }
        catch (Exception ex)
        {
            _status.Text = "Could not copy: " + ex.Message;
        }
    }

    // ---- keeping a trace -------------------------------------------------------------------------------

    /// <summary>Saves the diagram as it stands, as a plain canvas diagram of the active Silo.</summary>
    private async Task SaveToSiloAsync()
    {
        if (App.Instance?.ActiveSilo is not { IsWritable: true } silo || silo.Data is not IDiagramStore store)
        {
            _status.Text = "Open a Silo that can be written to, to save the trace into it.";
            return;
        }

        if (_source.Target is null && _source.Hops.Count == 0)
        {
            _status.Text = "There is no trace to save yet.";
            return;
        }

        try
        {
            string? xml = await _view.GetXmlAsync();
            var     doc = DrawioDocument.Parse(xml);
            DiagramBindings.Freeze(doc);

            string what = _source.Target?.HostName ?? _source.Target?.Address.ToString() ?? _source.Destination ?? "trace";
            var saved = store.SaveDiagram(new Diagram
                                          {
                                              Name    = $"Traceroute to {what} {DateTime.Now:yyyy-MM-dd HH.mm}",
                                              Kind    = "canvas",
                                              Profile = DrawioProfiles.Canvas,
                                              Xml     = doc.ToXml()
                                          });
            DiagramEvents.RaiseChanged();
            _status.Text = $"Saved \"{saved.Name}\" to the Silo's diagrams.";
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Saving the trace failed");
            _status.Text = "Saving the trace failed: " + ex.Message;
        }
    }

    // ---- bits ------------------------------------------------------------------------------------

    private static TextBox NumberBox(string text, string tip, double width = 36)
    {
        return new TextBox { Text = text, Width = width, ToolTip = tip, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
    }

    private static TextBlock Caption(string text, double left = 0)
    {
        return new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(left, 0, 4, 0) };
    }

    private static T Spaced<T>(T element, double left) where T : FrameworkElement
    {
        element.Margin            = new Thickness(left, 0, 0, 0);
        element.VerticalAlignment = VerticalAlignment.Center;
        return element;
    }
}

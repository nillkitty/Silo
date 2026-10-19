using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace Silo.Labs;

/// <summary>Small helpers for the code-built views of the Web Lab's inspectors.</summary>
internal static class WebUi
{
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");

    public static T Muted<T>(this T element) where T : FrameworkElement
    {
        element.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        return element;
    }

    public static ListBox ThemedList(IEnumerable items)
    {
        var list = new ListBox
                   {
                       ItemsSource                = items, BorderThickness = new Thickness(0),
                       HorizontalContentAlignment = HorizontalAlignment.Stretch
                   };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        list.SetResourceReference(Control.BackgroundProperty, "Theme.Background");
        list.SetResourceReference(Control.ForegroundProperty, "Theme.Text");
        return list;
    }

    public static Button Tool(string text, Action click, string? tip = null)
    {
        var button = new Button { Content = text, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0), ToolTip = tip };
        button.Click += (_, _) => click();
        return button;
    }

    public static TextBox Output(double height = 0)
    {
        var box = new TextBox
                  {
                      IsReadOnly                    = true,
                      FontFamily                    = Mono,
                      TextWrapping                  = TextWrapping.NoWrap,
                      VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                      HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                      BorderThickness               = new Thickness(0),
                      Padding                       = new Thickness(6)
                  };
        if (height > 0) box.Height = height;
        box.SetResourceReference(Control.BackgroundProperty, "Theme.Background");
        box.SetResourceReference(Control.ForegroundProperty, "Theme.Text");
        return box;
    }

    public static string FormatSize(double bytes)
    {
        if (bytes < 1024) return $"{bytes:0} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024:0.#} KB";
        return $"{bytes / 1024 / 1024:0.#} MB";
    }
}

/// <summary>
///     The base of the Web Lab's inspectors: each is a view of what the page is doing, read through the DevTools protocol
///     once the page's browser is up (<see cref="Attach" />).
/// </summary>
internal abstract class WebInspector : LabInspector
{
    /// <summary>The DevTools protocol of the page; null until the browser is up.</summary>
    protected DevTools? Dev { get; private set; }

    protected WebLabSession Session { get; }

    internal void Attach(DevTools dev)
    {
        Dev = dev;
        OnAttached();
    }

    protected virtual void OnAttached()
    {
    }

    /// <summary>The page is about to go somewhere else.</summary>
    internal virtual void OnNavigating(Uri uri)
    {
    }

    /// <summary>The page finished loading.</summary>
    internal virtual void OnNavigated(Uri? uri)
    {
    }

    /// <summary>Runs work the inspector does not wait for; a failure is logged, never thrown at the user.</summary>
    protected static async void RunSafe(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "A Web Lab inspector's work failed");
        }
    }

    protected WebInspector(WebLabSession session)
    {
        Session = session;
    }
}

// ---- Console --------------------------------------------------------------------------------

/// <summary>One line of the console: what the page logged, an error it raised, or what was evaluated.</summary>
internal sealed class ConsoleEntry
{
    public Brush  Brush { get; }
    public string Level { get; }
    public string Text  { get; }

    public string Time { get; }

    public ConsoleEntry(string level, Brush brush, string text)
    {
        Time  = DateTime.Now.ToString("HH:mm:ss");
        Level = level;
        Brush = brush;
        Text  = text;
    }
}

/// <summary>The page's console: what it logs and the errors it raises, and a line to evaluate JavaScript in the page.</summary>
internal sealed class ConsoleInspector : WebInspector
{
    private const int Limit = 2000;

    private static readonly Brush Red    = Frozen(Color.FromRgb(0xE5, 0x48, 0x4D));
    private static readonly Brush Orange = Frozen(Color.FromRgb(0xE0, 0x8A, 0x1E));
    private static readonly Brush Blue   = Frozen(Color.FromRgb(0x3B, 0x82, 0xD6));
    private static readonly Brush Gray   = Frozen(Color.FromRgb(0x8A, 0x8F, 0x98));

    private readonly ObservableCollection<ConsoleEntry> _entries = [];
    private          ListBox?                           _list;

    public override LabDock Dock => LabDock.Right;

    public override string IconKey => "Terminal";

    public override string Id => "console";

    public override string Title => "Console";

    protected override UIElement BuildView()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4, 6, 4) };
        bar.Children.Add(WebUi.Tool("Clear", () => _entries.Clear(), "Clear the console"));
        Grid.SetRow(bar, 0);
        root.Children.Add(bar);

        _list              = WebUi.ThemedList(_entries);
        _list.ItemTemplate = Template();
        Grid.SetRow(_list, 1);
        root.Children.Add(_list);

        var eval = new TextBox
                   {
                       FontFamily = WebUi.Mono,
                       Margin     = new Thickness(6, 4, 6, 6),
                       ToolTip    = "JavaScript to evaluate in the page; Enter runs it (top-level await works)"
                   };
        eval.KeyDown += async (_, e) =>
                        {
                            if (e.Key != Key.Enter || eval.Text.Trim().Length == 0) return;

                            string code = eval.Text.Trim();
                            eval.Clear();
                            Add("›", Gray, code);
                            Add("‹", Gray, await EvaluateAsync(code));
                        };
        Grid.SetRow(eval, 2);
        root.Children.Add(eval);

        _entries.CollectionChanged += (_, e) =>
                                      {
                                          if (e.Action == NotifyCollectionChangedAction.Add && _entries.Count > 0)
                                              _list.Dispatcher.InvokeAsync(() => _list.ScrollIntoView(_entries[^1]));
                                      };
        return root;
    }

    private static DataTemplate Template()
    {
        var level = new FrameworkElementFactory(typeof(TextBlock));
        level.SetBinding(TextBlock.TextProperty,       new Binding(nameof(ConsoleEntry.Level)));
        level.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(ConsoleEntry.Brush)));
        level.SetValue(TextBlock.WidthProperty,      34d);
        level.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        level.SetValue(TextBlock.FontFamilyProperty, WebUi.Mono);
        level.SetValue(DockPanel.DockProperty,       System.Windows.Controls.Dock.Left);

        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(ConsoleEntry.Text)));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetValue(TextBlock.FontFamilyProperty,   WebUi.Mono);

        var panel = new FrameworkElementFactory(typeof(DockPanel));
        panel.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 1, 4, 1));
        panel.AppendChild(level);
        panel.AppendChild(text);
        return new DataTemplate { VisualTree = panel };
    }

    protected override void OnAttached()
    {
        Dev!.On("Runtime.consoleAPICalled", e =>
                                            {
                                                string type = (string?)e["type"] ?? "log";
                                                IEnumerable<string> args =
                                                    (e["args"] as JArray)?.Select(a => Describe(a)) ?? Array.Empty<string>();
                                                AddLevel(type, string.Join(" ", args));
                                            });
        Dev.On("Runtime.exceptionThrown", e =>
                                          {
                                              var details = e["exceptionDetails"];
                                              string text = (string?)details?["exception"]?["description"] ??
                                                            (string?)details?["text"] ?? "Uncaught exception";
                                              Add("ERR", Red, text);
                                          });
        Dev.On("Log.entryAdded", e =>
                                 {
                                     var    entry = e["entry"];
                                     string url   = (string?)entry?["url"] ?? string.Empty;
                                     AddLevel((string?)entry?["level"] ?? "info",
                                              $"{(string?)entry?["text"]}{(url.Length > 0 ? "  " + url : string.Empty)}");
                                 });

        RunSafe(async () =>
                {
                    await Dev.EnableAsync("Runtime");
                    await Dev.EnableAsync("Log");
                });
    }

    internal override void OnNavigating(Uri uri)
    {
        Add("—", Gray, uri.AbsoluteUri);
    }

    /// <summary>Evaluates JavaScript in the page and returns the result as text. Never throws: a problem is the text.</summary>
    public async Task<string> EvaluateAsync(string expression)
    {
        if (Dev is null) return "The page is not up yet.";

        try
        {
            var response = await Dev.CallAsync("Runtime.evaluate",
                                               new { expression, awaitPromise = true, replMode = true, generatePreview = true });
            if (response["exceptionDetails"] is JObject failure)
                return "! " + ((string?)failure["exception"]?["description"] ?? (string?)failure["text"] ?? "exception");

            var result = response["result"];
            if (result is JObject { } obj && (string?)obj["type"] == "object" && (string?)obj["subtype"] != "null" &&
                (string?)obj["objectId"] is { } objectId)
            {
                // an object reads best as JSON (a node as its HTML)
                var text = await Dev.TryCallAsync("Runtime.callFunctionOn", new
                                                                            {
                                                                                objectId,
                                                                                functionDeclaration =
                                                                                    "function(){ try { if (this instanceof Node) return this.outerHTML || this.textContent; return JSON.stringify(this, null, 1); } catch (e) { return String(this); } }",
                                                                                returnByValue = true
                                                                            });
                if (text?["result"]?["value"] is { Type: JTokenType.String } value)
                {
                    string s = value.ToString();
                    return s.Length <= 6000 ? s : s[..6000] + "...";
                }
            }

            return Describe(result);
        }
        catch (Exception ex)
        {
            return "! " + ex.Message;
        }
    }

    private static string Describe(JToken? arg)
    {
        if (arg is null) return string.Empty;
        if (arg["value"] is { } value) return value.Type == JTokenType.String ? value.ToString() : value.ToString(Formatting.None);
        if ((string?)arg["unserializableValue"] is { } special) return special;
        if ((string?)arg["description"] is { } description) return description;
        return (string?)arg["type"] ?? string.Empty;
    }

    private void AddLevel(string type, string text)
    {
        switch (type.ToLowerInvariant())
        {
            case "error":
            case "assert":
                Add("ERR", Red, text);
                break;
            case "warning":
            case "warn":
                Add("WRN", Orange, text);
                break;
            case "info":
                Add("INF", Blue, text);
                break;
            case "debug":
            case "verbose":
                Add("DBG", Gray, text);
                break;
            default:
                Add("LOG", Gray, text);
                break;
        }
    }

    private void Add(string level, Brush brush, string text)
    {
        _entries.Add(new ConsoleEntry(level, brush, text));
        while (_entries.Count > Limit) _entries.RemoveAt(0);
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public ConsoleInspector(WebLabSession session) : base(session)
    {
    }
}

// ---- Network --------------------------------------------------------------------------------

/// <summary>One request of the page, filled in as the answer comes.</summary>
internal sealed class NetworkEntry : INotifyPropertyChanged
{
    private string _flags  = string.Empty;
    private string _size   = string.Empty;
    private string _status = "...";
    private string _time   = string.Empty;
    public  string Error { get; set; } = string.Empty;

    /// <summary>What the Lab itself did to the request: blocked, replaced, headers rewritten.</summary>
    public string Flags
    {
        get => _flags;
        set => Set(ref _flags, value);
    }

    public string Host   { get; }
    public string Method { get; }
    public string Mime   { get; set; } = string.Empty;
    public string Path   { get; }

    public string RequestId { get; }

    public string Size
    {
        get => _size;
        set => Set(ref _size, value);
    }

    public long Started { get; }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public string Time
    {
        get => _time;
        set => Set(ref _time, value);
    }

    public string Type { get; }
    public string Url  { get; }

    private void Set(ref string field, string value, [CallerMemberName] string? name = null)
    {
        if (field == value) return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public NetworkEntry(string requestId, string method, string url, string type)
    {
        RequestId = requestId;
        Method    = method;
        Url       = url;
        Type      = type;
        Started   = Stopwatch.GetTimestamp();

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            Host = uri.Scheme is "http" or "https" ? uri.Host : uri.Scheme                                 + ":";
            Path = uri.Scheme is "http" or "https" ? uri.PathAndQuery : url.Length <= 80 ? url : url[..80] + "...";
        }
        else
        {
            Host = string.Empty;
            Path = url;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
///     Every request the page makes (the "Sources" of a page): what was asked, the status, type, size and time of the
///     answer, and
///     whether the Lab blocked, replaced or rewrote it.
/// </summary>
internal sealed class NetworkInspector : WebInspector
{
    private const    int                              Limit = 3000;
    private readonly Dictionary<string, NetworkEntry> _byId = new(StringComparer.Ordinal);

    private readonly ObservableCollection<NetworkEntry> _entries = [];
    private          TextBox?                           _details;

    private TextBox?  _filter;
    private CheckBox? _keep;

    public override LabDock Dock => LabDock.Right;

    public override string IconKey => "Network";

    public override string Id => "network";

    public override string Title => "Network";

    protected override UIElement BuildView()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var bar  = new DockPanel { Margin       = new Thickness(6, 4, 6, 4) };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(WebUi.Tool("Clear", Clear, "Forget the requests so far"));

        _keep = new CheckBox
                {
                    Content = "Keep", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
                    ToolTip = "Keep the requests when the page goes somewhere else"
                };
        left.Children.Add(_keep);

        var noCache = new CheckBox
                      {
                          Content = "No cache", VerticalAlignment = VerticalAlignment.Center,
                          Margin  = new Thickness(0, 0, 8, 0),
                          ToolTip = "Ask the network for everything, ignoring the browser's cache"
                      };
        noCache.Click += (_, _) => RunSafe(async () =>
                                           {
                                               if (Dev is not null)
                                                   await Dev.CallAsync("Network.setCacheDisabled",
                                                                       new { cacheDisabled = noCache.IsChecked == true });
                                           });
        left.Children.Add(noCache);
        DockPanel.SetDock(left, System.Windows.Controls.Dock.Left);
        bar.Children.Add(left);

        _filter             =  new TextBox { ToolTip = "Show only requests whose address contains this text" };
        _filter.TextChanged += (_, _) => CollectionViewSource.GetDefaultView(_entries).Refresh();
        bar.Children.Add(_filter);
        Grid.SetRow(bar, 0);
        root.Children.Add(bar);

        var grid = new GridView();
        grid.Columns.Add(Column("Status", nameof(NetworkEntry.Status), 52));
        grid.Columns.Add(Column("Method", nameof(NetworkEntry.Method), 56));
        grid.Columns.Add(Column("Type",   nameof(NetworkEntry.Type),   70));
        grid.Columns.Add(Column("Host",   nameof(NetworkEntry.Host),   140));
        grid.Columns.Add(Column("Path",   nameof(NetworkEntry.Path),   240));
        grid.Columns.Add(Column("Size",   nameof(NetworkEntry.Size),   64));
        grid.Columns.Add(Column("Time",   nameof(NetworkEntry.Time),   60));
        grid.Columns.Add(Column("Lab",    nameof(NetworkEntry.Flags),  90));

        var list = new ListView { ItemsSource = _entries, View = grid, BorderThickness = new Thickness(0) };
        list.SetResourceReference(Control.BackgroundProperty, "Theme.Background");
        list.SetResourceReference(Control.ForegroundProperty, "Theme.Text");
        CollectionViewSource.GetDefaultView(_entries).Filter =  o => o is NetworkEntry entry && Matches(entry);
        list.SelectionChanged                                += (_, _) => ShowDetails(list.SelectedItem as NetworkEntry);
        list.MouseDoubleClick += (_, _) =>
                                 {
                                     if (list.SelectedItem is not NetworkEntry entry) return;

                                     try
                                     {
                                         Clipboard.SetText(entry.Url);
                                         Session.Say("Copied " + entry.Url);
                                     }
                                     catch (Exception ex)
                                     {
                                         Log.Debug(ex, "Copying a request address failed");
                                     }
                                 };
        Grid.SetRow(list, 1);
        root.Children.Add(list);

        _details = WebUi.Output(110);
        Grid.SetRow(_details, 2);
        root.Children.Add(_details);
        return root;
    }

    private static GridViewColumn Column(string header, string property, double width)
    {
        return new GridViewColumn { Header = header, Width = width, DisplayMemberBinding = new Binding(property) };
    }

    private bool Matches(NetworkEntry entry)
    {
        string text = _filter?.Text.Trim() ?? string.Empty;
        return text.Length == 0 || entry.Url.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private void ShowDetails(NetworkEntry? entry)
    {
        if (_details is null) return;

        _details.Text = entry is null
                            ? string.Empty
                            : string.Join(Environment.NewLine, $"{entry.Method} {entry.Url}",
                                          $"Status: {entry.Status}   Type: {entry.Type}   Mime: {entry.Mime}",
                                          $"Size: {entry.Size}   Time: {entry.Time}   Lab: {(entry.Flags.Length > 0 ? entry.Flags : "-")}",
                                          entry.Error.Length > 0 ? "Error: " + entry.Error : string.Empty);
    }

    private void Clear()
    {
        _entries.Clear();
        _byId.Clear();
        ShowDetails(null);
    }

    protected override void OnAttached()
    {
        Dev!.On("Network.requestWillBeSent", e =>
                                             {
                                                 string id = (string?)e["requestId"] ?? string.Empty;
                                                 if (_byId.TryGetValue(id, out var previous) && e["redirectResponse"] is JObject redirect)
                                                     previous.Status = ((int?)redirect["status"])?.ToString() ?? "redirect";

                                                 var request = e["request"];
                                                 var entry = new NetworkEntry(id, (string?)request?["method"] ?? "GET",
                                                                              (string?)request?["url"]        ?? string.Empty,
                                                                              (string?)e["type"]              ?? "Other");
                                                 entry.Flags = Session.FlagsFor(entry.Url);
                                                 _byId[id]   = entry;
                                                 _entries.Add(entry);
                                                 while (_entries.Count > Limit)
                                                 {
                                                     _byId.Remove(_entries[0].RequestId);
                                                     _entries.RemoveAt(0);
                                                 }
                                             });
        Dev.On("Network.responseReceived", e =>
                                           {
                                               if (!_byId.TryGetValue((string?)e["requestId"] ?? string.Empty, out var entry)) return;

                                               var response = e["response"];
                                               entry.Status = ((int?)response?["status"])?.ToString() ?? "?";
                                               entry.Mime   = (string?)response?["mimeType"]          ?? string.Empty;
                                           });
        Dev.On("Network.loadingFinished", e =>
                                          {
                                              if (!_byId.TryGetValue((string?)e["requestId"] ?? string.Empty, out var entry)) return;

                                              entry.Size = WebUi.FormatSize((double?)e["encodedDataLength"] ?? 0);
                                              entry.Time = $"{Stopwatch.GetElapsedTime(entry.Started).TotalMilliseconds:0} ms";
                                          });
        Dev.On("Network.loadingFailed", e =>
                                        {
                                            if (!_byId.TryGetValue((string?)e["requestId"] ?? string.Empty, out var entry)) return;

                                            entry.Error  = (string?)e["errorText"] ?? string.Empty;
                                            entry.Status = (bool?)e["canceled"] == true ? "canceled" : "failed";
                                            entry.Time   = $"{Stopwatch.GetElapsedTime(entry.Started).TotalMilliseconds:0} ms";
                                            if (entry.Flags.Length == 0 && (string?)e["blockedReason"] is { Length: > 0 } reason)
                                                entry.Flags = reason;
                                        });

        // the Lab hears of a request it blocked, replaced or rewrote, possibly after the protocol told of the request
        Session.FlagChanged += (url, flags) =>
                               {
                                   var entry                          = _entries.LastOrDefault(en => en.Url == url);
                                   if (entry is not null) entry.Flags = flags;
                               };

        RunSafe(() => Dev.EnableAsync("Network"));
    }

    internal override void OnNavigating(Uri uri)
    {
        if (_keep?.IsChecked != true) Clear();
    }

    public NetworkInspector(WebLabSession session) : base(session)
    {
    }
}
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace Silo.Labs;

/// <summary>
///     The text filter of the Console and Network panes: plain text matches anywhere (any case); text between slashes is a regular
///     expression; a word with a dash in front must not match.
/// </summary>
public sealed class WebTextFilter
{
    private readonly List<(bool Not, Func<string, bool> Test)> _terms = [];

    /// <summary>Reads the filter text; an empty text matches everything.</summary>
    public WebTextFilter(string? text)
    {
        foreach (string word in Words(text))
        {
            bool   not = word.Length > 1 && word[0] == '-';
            string body = not ? word[1..] : word;
            if (body.Length > 2 && body[0] == '/' && body[^1] == '/')
            {
                try
                {
                    var regex = new Regex(body[1..^1], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                    _terms.Add((not, s => SafeIsMatch(regex, s)));
                    continue;
                }
                catch (ArgumentException)
                {
                    // not a regular expression after all: it is text
                }
            }

            _terms.Add((not, s => s.Contains(body, StringComparison.OrdinalIgnoreCase)));
        }
    }

    public bool IsEmpty => _terms.Count == 0;

    /// <summary>
    ///     Cuts the filter text into its words at the spaces; a word that starts with <c>/</c> (or <c>-/</c>) and ends with <c>/</c> can have
    ///     spaces in it: it is a regular expression.
    /// </summary>
    public static IEnumerable<string> Words(string? text)
    {
        text ??= string.Empty;
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == ' ')
            {
                i++;
                continue;
            }

            int  start = i;
            bool regex = text[i] == '/' || (text[i] == '-' && i + 1 < text.Length && text[i + 1] == '/');
            int  end   = -1;
            if (regex)
            {
                // the closing slash is the first one that is followed by a space or the end
                for (int j = text.IndexOf('/', start + (text[i] == '-' ? 2 : 1)); j >= 0; j = text.IndexOf('/', j + 1))
                {
                    if (j + 1 == text.Length || text[j + 1] == ' ')
                    {
                        end = j + 1;
                        break;
                    }
                }
            }

            if (end < 0)
            {
                end = text.IndexOf(' ', start);
                if (end < 0) end = text.Length;
            }

            yield return text[start..end];
            i = end;
        }
    }

    /// <summary>Whether the text passes every term.</summary>
    public bool Matches(string text)
    {
        foreach (var (not, test) in _terms)
            if (test(text) == not)
                return false;

        return true;
    }

    private static bool SafeIsMatch(Regex regex, string text)
    {
        try
        {
            return regex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}

// ---- Console --------------------------------------------------------------------------------

/// <summary>One line of the console: what the page logged, an error it raised, or what was evaluated.</summary>
internal sealed class ConsoleEntry : INotifyPropertyChanged
{
    private int _count = 1;

    public ConsoleEntry(string level, Brush brush, string text, string source = "", string stack = "")
    {
        Time   = DateTime.Now.ToString("HH:mm:ss");
        Level  = level;
        Brush  = brush;
        Text   = text;
        Source = source;
        Stack  = stack;
    }

    public Brush Brush { get; }

    /// <summary>How many times in a row the same thing was logged.</summary>
    public int Count
    {
        get => _count;
        set
        {
            if (_count == value) return;

            _count = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Repeats)));
        }
    }

    public string Level { get; }

    /// <summary>"×3", or nothing for a line seen once.</summary>
    public string Repeats => _count > 1 ? $"×{_count}" : string.Empty;

    /// <summary>Where in the page it came from ("app.js:12:5").</summary>
    public string Source { get; }

    /// <summary>The call stack, a line to a frame.</summary>
    public string Stack { get; }

    public string Text { get; }

    public string Time { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string AsText() => $"{Time} {Level} {Text}{(Source.Length > 0 ? "  (" + Source + ")" : string.Empty)}{(Stack.Length > 0 ? Environment.NewLine + Stack : string.Empty)}";
}

/// <summary>
///     The page's console: what it logs and the errors it raises (with where they came from and the stack), a line to evaluate JavaScript in
///     the page, a filter by level and text, and the console kept across navigations, copied or saved.
/// </summary>
internal sealed class ConsoleInspector : WebInspector
{
    private const int Limit = 2000;

    private static readonly Brush Red    = Frozen(Color.FromRgb(0xE5, 0x48, 0x4D));
    private static readonly Brush Orange = Frozen(Color.FromRgb(0xE0, 0x8A, 0x1E));
    private static readonly Brush Blue   = Frozen(Color.FromRgb(0x3B, 0x82, 0xD6));
    private static readonly Brush Gray   = Frozen(Color.FromRgb(0x8A, 0x8F, 0x98));

    private static readonly string[] Levels = ["ERR", "WRN", "INF", "LOG", "DBG"];

    private readonly ObservableCollection<ConsoleEntry> _entries = [];
    private readonly List<string>                       _history = [];
    private readonly HashSet<string>                    _hidden  = new(StringComparer.Ordinal);

    private TextBox?     _details;
    private TextBox?     _filter;
    private WebTextFilter _text = new(null);
    private CheckBox?    _keep;
    private int          _historyIndex;
    private ListBox?     _list;
    private bool         _keepEntries = true;
    private readonly Dictionary<string, ToggleButton> _levelButtons = [];

    public ConsoleInspector(WebLabSession session) : base(session)
    {
    }

    public override LabDock Dock => LabDock.Right;

    public override string IconKey => "Terminal";

    public override string Id => "console";

    public override string Title => "Console";

    /// <summary>The filter as a preset: which levels are hidden, and the text.</summary>
    public LabPresetKind Preset => new("console-filter", "Console filter", () => new Dictionary<string, string> { ["hidden"] = string.Join(",", _hidden), ["text"] = _filter?.Text ?? _filterText, ["keep"] = _keepEntries.ToString() },
                                       settings => ApplyFilter(settings.GetValueOrDefault("hidden") ?? string.Empty, settings.GetValueOrDefault("text") ?? string.Empty, !bool.TryParse(settings.GetValueOrDefault("keep"), out bool keep) || keep),
                                       "Terminal");

    private string _filterText = string.Empty;

    private void ApplyFilter(string hidden, string text, bool keep)
    {
        _hidden.Clear();
        foreach (string level in hidden.Split(',', StringSplitOptions.RemoveEmptyEntries)) _hidden.Add(level);
        _filterText  = text;
        _keepEntries = keep;
        if (_filter is not null) _filter.Text = text;
        if (_keep is not null) _keep.IsChecked = keep;
        foreach (var (level, button) in _levelButtons) button.IsChecked = !_hidden.Contains(level);
        _text = new WebTextFilter(text);
        Refilter();
    }

    protected override UIElement BuildView()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // row 1: the buttons; row 2 (inside the same cell): the levels and the filter
        var top = new StackPanel();
        var bar = new WrapPanel { Margin = new Thickness(6, 4, 6, 2) };
        bar.Children.Add(WebUi.Tool("Clear", Clear, "Clear the console"));
        _keep = new CheckBox
                {
                    Content = "Keep", IsChecked = _keepEntries, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0),
                    ToolTip = "Keep the console when the page goes somewhere else (off: it is cleared at each navigation)"
                };
        _keep.Click += (_, _) => _keepEntries = _keep.IsChecked == true;
        bar.Children.Add(_keep);
        bar.Children.Add(WebUi.Tool("Copy", CopyShown, "Copy the lines shown (or the ones selected) to the clipboard", "TbCopy"));
        bar.Children.Add(WebUi.Tool("Save", Save, "Save the lines shown to a file", "TbSave"));
        top.Children.Add(bar);

        var filterBar = new DockPanel { Margin = new Thickness(6, 2, 6, 4) };
        var levels    = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (string level in Levels)
        {
            string captured = level;
            var button = new ToggleButton
                         {
                             Content = level, IsChecked = !_hidden.Contains(level), Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(0, 0, 3, 0), MinWidth = 36,
                             ToolTip = $"Show or hide {level} lines"
                         };
            button.Click += (_, _) =>
                            {
                                if (button.IsChecked == true) _hidden.Remove(captured);
                                else _hidden.Add(captured);
                                Refilter();
                            };
            _levelButtons[level] = button;
            levels.Children.Add(button);
        }

        DockPanel.SetDock(levels, System.Windows.Controls.Dock.Left);
        filterBar.Children.Add(levels);
        _filter = new TextBox { Text = _filterText, ToolTip = "Show only lines with this text. /pattern/ is a regular expression; -word hides lines with the word", Margin = new Thickness(4, 0, 0, 0) };
        _filter.TextChanged += (_, _) =>
                               {
                                   _filterText = _filter.Text;
                                   _text       = new WebTextFilter(_filter.Text);
                                   Refilter();
                               };
        filterBar.Children.Add(_filter);
        top.Children.Add(filterBar);
        Grid.SetRow(top, 0);
        root.Children.Add(top);

        _list              = WebUi.ThemedList(_entries);
        _list.ItemTemplate = Template();
        _list.SelectionMode = SelectionMode.Extended;
        _list.SelectionChanged += (_, _) => ShowDetails(_list.SelectedItem as ConsoleEntry);
        _list.KeyDown += (_, e) =>
                         {
                             if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) CopyShown();
                         };
        CollectionViewSource.GetDefaultView(_entries).Filter = o => o is ConsoleEntry entry && Shown(entry);
        _list.ContextMenu = ListMenu();
        Grid.SetRow(_list, 1);
        root.Children.Add(_list);

        _details = WebUi.Output(70);
        _details.TextWrapping = TextWrapping.Wrap;
        Grid.SetRow(_details, 2);
        root.Children.Add(_details);

        var eval = new TextBox
                   {
                       FontFamily = WebUi.Mono,
                       Margin     = new Thickness(6, 4, 6, 6),
                       ToolTip    = "JavaScript to evaluate in the page; Enter runs it (top-level await works), Up and Down recall what was run"
                   };
        eval.KeyDown += async (_, e) =>
                        {
                            switch (e.Key)
                            {
                                case Key.Enter when eval.Text.Trim().Length > 0:
                                    string code = eval.Text.Trim();
                                    eval.Clear();
                                    if (_history.Count == 0 || _history[^1] != code) _history.Add(code);
                                    _historyIndex = _history.Count;
                                    Add("›", Gray, code);
                                    Add("‹", Gray, await EvaluateAsync(code));
                                    break;
                                case Key.Up when _history.Count > 0:
                                    _historyIndex = Math.Max(0, _historyIndex - 1);
                                    eval.Text     = _history[_historyIndex];
                                    eval.CaretIndex = eval.Text.Length;
                                    e.Handled     = true;
                                    break;
                                case Key.Down when _history.Count > 0:
                                    _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
                                    eval.Text     = _historyIndex >= _history.Count ? string.Empty : _history[_historyIndex];
                                    eval.CaretIndex = eval.Text.Length;
                                    e.Handled     = true;
                                    break;
                            }
                        };
        Grid.SetRow(eval, 3);
        root.Children.Add(eval);

        _entries.CollectionChanged += (_, e) =>
                                      {
                                          if (e.Action == NotifyCollectionChangedAction.Add && _entries.Count > 0 && Shown(_entries[^1]))
                                              _list.Dispatcher.InvokeAsync(() => _list.ScrollIntoView(_entries[^1]));
                                      };
        return root;
    }

    private ContextMenu ListMenu()
    {
        var menu = new ContextMenu();
        void Item(string header, string icon, Action act)
        {
            var item = new MenuItem { Header = header, Icon = LabUi.Icon(icon) };
            item.Click += (_, _) => act();
            menu.Items.Add(item);
        }

        Item("Copy", "TbCopy", CopyShown);
        Item("Copy All Shown", "TbCopy", () => CopyAllShown());
        menu.Items.Add(new Separator());
        Item("Save As...", "TbSave", Save);
        Item("Clear Console", "TbDelete", Clear);
        return menu;
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

        var source = new FrameworkElementFactory(typeof(TextBlock));
        source.SetBinding(TextBlock.TextProperty, new Binding(nameof(ConsoleEntry.Source)));
        source.SetValue(DockPanel.DockProperty,        System.Windows.Controls.Dock.Right);
        source.SetValue(TextBlock.MarginProperty,      new Thickness(8, 0, 0, 0));
        source.SetValue(TextBlock.FontSizeProperty,    10.5);
        source.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        source.SetValue(TextBlock.MaxWidthProperty,    150d);
        source.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");

        var repeats = new FrameworkElementFactory(typeof(TextBlock));
        repeats.SetBinding(TextBlock.TextProperty, new Binding(nameof(ConsoleEntry.Repeats)));
        repeats.SetValue(DockPanel.DockProperty,   System.Windows.Controls.Dock.Right);
        repeats.SetValue(TextBlock.MarginProperty, new Thickness(8, 0, 0, 0));
        repeats.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        repeats.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");

        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(ConsoleEntry.Text)));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetValue(TextBlock.FontFamilyProperty,   WebUi.Mono);

        var panel = new FrameworkElementFactory(typeof(DockPanel));
        panel.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 1, 4, 1));
        panel.AppendChild(level);
        panel.AppendChild(repeats);
        panel.AppendChild(source);
        panel.AppendChild(text);
        return new DataTemplate { VisualTree = panel };
    }

    // ---- what is shown ----------------------------------------------------------------------

    private bool Shown(ConsoleEntry entry)
    {
        if (_hidden.Contains(entry.Level)) return false;

        return _text.IsEmpty || _text.Matches(entry.Text + " " + entry.Source);
    }

    private void Refilter()
    {
        CollectionViewSource.GetDefaultView(_entries).Refresh();
    }

    private void ShowDetails(ConsoleEntry? entry)
    {
        if (_details is null) return;

        _details.Text = entry is null
                            ? string.Empty
                            : string.Join(Environment.NewLine, new[] { entry.Text, entry.Source.Length > 0 ? "at " + entry.Source : string.Empty, entry.Stack }.Where(l => l.Length > 0));
    }

    private IEnumerable<ConsoleEntry> ShownEntries() => _entries.Where(Shown);

    private void CopyShown()
    {
        var chosen = _list?.SelectedItems.OfType<ConsoleEntry>().ToList() ?? [];
        string text = string.Join(Environment.NewLine, (chosen.Count > 0 ? chosen : ShownEntries().ToList()).Select(e => e.AsText()));
        if (text.Length == 0) return;

        LabUi.Copy(text);
        Session.Say($"Copied {(chosen.Count > 0 ? chosen.Count : ShownEntries().Count())} line(s)");
    }

    private void CopyAllShown()
    {
        string text = string.Join(Environment.NewLine, ShownEntries().Select(e => e.AsText()));
        if (text.Length == 0) return;

        LabUi.Copy(text);
        Session.Say($"Copied {ShownEntries().Count()} line(s)");
    }

    private void Save()
    {
        var dialog = new SaveFileDialog { Title = "Save the console", Filter = "Text (*.txt)|*.txt|JSON (*.json)|*.json", FileName = "console.txt" };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var lines = ShownEntries().ToList();
            if (dialog.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                File.WriteAllText(dialog.FileName, JsonConvert.SerializeObject(lines.Select(e => new { time = e.Time, level = e.Level, text = e.Text, source = e.Source, stack = e.Stack, count = e.Count }), Formatting.Indented));
            else
                File.WriteAllText(dialog.FileName, string.Join(Environment.NewLine, lines.Select(e => e.AsText())));
            Session.Say($"Saved {lines.Count} line(s) to {dialog.FileName}");
        }
        catch (Exception ex)
        {
            Session.Say("Could not save the console: " + ex.Message);
        }
    }

    /// <summary>Forgets the lines.</summary>
    public void Clear()
    {
        _entries.Clear();
        ShowDetails(null);
        Session.ClearProblems("JavaScript");
    }

    // ---- the page ---------------------------------------------------------------------------

    protected override void OnAttached()
    {
        Dev!.On("Runtime.consoleAPICalled", e =>
                                            {
                                                string type = (string?)e["type"] ?? "log";
                                                IEnumerable<string> args = (e["args"] as JArray)?.Select(a => Describe(a)) ?? Array.Empty<string>();
                                                var frames = e["stackTrace"]?["callFrames"] as JArray;
                                                AddLevel(type, string.Join(" ", args), Where(frames?.FirstOrDefault()), Stack(frames));
                                            });
        Dev.On("Runtime.exceptionThrown", e =>
                                          {
                                              var details = e["exceptionDetails"];
                                              string text = (string?)details?["exception"]?["description"] ?? (string?)details?["text"] ?? "Uncaught exception";
                                              var frames  = details?["stackTrace"]?["callFrames"] as JArray;
                                              string where = frames?.FirstOrDefault() is { } first ? Where(first) : WhereDetails(details);
                                              Add("ERR", Red, text, where, Stack(frames), report: true);
                                          });
        Dev.On("Log.entryAdded", e =>
                                 {
                                     var    entry = e["entry"];
                                     string url   = (string?)entry?["url"] ?? string.Empty;
                                     string line  = (int?)entry?["lineNumber"] is { } n && n >= 0 ? $":{n + 1}" : string.Empty;
                                     AddLevel((string?)entry?["level"] ?? "info", (string?)entry?["text"] ?? string.Empty, url.Length > 0 ? Short(url) + line : string.Empty, string.Empty);
                                 });

        RunSafe(async () =>
                {
                    await Dev.EnableAsync("Runtime");
                    await Dev.EnableAsync("Log");
                });
    }

    internal override void OnNavigating(Uri uri)
    {
        if (_keepEntries)
        {
            Add("—", Gray, uri.AbsoluteUri);
        }
        else
        {
            _entries.Clear();
            ShowDetails(null);
            Session.ClearProblems("JavaScript");
        }
    }

    private static string Short(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return url.Length <= 60 ? url : url[..60] + "...";

        string file = uri.Segments.Length > 0 ? uri.Segments[^1].Trim('/') : string.Empty;
        return file.Length > 0 ? file : uri.Host;
    }

    private static string Where(JToken? frame)
    {
        if (frame is null) return string.Empty;

        string url = (string?)frame["url"] ?? string.Empty;
        if (url.Length == 0) return string.Empty;

        return $"{Short(url)}:{((int?)frame["lineNumber"] ?? 0) + 1}:{((int?)frame["columnNumber"] ?? 0) + 1}";
    }

    private static string WhereDetails(JToken? details)
    {
        string url = (string?)details?["url"] ?? string.Empty;
        return url.Length == 0 ? string.Empty : $"{Short(url)}:{((int?)details?["lineNumber"] ?? 0) + 1}:{((int?)details?["columnNumber"] ?? 0) + 1}";
    }

    private static string Stack(JArray? frames)
    {
        if (frames is null || frames.Count == 0) return string.Empty;

        var text = new StringBuilder();
        foreach (var frame in frames.Take(30))
        {
            string name = (string?)frame["functionName"] is { Length: > 0 } fn ? fn : "(anonymous)";
            text.Append("  at ").Append(name).Append(" (").Append((string?)frame["url"]).Append(':').Append(((int?)frame["lineNumber"] ?? 0) + 1).Append(':').Append(((int?)frame["columnNumber"] ?? 0) + 1).AppendLine(")");
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>Evaluates JavaScript in the page and returns the result as text. Never throws: a problem is the text.</summary>
    public async Task<string> EvaluateAsync(string expression)
    {
        if (Dev is null) return "The page is not up yet.";

        try
        {
            var response = await Dev.CallAsync("Runtime.evaluate", new { expression, awaitPromise = true, replMode = true, generatePreview = true });
            if (response["exceptionDetails"] is JObject failure)
                return "! " + ((string?)failure["exception"]?["description"] ?? (string?)failure["text"] ?? "exception");

            var result = response["result"];
            if (result is JObject { } obj && (string?)obj["type"] == "object" && (string?)obj["subtype"] != "null" && (string?)obj["objectId"] is { } objectId)
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

    private void AddLevel(string type, string text, string source, string stack)
    {
        switch (type.ToLowerInvariant())
        {
            case "error":
            case "assert":
                Add("ERR", Red, text, source, stack, report: true);
                break;
            case "warning":
            case "warn":
                Add("WRN", Orange, text, source, stack);
                break;
            case "info":
                Add("INF", Blue, text, source, stack);
                break;
            case "debug":
            case "verbose":
                Add("DBG", Gray, text, source, stack);
                break;
            default:
                Add("LOG", Gray, text, source, stack);
                break;
        }
    }

    private void Add(string level, Brush brush, string text, string source = "", string stack = "", bool report = false)
    {
        // the same line again and again is one line that says how often
        if (_entries.Count > 0 && _entries[^1] is { } last && last.Level == level && last.Text == text && last.Source == source)
        {
            last.Count++;
        }
        else
        {
            _entries.Add(new ConsoleEntry(level, brush, text, source, stack));
            while (_entries.Count > Limit) _entries.RemoveAt(0);
        }

        if (report) Session.ReportProblem(LabProblemSeverity.Error, "JavaScript", FirstLine(text), source.Length > 0 ? source : null, () => Session.ShowPane("console"));
    }

    private static string FirstLine(string text)
    {
        int end = text.IndexOfAny(['\r', '\n']);
        string line = end < 0 ? text : text[..end];
        return line.Length <= 300 ? line : line[..300] + "...";
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

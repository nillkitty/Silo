using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Serilog;

namespace Silo.Ui;

/// <summary>
///     The main window's terminal as the Lab windows have it: a <c>›</c> prompt and one input line in the status bar, the
///     log of what was typed and answered in a popup above it. Up and Down recall, Tab completes the first word (again
///     cycles), Escape clears and leaves. A one-line answer goes to the status line (<see cref="AppStatus" />), a longer one
///     opens the log as well.
/// </summary>
public sealed class TerminalLine : UserControl
{
    private const int LogLimit = 500;

    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");

    private readonly List<string>  _history = [];
    private readonly Queue<string> _log     = new();

    private readonly TextBox _input = new()
                                      {
                                          BorderThickness = new Thickness(0), Background = Brushes.Transparent, VerticalContentAlignment = VerticalAlignment.Center,
                                          FontFamily      = Mono, MinWidth = 120
                                      };

    private readonly TextBox _logText = new()
                                        {
                                            IsReadOnly                    = true, FontFamily = Mono, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                                            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap,
                                            BorderThickness               = new Thickness(0), Padding = new Thickness(6)
                                        };

    private readonly Popup     _logPopup = new() { StaysOpen = false, Placement = PlacementMode.Top, AllowsTransparency = false };
    private readonly TextBlock _prompt   = new() { Text = "›", FontSize = 16, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };

    private MainTerminal? _terminal;
    private int           _historyIndex;
    private string?       _completionBase;
    private string?       _lastCompletion;
    private int           _completionIndex;

    public TerminalLine()
    {
        var dock = new DockPanel { LastChildFill = true, Margin = new Thickness(8, 0, 8, 0) };
        _prompt.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        DockPanel.SetDock(_prompt, Dock.Left);
        dock.Children.Add(_prompt);
        dock.Children.Add(_input);
        Content = dock;

        _input.ToolTip = "Terminal: help lists the commands; every menu and toolbar command is a command too. Up and Down recall, Tab completes (Ctrl+`)";
        _input.SetResourceReference(ForegroundProperty, "Theme.Text");
        _input.SetResourceReference(TextBox.CaretBrushProperty, "Theme.Text");
        _input.PreviewKeyDown += OnKey;

        var border = new Border { Width = 760, Height = 280, BorderThickness = new Thickness(1), Child = _logText };
        border.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
        _logText.SetResourceReference(BackgroundProperty, "Theme.Background");
        _logText.SetResourceReference(ForegroundProperty, "Theme.Text");
        _logPopup.Child           = border;
        _logPopup.PlacementTarget = this;
    }

    /// <summary>What runs the lines. Set once by the main window.</summary>
    internal MainTerminal? Terminal
    {
        get => _terminal;
        set
        {
            if (_terminal is not null) _terminal.RecordingChanged -= OnRecordingChanged;
            _terminal = value;
            if (_terminal is not null) _terminal.RecordingChanged += OnRecordingChanged;
            OnRecordingChanged(null, EventArgs.Empty);
        }
    }

    /// <summary>Puts the keyboard focus in the input line (Ctrl+`).</summary>
    public void FocusInput()
    {
        _input.Focus();
        Keyboard.Focus(_input);
    }

    public void ShowLog()
    {
        _logPopup.IsOpen = true;
        _logText.ScrollToEnd();
    }

    public void ClearLog()
    {
        _log.Clear();
        _logText.Clear();
    }

    /// <summary>While a macro is recorded the prompt turns red and says so.</summary>
    private void OnRecordingChanged(object? sender, EventArgs e)
    {
        if (_terminal?.RecordingName is { } name)
        {
            _prompt.Text       = "● ›";
            _prompt.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));
            _prompt.ToolTip    = $"Recording macro '{name}' ({_terminal.RecordedCount} step(s)); macro stop keeps it";
        }
        else
        {
            _prompt.Text    = "›";
            _prompt.ToolTip = null;
            _prompt.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        }
    }

    private async void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                string line = _input.Text.Trim();
                _input.Clear();
                _completionBase = null;
                _lastCompletion = null;
                if (line.Length > 0)
                {
                    if (_history.Count == 0 || _history[^1] != line) _history.Add(line);
                    _historyIndex = _history.Count;
                    await RunAsync(line);
                }

                break;

            case Key.Up when _history.Count > 0:
                e.Handled         = true;
                _historyIndex     = Math.Max(0, _historyIndex - 1);
                _input.Text       = _history[_historyIndex];
                _input.CaretIndex = _input.Text.Length;
                break;

            case Key.Down when _history.Count > 0:
                e.Handled         = true;
                _historyIndex     = Math.Min(_history.Count, _historyIndex + 1);
                _input.Text       = _historyIndex >= _history.Count ? string.Empty : _history[_historyIndex];
                _input.CaretIndex = _input.Text.Length;
                break;

            case Key.Tab:
                e.Handled = true;
                Complete();
                break;

            case Key.Escape:
                e.Handled = true;
                _input.Clear();
                Keyboard.ClearFocus();
                break;
        }
    }

    private void Complete()
    {
        // only the first word completes; repeating Tab cycles through the matches
        string text = _input.Text;
        if (text.Contains(' ') || _terminal is null) return;

        if (_lastCompletion is null || text != _lastCompletion)
        {
            _completionBase  = text;
            _completionIndex = -1;
        }

        var matches = _terminal.Completions(_completionBase ?? string.Empty);
        if (matches.Count == 0) return;

        _completionIndex  = (_completionIndex + 1) % matches.Count;
        _lastCompletion   = matches[_completionIndex];
        _input.Text       = _lastCompletion;
        _input.CaretIndex = _input.Text.Length;
    }

    private async Task RunAsync(string line)
    {
        if (_terminal is null) return;

        Print("> " + line);
        try
        {
            string? result = await _terminal.ExecuteAsync(line);
            if (!string.IsNullOrEmpty(result))
            {
                Print(result);
                AppStatus.Instance.Report(result.Split('\n')[0].TrimEnd('\r'));
                if (result.Contains('\n')) ShowLog();
            }
            else
            {
                AppStatus.Instance.Report("ok");
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Terminal line {Line} failed", line);
            Print("error: " + ex.Message);
            AppStatus.Instance.Report(ex.Message, true);
        }
    }

    private void Print(string text)
    {
        foreach (string line in text.Replace("\r", string.Empty).Split('\n'))
        {
            _log.Enqueue(line);
            while (_log.Count > LogLimit) _log.Dequeue();
        }

        _logText.Text = string.Join(Environment.NewLine, _log);
        _logText.ScrollToEnd();
    }
}

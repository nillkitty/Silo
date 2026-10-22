using System.IO;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;

namespace Silo.Labs.Streams;

/// <summary>
///     The main view of the Stream Lab: where the reel comes from, the controls to play it (step, read, wind, play), the strip of what was read shown as a hex
///     dump with its marks, and what to write.
/// </summary>
internal sealed class StreamView : Grid
{
    private static readonly string[] Steps = ["1", "2", "4", "8", "16", "32", "64", "256", "1024", "4096"];

    private readonly StreamLabSession _session;
    private readonly Debouncer        _draw;
    private bool                      _loading;

    // the source
    private readonly ComboBox   _kind;
    private readonly DockPanel  _filePanel;
    private readonly StackPanel _memoryPanel;
    private readonly StackPanel _netPanel;
    private readonly TextBox    _path;
    private readonly ComboBox   _mode;
    private readonly ComboBox   _access;
    private readonly ComboBox   _share;
    private readonly TextBlock  _memoryInfo;
    private readonly TextBox    _host;
    private readonly TextBox    _port;
    private readonly TextBox    _serverName;
    private readonly CheckBox   _trust;
    private readonly StackPanel _tlsOnly;
    private readonly Button     _open;
    private readonly Button     _close;
    private readonly TextBlock  _stateText;

    // playing
    private readonly Button    _rewind;
    private readonly Button    _back;
    private readonly Button    _step;
    private readonly Button    _read;
    private readonly Button    _exactly;
    private readonly Button    _all;
    private readonly Button    _play;
    private readonly Button    _stop;
    private readonly ComboBox  _bytes;
    private readonly TextBox   _position;
    private readonly Button    _go;
    private readonly TextBlock _where;
    private readonly TextBlock _caps;
    private readonly TextBlock _problem;

    // the strip
    private readonly ComboBox   _side;
    private readonly TextBox    _in;
    private readonly TextBox    _out;
    private readonly CheckBox   _follow;
    private readonly ListBox    _rows;
    private readonly TextBlock  _stripInfo;
    private int                 _firstRow;

    // writing
    private readonly ComboBox _format;
    private readonly TextBox  _text;
    private readonly ComboBox _pattern;
    private readonly TextBox  _count;
    private readonly Button   _write;
    private readonly Button   _patternWrite;
    private readonly Button   _shutdown;

    public StreamView(StreamLabSession session)
    {
        _session = session;
        _draw    = new Debouncer(TimeSpan.FromMilliseconds(30), DrawRows);

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.6, GridUnitType.Star), MinHeight = 90 });

        // ---- the source -------------------------------------------------------------------------
        _kind = LabUi.Combo(["File", "Memory", "TCP", "TLS"], "Memory", KindChosen, 80);

        _path = LabUi.Field(string.Empty, v => Edit(s => s.Source.Path = v.Trim().Trim('"')), 200, "The file to open", true);
        _mode = LabUi.Combo(Enum.GetNames<FileMode>().Where(n => n != nameof(FileMode.CreateNew)), nameof(FileMode.Open), v => Edit(s => s.Source.Mode = Enum.Parse<FileMode>(v)), 90);
        _mode.ToolTip = "How the file is opened: Open (it has to be there), OpenOrCreate, Create (emptied if it is there), Append (written at the end), Truncate (emptied)";
        _access = LabUi.Combo(Enum.GetNames<FileAccess>(), nameof(FileAccess.Read), v => Edit(s => s.Source.Access = Enum.Parse<FileAccess>(v)), 80);
        _access.ToolTip = "What can be done with the stream: Read, Write or ReadWrite";
        _share = LabUi.Combo(Enum.GetNames<FileShare>().Where(n => n is "None" or "Read" or "ReadWrite"), nameof(FileShare.Read), v => Edit(s => s.Source.Share = Enum.Parse<FileShare>(v)), 80);
        _share.ToolTip = "What others may do with the file while it is open";
        var browse = LabUi.Button("Browse...", Browse, "Choose a file");
        _filePanel = new DockPanel();
        foreach (var (element, dock) in new (UIElement, System.Windows.Controls.Dock)[] { (browse, System.Windows.Controls.Dock.Right), (_share, System.Windows.Controls.Dock.Right), (_access, System.Windows.Controls.Dock.Right), (_mode, System.Windows.Controls.Dock.Right) })
        {
            DockPanel.SetDock(element, dock);
            _filePanel.Children.Add(element);
        }

        _filePanel.Children.Add(_path);

        _memoryInfo = LabUi.Label(string.Empty);
        _memoryPanel = new StackPanel { Orientation = Orientation.Horizontal };
        _memoryPanel.Children.Add(_memoryInfo);
        _memoryPanel.Children.Add(LabUi.Button("Set...", SetMemory, "Type what the stream starts with: text, hex:48 65 6C or b64:SGVsbG8="));
        _memoryPanel.Children.Add(LabUi.Button("From file...", LoadMemory, "Start with the contents of a file"));
        _memoryPanel.Children.Add(LabUi.Button("Empty", () => EditSource(s => s.Data = []), "Start with nothing"));

        _host = LabUi.Field(string.Empty, v => Edit(s => s.Source.Host = v.Trim()), 180, "The host name or address", true);
        _port = LabUi.Field(string.Empty, v => Edit(s => s.Source.Port = int.TryParse(v, out int p) ? p : 0), 60, "The port", true);
        _serverName = LabUi.Field(string.Empty, v => Edit(s => s.Source.ServerName = string.IsNullOrWhiteSpace(v) ? null : v.Trim()), 140, "The name the certificate has to be for (the host when empty)");
        _trust = LabUi.Check("Trust any certificate", false, on => Edit(s => s.Source.TrustAnyCertificate = on), "Accept a certificate that does not check out (a server with a certificate of its own making). The Properties still show what was wrong with it.");
        _tlsOnly = new StackPanel { Orientation = Orientation.Horizontal };
        _tlsOnly.Children.Add(LabUi.Label("Server name", muted: true).Also(l => l.Margin = new Thickness(8, 0, 2, 0)));
        _tlsOnly.Children.Add(_serverName);
        _tlsOnly.Children.Add(_trust);
        _netPanel = new StackPanel { Orientation = Orientation.Horizontal };
        _netPanel.Children.Add(_host);
        _netPanel.Children.Add(LabUi.Label(":"));
        _netPanel.Children.Add(_port);
        _netPanel.Children.Add(_tlsOnly);

        _open  = LabUi.Button("Open", () => _ = _session.OpenReelAsync(), "Open the stream (again, from the start)");
        _close = LabUi.Button("Close", _session.CloseReel, "Close the stream");
        _stateText = LabUi.Label(string.Empty, muted: true);
        _stateText.Margin = new Thickness(6, 0, 6, 0);

        var sourceRow = new DockPanel { Margin = new Thickness(6, 6, 6, 2) };
        var buttons   = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_open);
        buttons.Children.Add(_close);
        buttons.Children.Add(_stateText);
        DockPanel.SetDock(buttons, System.Windows.Controls.Dock.Right);
        var kindLabel = LabUi.Label("Reel", muted: true).Also(l => l.Margin = new Thickness(0, 0, 6, 0));
        DockPanel.SetDock(kindLabel, System.Windows.Controls.Dock.Left);
        DockPanel.SetDock(_kind, System.Windows.Controls.Dock.Left);
        var sourceHost = new Grid { Margin = new Thickness(6, 0, 6, 0) };
        sourceHost.Children.Add(_filePanel);
        sourceHost.Children.Add(_memoryPanel);
        sourceHost.Children.Add(_netPanel);
        sourceRow.Children.Add(buttons);
        sourceRow.Children.Add(kindLabel);
        sourceRow.Children.Add(_kind);
        sourceRow.Children.Add(sourceHost);
        SetRow(sourceRow, 0);
        Children.Add(sourceRow);

        // ---- playing ----------------------------------------------------------------------------
        _rewind  = LabUi.Button("|<", () => _session.Rewind(), "Wind back to the start of the stream (it has to be able to seek)");
        _back    = LabUi.Button("< Back", () => _session.StepBack(Step), "Move the position back by the number of bytes in the box");
        _step    = LabUi.Button("Step 1", () => _ = _session.StepAsync(), "Read one byte (F10)");
        _read    = LabUi.Button("Read", () => _ = _session.ReadAsync(Step, ReadMode.Once), "Read up to that many bytes with one call: what a program usually does, and what comes may be less (F5)");
        _exactly = LabUi.Button("Read exactly", () => _ = _session.ReadAsync(Step, ReadMode.Exactly), "Keep reading until that many bytes have come, or the data ends");
        _all     = LabUi.Button("All", () => _ = _session.ReadAllAsync(), "Read to the end of the data");
        _play    = LabUi.Button("Play", () => _ = _session.PlayAsync(), "Read a step after step, a moment apart (F9)");
        _stop    = LabUi.Button("Stop", _session.Stop, "Stop what is going on, such as a read that waits for a connection to say something (Escape)");
        _bytes   = new ComboBox { IsEditable = true, Width = 74, Margin = new Thickness(2, 0, 2, 0), ToolTip = "How many bytes a step reads and a back moves" };
        foreach (string s in Steps) _bytes.Items.Add(s);
        _bytes.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) =>
                                                                                                                       {
                                                                                                                           if (!_loading && int.TryParse(_bytes.Text, out int n) && n > 0) _session.EditSetup(s => s.Step = Math.Min(n, 1 << 24));
                                                                                                                       }));
        _position = new TextBox { Width = 90, Margin = new Thickness(2, 0, 2, 0), FontFamily = LabUi.Mono, ToolTip = "Where to move the position: 1234 or 0x4D2 (the stream has to be able to seek)", VerticalContentAlignment = VerticalAlignment.Center };
        _position.KeyDown += (_, e) =>
                             {
                                 if (e.Key == System.Windows.Input.Key.Enter) GoToPosition();
                             };
        _go = LabUi.Button("Go", GoToPosition, "Move the position there");

        var transport = new WrapPanel { Margin = new Thickness(6, 2, 6, 2) };
        foreach (UIElement e in new UIElement[] { _rewind, _back, _step, _read, _exactly, _all, _play, _stop })
            transport.Children.Add(e);
        transport.Children.Add(LabUi.Label("Bytes", muted: true).Also(l => l.Margin = new Thickness(10, 0, 2, 0)));
        transport.Children.Add(_bytes);
        transport.Children.Add(LabUi.Label("Position", muted: true).Also(l => l.Margin = new Thickness(10, 0, 2, 0)));
        transport.Children.Add(_position);
        transport.Children.Add(_go);
        // the deck takes the place of the plain row of buttons (which is still built, so that what the view does for them keeps working, but is not shown)
        var deck = new StreamDeck(_session, WriteTyped);
        var deckBox = new Expander { Header = "Deck", IsExpanded = true, Content = deck, Margin = new Thickness(6, 0, 6, 0), ToolTip = "The stream, what is on it and the buttons to run it" };
        deckBox.SetResourceReference(Control.ForegroundProperty, "Theme.Text");
        SetRow(deckBox, 1);
        Children.Add(deckBox);

        _where = LabUi.Label(string.Empty);
        _caps  = LabUi.Label(string.Empty, muted: true);
        _problem = LabUi.Label(string.Empty, wrap: true);
        var info = new StackPanel { Margin = new Thickness(8, 2, 8, 4) };
        var infoRow = new WrapPanel();
        infoRow.Children.Add(_where);
        infoRow.Children.Add(_caps.Also(c => c.Margin = new Thickness(12, 0, 0, 0)));
        info.Children.Add(infoRow);
        info.Children.Add(_problem);
        SetRow(info, 2);
        Children.Add(info);

        // ---- the strip --------------------------------------------------------------------------
        _side = LabUi.Combo(["Output", "Input", "Written", "Processed"], "Output", v => _session.ShowStrip(Enum.Parse<StripSide>(v)), 80);
        _side.ToolTip = "Output: what came out of the adapters. Input: what was read from the stream. Written: what reached the stream when something was written. Processed: the output after the post-processing adapters.";
        _in  = LabUi.Field(string.Empty, v => SetMark(v, true), 80, "Where the cut begins, an offset in the strip (123 or 0x7B)", true);
        _out = LabUi.Field(string.Empty, v => SetMark(v, false), 80, "Where the cut ends, an offset in the strip (not included)", true);
        _follow = LabUi.Check("Follow", true, _ => DrawRows(), "Keep the end of the strip in view");
        _stripInfo = LabUi.Label(string.Empty, muted: true);
        var stripBar = new WrapPanel { Margin = new Thickness(6, 2, 6, 2) };
        stripBar.Children.Add(LabUi.Label("Strip", bold: true).Also(l => l.Margin = new Thickness(0, 0, 6, 0)));
        stripBar.Children.Add(_side);
        stripBar.Children.Add(LabUi.Label("In", muted: true).Also(l => l.Margin = new Thickness(10, 0, 2, 0)));
        stripBar.Children.Add(_in);
        stripBar.Children.Add(LabUi.Button("= end", () => _session.SetMarks(_session.Strip.Length, _session.MarkOut), "Mark In at the end of the strip, where the playhead is"));
        stripBar.Children.Add(LabUi.Label("Out", muted: true).Also(l => l.Margin = new Thickness(6, 0, 2, 0)));
        stripBar.Children.Add(_out);
        stripBar.Children.Add(LabUi.Button("= end", () => _session.SetMarks(_session.MarkIn, _session.Strip.Length), "Mark Out at the end of the strip, where the playhead is"));
        stripBar.Children.Add(LabUi.Button("Cut", () => _session.Cut(), "Take the stretch between In and Out as a cut (Ctrl+Shift+X); the Cuts list can join the cuts"));
        stripBar.Children.Add(LabUi.Button("Clear marks", _session.ClearMarks, "Forget In and Out"));
        stripBar.Children.Add(_follow.Also(f => f.Margin = new Thickness(10, 0, 4, 0)));
        stripBar.Children.Add(LabUi.Button("<<", () => Page(-1), "Show earlier rows"));
        stripBar.Children.Add(LabUi.Button(">>", () => Page(1), "Show later rows"));

        _rows = new ListBox { SelectionMode = SelectionMode.Extended, BorderThickness = new Thickness(0), FontFamily = LabUi.Mono }.Themed();
        _rows.ContextMenu = BuildRowMenu();
        var stripBox = LabUi.Layout(new StackPanel().Also(p =>
                                                          {
                                                              p.Children.Add(stripBar);
                                                              p.Children.Add(_stripInfo.Also(l => l.Margin = new Thickness(8, 0, 8, 2)));
                                                          }), _rows);
        SetRow(stripBox, 3);
        Children.Add(stripBox);

        var splitter = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch };
        splitter.SetResourceReference(BackgroundProperty, "Theme.Border");
        SetRow(splitter, 4);
        Children.Add(splitter);

        // ---- writing ----------------------------------------------------------------------------
        _format = LabUi.Combo(["Text", "Hex", "Base64"], "Text", v => Edit(s => s.WriteFormat = Enum.Parse<StreamPayload.Format>(v)), 70);
        _format.ToolTip = "How what is typed stands for bytes. Text reads \\n \\r \\t \\0 \\\\ \\xHH \\uHHHH and is written as UTF-8.";
        _write = LabUi.Button("Write", WriteTyped, "Write the typed bytes at the stream's position, through the adapters that apply to writes");
        _shutdown = LabUi.Button("Stop sending", _session.ShutdownSend, "Tell the other side that nothing more is coming (a connection only); what it sends can still be read");
        var writeBar = new WrapPanel { Margin = new Thickness(6, 2, 6, 2) };
        writeBar.Children.Add(LabUi.Label("Write", bold: true).Also(l => l.Margin = new Thickness(0, 0, 6, 0)));
        writeBar.Children.Add(_format);
        writeBar.Children.Add(_write);
        writeBar.Children.Add(_shutdown);

        _text = LabUi.Memo(string.Empty, v =>
                                         {
                                             if (!_loading) _session.EditSetup(s => s.WriteText = v);
                                         }, wrap: true);
        _pattern = LabUi.Combo(StreamPayload.Patterns, StreamPayload.Patterns[0], _ => { }, 110);
        _count = LabUi.Line("16", _ => { }, 60, "How many bytes", true);
        _patternWrite = LabUi.Button("Write bytes", WritePattern, "Write that many made-up bytes of the chosen kind");
        var patternBar = new WrapPanel { Margin = new Thickness(6, 2, 6, 2) };
        patternBar.Children.Add(_count);
        patternBar.Children.Add(LabUi.Label("of", muted: true).Also(l => l.Margin = new Thickness(4, 0, 4, 0)));
        patternBar.Children.Add(_pattern);
        patternBar.Children.Add(_patternWrite);
        var writeBox = LabUi.Layout(writeBar, _text, patternBar);
        SetRow(writeBox, 5);
        Children.Add(writeBox);

        this.SetResourceReference(BackgroundProperty, "Theme.Background");

        _session.Changed += (_, _) => OnChanged();
        _session.Loaded  += (_, _) => Load();
        Load();
    }

    private int Step => _session.Setup.Step;

    private void Edit(Action<StreamSetup> change)
    {
        if (!_loading) _session.EditSetup(change);
    }

    private void EditSource(Action<StreamSource> change)
    {
        if (_loading) return;

        _session.EditSetup(s => change(s.Source));
        Load();
    }

    private void KindChosen(string name)
    {
        if (_loading) return;

        var kind = name switch { "File" => StreamKind.File, "TCP" => StreamKind.Tcp, "TLS" => StreamKind.Tls, _ => StreamKind.Memory };
        _session.EditSetup(s =>
                           {
                               s.Source.Kind = kind;
                               if (kind is StreamKind.Tcp or StreamKind.Tls && s.Source.Port == 0) s.Source.Port = kind == StreamKind.Tls ? 443 : 80;
                           });
        Load();
    }

    private void Browse()
    {
        var dialog = new OpenFileDialog { Title = "Open a file as a reel", CheckFileExists = _session.Setup.Source.Mode is FileMode.Open or FileMode.Truncate };
        if (dialog.ShowDialog() != true) return;

        EditSource(s => s.Path = dialog.FileName);
    }

    private void SetMemory()
    {
        string? text = LabUi.Ask("Reel in memory", "What it starts with: text, hex:48 65 6C or b64:SGVsbG8=", string.Empty);
        if (text is null) return;

        try
        {
            EditSource(s => s.Data = StreamPayload.FromTyped(text));
        }
        catch (FormatException ex)
        {
            _session.Say(ex.Message);
        }
    }

    private void LoadMemory()
    {
        var dialog = new OpenFileDialog { Title = "Start with the contents of a file" };
        if (dialog.ShowDialog() != true) return;

        try
        {
            if (new FileInfo(dialog.FileName).Length > 64 << 20)
            {
                _session.Say("That file is too big to hold in memory; open it as a file reel instead.");
                return;
            }

            EditSource(s => s.Data = File.ReadAllBytes(dialog.FileName));
        }
        catch (IOException ex)
        {
            _session.Say(ex.Message);
        }
    }

    private void GoToPosition()
    {
        if (StreamLabSession.TryNumber(_position.Text, out long to)) _session.SeekTo(to);
        else _session.Say("Type the position as a number: 1234 or 0x4D2.");
    }

    private void SetMark(string text, bool isIn)
    {
        if (_loading) return;

        long? value = StreamLabSession.TryNumber(text, out long n) ? n : null;
        if (isIn) _session.SetMarks(value, _session.MarkOut);
        else _session.SetMarks(_session.MarkIn, value);
    }

    private void WriteTyped()
    {
        try
        {
            var bytes = StreamPayload.ToBytes(_text.Text, _session.Setup.WriteFormat);
            _ = _session.WriteAsync(bytes, "typed " + _session.Setup.WriteFormat.ToString().ToLowerInvariant());
        }
        catch (FormatException ex)
        {
            _session.Say(ex.Message);
        }
    }

    private void WritePattern()
    {
        if (!int.TryParse(_count.Text.Replace(",", string.Empty), out int n) || n < 0 || n > 64 << 20)
        {
            _session.Say("How many bytes? A number up to 67,108,864.");
            return;
        }

        string kind = _pattern.SelectedItem as string ?? StreamPayload.Patterns[0];
        _ = _session.WriteAsync(StreamPayload.Pattern(kind, n), $"{n:N0} byte(s) of {kind}");
    }

    // ---- showing the state ---------------------------------------------------------------------

    /// <summary>Shows the setup of the session in the boxes (a different reel was opened).</summary>
    private void Load()
    {
        _loading = true;
        try
        {
            var setup = _session.Setup;
            var s     = setup.Source;
            _kind.SelectedItem = s.Kind switch { StreamKind.File => "File", StreamKind.Tcp => "TCP", StreamKind.Tls => "TLS", _ => "Memory" };
            _kind.IsEnabled    = s.Kind != StreamKind.Existing;
            Set(_path, s.Path);
            _mode.SelectedItem   = s.Mode.ToString();
            _access.SelectedItem = s.Access.ToString();
            _share.SelectedItem  = s.Share.ToString();
            Set(_host, s.Host);
            Set(_port, s.Port == 0 ? string.Empty : s.Port.ToString());
            Set(_serverName, s.ServerName ?? string.Empty);
            _trust.IsChecked = s.TrustAnyCertificate;
            _bytes.Text      = setup.Step.ToString();
            _format.SelectedItem = setup.WriteFormat.ToString();
            Set(_text, setup.WriteText);
        }
        finally
        {
            _loading = false;
        }

        OnChanged();
    }

    private static void Set(TextBox box, string text)
    {
        if (box.Text != text) box.Text = text;
    }

    private void OnChanged()
    {
        var s = _session.Setup.Source;
        _filePanel.Visibility   = s.Kind == StreamKind.File ? Visibility.Visible : Visibility.Collapsed;
        _memoryPanel.Visibility = s.Kind is StreamKind.Memory or StreamKind.Existing ? Visibility.Visible : Visibility.Collapsed;
        _netPanel.Visibility    = s.Kind is StreamKind.Tcp or StreamKind.Tls ? Visibility.Visible : Visibility.Collapsed;
        _tlsOnly.Visibility     = s.Kind == StreamKind.Tls ? Visibility.Visible : Visibility.Collapsed;
        _memoryInfo.Text        = s.Kind == StreamKind.Existing ? $"A stream that is open already: {s.Label}   " : $"Starts with {s.Data.Length:N0} byte(s)   ";
        foreach (var b in _memoryPanel.Children.OfType<Button>()) b.IsEnabled = s.Kind == StreamKind.Memory;

        bool open = _session.IsOpen;
        bool idle = !_session.IsBusy;
        _open.Content   = open ? "Reopen" : "Open";
        _open.IsEnabled = idle;
        _close.IsEnabled = open || _session.State == ReelState.Failed;
        _stateText.Text = _session.State switch
                          {
                              ReelState.Open    => _session.IsBusy ? "open - busy" : "open",
                              ReelState.Opening => "opening...",
                              ReelState.Failed  => "could not open",
                              _                 => "closed"
                          };
        _stateText.Foreground = _session.State == ReelState.Failed ? LabUi.Red : open ? LabUi.Green : LabUi.Gray;

        _rewind.IsEnabled = _session.CanSeek && idle;
        _back.IsEnabled   = _session.CanSeek && idle;
        _go.IsEnabled     = _session.CanSeek && idle;
        _position.IsEnabled = _session.CanSeek;
        _step.IsEnabled   = _session.CanRead && idle;
        _read.IsEnabled   = _step.IsEnabled;
        _exactly.IsEnabled = _step.IsEnabled;
        _all.IsEnabled    = _step.IsEnabled;
        _play.IsEnabled   = _session.CanRead && (idle || _session.IsPlaying);
        _play.Content     = _session.IsPlaying ? "Pause" : "Play";
        _stop.IsEnabled   = !idle || _session.IsPlaying;
        _write.IsEnabled  = _session.CanWrite && idle;
        _patternWrite.IsEnabled = _write.IsEnabled;
        _shutdown.IsEnabled = _session.Reel?.Socket is { Connected: true } && idle;
        _shutdown.Visibility = s.Kind is StreamKind.Tcp or StreamKind.Tls ? Visibility.Visible : Visibility.Collapsed;
        if (!_position.IsKeyboardFocusWithin) _position.Text = _session.Position?.ToString() ?? string.Empty;

        _where.Text = _session.PositionText;
        _caps.Inlines.Clear();
        if (open)
        {
            var stream = _session.Reel!.Stream;
            _caps.Inlines.Add("can: ");
            foreach (var (name, on) in new[] { ("read", stream.CanRead), ("write", stream.CanWrite), ("seek", stream.CanSeek), ("time out", stream.CanTimeout) })
                _caps.Inlines.Add(new Run(name + "  ") { Foreground = on ? LabUi.Green : LabUi.Gray, TextDecorations = on ? null : TextDecorations.Strikethrough });
        }

        string? problem = _session.OpenError ?? _session.AdapterProblem ?? _session.Setup.Source.Problem();
        _problem.Text       = problem ?? string.Empty;
        _problem.Foreground = _session.OpenError is not null ? LabUi.Red : LabUi.Amber;
        _problem.Visibility = problem is null || (_session.OpenError is null && _session.State == ReelState.Open && _session.AdapterProblem is null) ? Visibility.Collapsed : Visibility.Visible;

        _side.SelectedItem = _session.ViewSide.ToString();
        if (!_in.IsKeyboardFocusWithin) _in.Text = _session.MarkIn?.ToString() ?? string.Empty;
        if (!_out.IsKeyboardFocusWithin) _out.Text = _session.MarkOut?.ToString() ?? string.Empty;
        _draw.Poke();
    }

    // ---- the strip as a hex dump ---------------------------------------------------------------

    private void Page(int direction)
    {
        _follow.IsChecked = false;
        int rows = Math.Max(1, _session.ViewRows / 2);
        _firstRow = Math.Max(0, _firstRow + direction * rows);
        DrawRows();
    }

    private void DrawRows()
    {
        var strip   = _session.Strip;
        int total   = (strip.Length + HexDump.Width - 1) / HexDump.Width;
        int window  = _session.ViewRows;
        int maxFirst = Math.Max(0, total - window);
        if (_follow.IsChecked == true) _firstRow = maxFirst;
        _firstRow = Math.Clamp(_firstRow, 0, maxFirst);

        _rows.Items.Clear();
        if (strip.Length == 0)
        {
            _rows.Items.Add(new ListBoxItem { IsEnabled = false, Content = LabUi.Label($"The {_session.ViewSide} strip is empty. " + (_session.ViewSide == StripSide.Written ? "Write something to the stream." : "Read from the reel: Step reads a byte."), muted: true) });
            _stripInfo.Text = string.Empty;
            return;
        }

        var (lastStart, lastLength) = strip.Last;
        long markFrom = -1, markTo = -1;
        if (_session.HasMarks)
        {
            (markFrom, markTo) = _session.MarkedRange();
        }

        var data = strip.Span;
        int end  = Math.Min(total, _firstRow + window);
        for (int row = _firstRow; row < end; row++)
        {
            int at = row * HexDump.Width;
            _rows.Items.Add(new ListBoxItem { Tag = (long)at, Content = Row(data, at, lastStart, lastLength, markFrom, markTo) });
        }

        _stripInfo.Text = $"{strip.Length:N0} byte(s){(strip.Overflowed ? " (the strip is full: the rest was not kept)" : string.Empty)}; rows {_firstRow + 1:N0} to {end:N0} of {total:N0}" +
                          (lastLength > 0 ? $"; the last read was at {lastStart:N0}, {lastLength:N0} byte(s) (blue)" : string.Empty) +
                          (_session.HasMarks ? $"; cut {markFrom:N0}..{markTo:N0} (amber)" : string.Empty);
        if (_follow.IsChecked == true && _rows.Items.Count > 0) _rows.ScrollIntoView(_rows.Items[^1]);
    }

    private static TextBlock Row(ReadOnlySpan<byte> data, int at, int lastStart, int lastLength, long markFrom, long markTo)
    {
        var block = new TextBlock { FontFamily = LabUi.Mono };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text");
        block.Inlines.Add(new Run(HexDump.Offset(at) + "  ") { Foreground = LabUi.Gray });

        int length = Math.Min(HexDump.Width, data.Length - at);
        int State(int i)
        {
            int abs = at + i;
            int s   = 0;
            if (abs >= lastStart && abs < lastStart + lastLength) s |= 1;
            if (abs >= markFrom && abs < markTo) s |= 2;
            return s;
        }

        // the hex, in runs of bytes that are in the same state
        var text   = new StringBuilder();
        int state  = State(0);
        for (int i = 0; i < HexDump.Width; i++)
        {
            int s = i < length ? State(i) : 0;
            if (s != state)
            {
                Flush(block, text, state);
                state = s;
            }

            if (i > 0) text.Append(i % 8 == 0 ? "  " : " ");
            text.Append(i < length ? data[at + i].ToString("X2") : "  ");
        }

        Flush(block, text, state);

        block.Inlines.Add(new Run("  |") { Foreground = LabUi.Gray });
        state = State(0);
        for (int i = 0; i < length; i++)
        {
            int s = State(i);
            if (s != state)
            {
                Flush(block, text, state);
                state = s;
            }

            text.Append(HexDump.Show(data[at + i]));
        }

        Flush(block, text, state);
        block.Inlines.Add(new Run("|") { Foreground = LabUi.Gray });
        return block;
    }

    private static void Flush(TextBlock block, StringBuilder text, int state)
    {
        if (text.Length == 0) return;

        var run = new Run(text.ToString());
        if (state != 0) run.Background = state == 1 ? LabUi.Marks[0] : state == 2 ? LabUi.Marks[2] : LabUi.Marks[1];
        block.Inlines.Add(run);
        text.Clear();
    }

    private ContextMenu BuildRowMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, Action act)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => act();
            return item;
        }

        long Selected(bool first)
        {
            var offsets = _rows.SelectedItems.OfType<ListBoxItem>().Select(i => i.Tag).OfType<long>().ToList();
            return offsets.Count == 0 ? -1 : first ? offsets.Min() : offsets.Max();
        }

        menu.Items.Add(Item("Mark In at the start of the row", () =>
                                                              {
                                                                  if (Selected(true) is >= 0 and var a) _session.SetMarks(a, _session.MarkOut);
                                                              }));
        menu.Items.Add(Item("Mark Out at the end of the row", () =>
                                                              {
                                                                  if (Selected(false) is >= 0 and var b) _session.SetMarks(_session.MarkIn, Math.Min(b + HexDump.Width, _session.Strip.Length));
                                                              }));
        menu.Items.Add(Item("Mark the selected rows", () =>
                                                      {
                                                          if (Selected(true) is >= 0 and var a && Selected(false) is var b) _session.SetMarks(a, Math.Min(b + HexDump.Width, _session.Strip.Length));
                                                      }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Cut the marked stretch", () => _session.Cut()));
        menu.Items.Add(Item("Copy the selected rows", () =>
                                                      {
                                                          var lines = _rows.SelectedItems.OfType<ListBoxItem>().Select(i => i.Content).OfType<TextBlock>().Select(t => string.Concat(t.Inlines.OfType<Run>().Select(r => r.Text)));
                                                          LabUi.Copy(string.Join("\r\n", lines));
                                                      }));
        return menu;
    }
}

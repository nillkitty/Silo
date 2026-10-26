using System.IO;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Silo.Labs.Web;

namespace Silo.Labs.Streams;

/// <summary>
///     The deck of the Stream Lab: on the left the stream topology (the stream, a tee that records what it gives, the adapters it passes through, and you as the controller)
///     and the post-processing (adapters applied to what was recorded); on the right what the stream is, its position and what it can do, and the transport buttons
///     as on a tape machine.
/// </summary>
internal sealed class StreamDeck : Grid
{
    private static readonly string[] Steps = ["1", "2", "4", "8", "16", "32", "64", "256", "1024", "4096"];
    private static readonly FontFamily Symbols = new("Segoe UI Symbol, Segoe UI Emoji, Segoe UI");
    private static readonly Brush Lit = LabUi.Frozen(0xFF, 0x99, 0x99);

    private readonly StreamLabSession _session;
    private readonly Action           _writeTyped;
    private readonly StackPanel       _topology = new();
    private readonly StackPanel       _post     = new();
    private readonly TextBlock        _streamType;
    private readonly TextBlock        _source;
    private readonly TextBox          _position;
    private readonly ComboBox         _bytes;
    private readonly Border           _canWrite;
    private readonly Border           _canRead;
    private readonly Border           _canSeek;
    private readonly TextBlock        _state;
    private readonly List<Transport>  _buttons = [];
    private readonly List<Action>     _live    = [];
    private string                    _signature = "\0";
    private bool                      _loading;

    private sealed record Transport(Button Button, Func<bool> Enabled, Func<bool>? IsLit);

    public StreamDeck(StreamLabSession session, Action writeTyped)
    {
        _session    = session;
        _writeTyped = writeTyped;

        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star), MinWidth = 240 });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star), MinWidth = 330 });

        // ---- the left: topology and post-processing -----------------------------------------------
        var left = new StackPanel { Margin = new Thickness(6) };
        left.Children.Add(LabUi.Label("Stream topology:", bold: true, mono: true).Also(l => l.Margin = new Thickness(0, 0, 0, 3)));
        left.Children.Add(Pane(_topology, Adder("Put an adapter on the stream: on a read it works on what comes from the stream, on a write on what is written to it", AddTopology)));
        left.Children.Add(LabUi.Label("Post-Processing:", bold: true, mono: true).Also(l => l.Margin = new Thickness(0, 8, 0, 3)));
        left.Children.Add(Pane(_post, Adder("Apply an adapter to what was recorded (the Processed strip); the stream is not touched", AddPost), 90));
        SetColumn(left, 0);
        Children.Add(left);

        // ---- the right: what the stream is, then the transport ------------------------------------
        _streamType = LabUi.Label(string.Empty, mono: true);
        _source     = LabUi.Label(string.Empty, mono: true);
        _state      = LabUi.Label(string.Empty, muted: true, wrap: true);
        _position   = new TextBox { MinWidth = 150, Margin = new Thickness(2), FontFamily = LabUi.Mono, TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                                    ToolTip = "Where the position is: type another (1234 or 0x4D2) and press Enter to move it. A stream that cannot seek has no position to set." };
        _position.KeyDown += (_, e) =>
                             {
                                 if (e.Key != Key.Enter) return;

                                 if (StreamLabSession.TryNumber(_position.Text, out long n)) _session.SeekTo(n);
                                 else _session.Say("Type the position as a number: 1234 or 0x4D2.");
                             };
        _bytes = new ComboBox { IsEditable = true, Width = 80, Margin = new Thickness(2), ToolTip = "How many bytes Take, Wait, Rewind and Fast Forward go by" };
        foreach (string s in Steps) _bytes.Items.Add(s);
        _bytes.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) =>
                                                                                                                       {
                                                                                                                           if (!_loading && int.TryParse(_bytes.Text, out int n) && n > 0) _session.EditSetup(s => s.Step = Math.Min(n, 1 << 24));
                                                                                                                       }));
        _canWrite = Flag();
        _canRead  = Flag();
        _canSeek  = Flag();

        var facts = new StackPanel { Margin = new Thickness(6, 6, 6, 0) };
        facts.Children.Add(Fact("Stream:", _streamType));
        facts.Children.Add(Fact("Source:", _source));

        var props = new Grid { Margin = new Thickness(6, 6, 6, 4) };
        props.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        props.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        int row = 0;
        void Prop(string title, UIElement value, string tip)
        {
            props.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = LabUi.Label(title, mono: true);
            label.Margin  = new Thickness(24, 4, 6, 4);
            label.ToolTip = tip;
            SetRow(label, row);
            SetRow(value, row);
            SetColumn(value, 1);
            props.Children.Add(label);
            props.Children.Add(value);
            row++;
        }

        Prop("Position:", _position, "Where the stream is: the next byte read or written");
        Prop("Can Write:", _canWrite, "Whether the stream may be written to");
        Prop("Can Read:", _canRead, "Whether the stream may be read from");
        Prop("Can Seek:", _canSeek, "Whether the position may be moved (a connection cannot)");
        Prop("Bytes:", _bytes, "How many bytes a step goes by");

        var deck = new Grid { Margin = new Thickness(6, 4, 6, 6) };
        for (int c = 0; c < 5; c++) deck.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < 3; r++) deck.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        void Press(int r, int c, string caption, string glyph, Brush color, Action click, Func<bool> enabled, string tip, Func<bool>? lit = null)
        {
            var glyphBlock = new TextBlock { Text = glyph, FontFamily = Symbols, FontSize = 22, Foreground = color, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var button     = new Button { Content = glyphBlock, Height = 46, Margin = new Thickness(3, 3, 3, 0), ToolTip = tip };
            button.Click += (_, _) => click();
            var text = LabUi.Label(caption, mono: true);
            text.HorizontalAlignment = HorizontalAlignment.Center;
            text.TextAlignment       = TextAlignment.Center;
            text.TextWrapping        = TextWrapping.Wrap;
            text.FontSize            = 11;
            text.Margin              = new Thickness(0, 0, 0, 2);
            var cell = new StackPanel();
            cell.Children.Add(button);
            cell.Children.Add(text);
            SetRow(cell, r);
            SetColumn(cell, c);
            deck.Children.Add(cell);
            _buttons.Add(new Transport(button, enabled, lit));
        }

        int Step() => _session.Setup.Step;
        bool Free() => !_session.IsBusy;

        // the top row winds, the middle row reads and writes, the bottom row looks after the stream
        Press(0, 0, "Start", "⏮", LabUi.Amber, () => _session.Rewind(), () => _session.CanSeek && Free(), "Wind back to the start of the stream");
        Press(0, 1, "Rewind", "⏪", LabUi.Amber, () => _session.SeekBy(-Step()), () => _session.CanSeek && Free(), "Move the position back by the number of bytes in the Bytes box");
        Press(0, 2, "Open", "▶", LabUi.Green, OpenOrPlay, () => Free() || _session.IsPlaying, "Open the reel if it is not open; if it is, play it: read a step after step (Hold stops)",
            () => _session.IsPlaying);
        Press(0, 3, "FF", "⏩", LabUi.Blue, () => _session.SeekBy(Step()), () => _session.CanSeek && Free(), "Move the position forward by the number of bytes in the Bytes box");
        Press(0, 4, "End", "⏭", LabUi.Blue, () => _session.WindToEnd(), () => _session.CanSeek && Free(), "Wind to the end of the stream");

        Press(1, 0, "Byte\nRetreat", "‹", LabUi.Gray, () => _session.StepBack(1), () => _session.CanSeek && Free(), "Move the position back one byte");
        Press(1, 1, "Close", "■", LabUi.Gray, _session.CloseReel, () => _session.IsOpen || _session.State == ReelState.Failed, "Close the stream");
        Press(1, 2, "Write", "●", LabUi.Red, _writeTyped, () => _session.CanWrite && Free(), "Write what is typed in the Write box, at the position, through the adapters that apply to writes",
            () => _session.CanWrite);
        Press(1, 3, "Hold", "⏸", LabUi.Blue, _session.Stop, () => _session.IsPlaying || _session.IsBusy, "Stop Play, or give up a read that is waiting (Escape)", () => _session.IsPlaying);
        Press(1, 4, "Byte\nAdvance", "›", LabUi.Gray, () => _ = _session.StepAsync(), () => _session.CanRead && Free(), "Read one byte (F10)");

        Press(2, 0, "Sync", "⚙", LabUi.Green, _session.Flush, () => _session.CanWrite && Free(), "Flush: make the stream pass on what it is holding back");
        Press(2, 1, "Wait", "◷", LabUi.Blue, () => _ = _session.ReadAsync(Step(), ReadMode.Exactly), () => _session.CanRead && Free(),
            "Read exactly that many bytes: keep asking until they have all come, or the data ends (a connection may make you wait)");
        Press(2, 2, "Take", "▼", LabUi.Blue, () => _ = _session.ReadAsync(Step(), ReadMode.Once), () => _session.CanRead && Free(),
            "Read once, asking for that many bytes: what comes may be less (F5)");
        Press(2, 3, "Correct", "⚠", LabUi.Amber, _session.Resync, () => _session.IsOpen && Free(), "Put the adapters on afresh at the stream's position: what one had half done is forgotten");
        Press(2, 4, "Join", "◢", LabUi.Gray, () => _session.SpliceToNewReel(), () => _session.Cuts.Count > 0, "Join the cuts, in the order of the Cuts list, as a new reel");

        var right = new StackPanel();
        right.Children.Add(facts);
        right.Children.Add(props);
        right.Children.Add(deck);
        right.Children.Add(_state.Also(s => s.Margin = new Thickness(8, 0, 8, 4)));
        SetColumn(right, 1);
        Children.Add(right);

        _session.Changed        += (_, _) => Refresh();
        _session.Loaded         += (_, _) => Refresh();
        _session.AdaptersChanged += (_, _) => _signature = "\0";
        Refresh();
    }

    private static Border Flag()
    {
        var text = new TextBlock { FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontFamily = LabUi.Mono };
        return new Border { Child = text, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(2), BorderThickness = new Thickness(1), BorderBrush = Brushes.Black };
    }

    private static void SetFlag(Border flag, bool? value)
    {
        flag.Background            = value is null ? LabUi.Gray : value.Value ? LabUi.Green : LabUi.Red;
        ((TextBlock)flag.Child).Text = value is null ? "—" : value.Value ? "TRUE" : "FALSE";
    }

    private static UIElement Fact(string title, TextBlock value)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var label = LabUi.Label(title, mono: true);
        label.Width = 80;
        DockPanel.SetDock(label, System.Windows.Controls.Dock.Left);
        value.TextTrimming = TextTrimming.CharacterEllipsis;
        row.Children.Add(label);
        row.Children.Add(value);
        return row;
    }

    /// <summary>A bordered pane with a list of rows and a round + button in its corner.</summary>
    private static UIElement Pane(StackPanel rows, Button add, double minHeight = 130)
    {
        var scroll = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MinHeight = minHeight, MaxHeight = 220 };
        var grid   = new Grid();
        grid.Children.Add(scroll);
        add.HorizontalAlignment = HorizontalAlignment.Right;
        add.VerticalAlignment   = VerticalAlignment.Bottom;
        add.Margin              = new Thickness(0, 0, 8, 6);
        grid.Children.Add(add);
        return LabUi.Box(grid, padding: new Thickness(2));
    }

    private Button Adder(string tip, Action<AdapterDef> choose)
    {
        var button = new Button { Content = "+", Width = 28, Height = 28, FontSize = 18, FontWeight = FontWeights.Bold, Padding = new Thickness(0, -3, 0, 0), ToolTip = tip };
        button.Click += (_, _) =>
                        {
                            var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
                            foreach (var def in StreamAdapters.All)
                            {
                                var captured = def;
                                var item     = new MenuItem { Header = $"{def.Title}  ({def.Key})", ToolTip = def.Description };
                                item.Click += (_, _) => choose(captured);
                                menu.Items.Add(item);
                            }

                            menu.IsOpen = true;
                        };
        return button;
    }

    private void AddTopology(AdapterDef def) => _session.AddAdapter(new AdapterSpec { Key = def.Key, Arg = def.ArgDefault });

    private void AddPost(AdapterDef def)
    {
        if (!def.CanRead)
        {
            _session.Say($"{def.Title} cannot work on bytes that are read, so it cannot be used for post-processing.");
            return;
        }

        _session.AddPost(new AdapterSpec { Key = def.Key, Arg = def.ArgDefault, Side = AdapterSide.Read });
    }

    private void OpenOrPlay()
    {
        if (_session.IsPlaying) return;

        if (_session.IsOpen && _session.CanRead) _ = _session.PlayAsync();
        else _ = _session.OpenReelAsync();
    }

    // ---- drawing -----------------------------------------------------------------------------------

    private void Refresh()
    {
        var  setup = _session.Setup;
        var  reel  = _session.Reel;
        string signature = string.Join('|', setup.Adapters.Select(a => a.ToString() + StreamLabSession.ProblemOf(a)))
                           + "#" + string.Join('|', setup.Post.Select(a => a.ToString() + StreamLabSession.ProblemOf(a)))
                           + "#" + (_session.IsOpen ? reel?.Stream.GetType().FullName : _session.State.ToString()) + setup.Source.Kind;
        if (signature != _signature)
        {
            _signature = signature;
            Rebuild();
        }

        _loading = true;
        try
        {
            _streamType.Text = _session.IsOpen ? reel!.Stream.GetType().FullName : setup.Source.Kind switch
                                                                                { StreamKind.File => "System.IO.FileStream", StreamKind.Memory => "System.IO.MemoryStream", StreamKind.Tcp => "System.Net.Sockets.NetworkStream", StreamKind.Tls => "System.Net.Security.SslStream", _ => "System.IO.Stream" } + " (not open)";
            _source.Text = setup.Source.ToString();
            if (!_bytes.IsKeyboardFocusWithin) _bytes.Text = setup.Step.ToString();
            if (!_position.IsKeyboardFocusWithin)
            {
                _position.Text      = _session.Position is { } p ? $"0x{p:X}" : _session.IsOpen ? $"{_session.Pipeline?.InputTotal ?? 0:N0} read" : "—";
                _position.IsEnabled = _session.CanSeek;
            }
        }
        finally
        {
            _loading = false;
        }

        SetFlag(_canWrite, _session.IsOpen ? _session.CanWrite : null);
        SetFlag(_canRead, _session.IsOpen ? _session.CanRead : null);
        SetFlag(_canSeek, _session.IsOpen ? _session.CanSeek : null);
        _state.Text = _session.OpenError ?? _session.AdapterProblem ?? _session.PostProblem ?? string.Empty;
        _state.Foreground = LabUi.Red;

        foreach (var live in _live) live();
        foreach (var t in _buttons)
        {
            t.Button.IsEnabled = t.Enabled();
            t.Button.Background = t.IsLit?.Invoke() == true ? Lit : null!;
            if (t.Button.Background is null) t.Button.ClearValue(Control.BackgroundProperty);
        }
    }

    private void Rebuild()
    {
        _live.Clear();
        _topology.Children.Clear();
        _post.Children.Clear();
        var setup = _session.Setup;

        // the stream, then a tee that records what it gives, then the adapters, then you
        var streamName = _session.IsOpen ? _session.Reel!.Stream.GetType().Name : setup.Source.Kind switch
                                                                              { StreamKind.File => "FileStream", StreamKind.Memory => "MemoryStream", StreamKind.Tcp => "NetworkStream", StreamKind.Tls => "SslStream", _ => "Stream" };
        _topology.Children.Add(Node("≋", streamName, italicBold: true, null, () => _session.ShowStrip(StripSide.Input), "The stream itself: click to look at what was read from it"));

        var inCount = new TextBlock { Margin = new Thickness(8, 0, 0, 0) }.Muted();
        _live.Add(() => inCount.Text = $"{_session.StripOf(StripSide.Input).Length:N0} B");
        var tee = Node("⑂", "Tee", italicBold: false, null, () => _session.ShowStrip(StripSide.Input), "Records what the stream gives (the Input strip): click to look at it");
        var rec = new StackPanel { Orientation = Orientation.Horizontal };
        rec.Children.Add(new TextBlock { Text = "●", Foreground = LabUi.Red, FontFamily = Symbols, Margin = new Thickness(10, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center });
        rec.Children.Add(LabUi.Label("Recording", bold: true));
        rec.Children.Add(inCount);
        ((StackPanel)tee).Children.Add(rec);
        _topology.Children.Add(tee);

        foreach (var spec in setup.Adapters.ToList()) _topology.Children.Add(AdapterRow(spec, topology: true));

        var outCount = new TextBlock { Margin = new Thickness(8, 0, 0, 0) }.Muted();
        _live.Add(() => outCount.Text = $"{_session.StripOf(StripSide.Output).Length:N0} B");
        var controller = Node("⌸", "Controller (you)", italicBold: false, outCount, () => _session.ShowStrip(StripSide.Output), "You: what comes out at this end is the Output strip; click to look at it");
        _topology.Children.Add(controller);

        foreach (var spec in setup.Post.ToList()) _post.Children.Add(AdapterRow(spec, topology: false));
        var processed = new TextBlock { Margin = new Thickness(8, 0, 0, 0) }.Muted();
        _live.Add(() => processed.Text = $"{_session.StripOf(StripSide.Processed).Length:N0} B");
        _post.Children.Add(Node("▤", "Processed", italicBold: false, processed, () => _session.ShowStrip(StripSide.Processed), "What the post-processing makes of the Output strip: click to look at it"));
    }

    private static UIElement Node(string glyph, string title, bool italicBold, TextBlock? extra, Action click, string tip)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 4, 4, 4), Background = Brushes.Transparent, ToolTip = tip, Cursor = Cursors.Hand };
        row.Children.Add(new TextBlock { Text = glyph, FontFamily = Symbols, FontSize = 22, Width = 34, VerticalAlignment = VerticalAlignment.Center }.Also(t => t.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text")));
        var name = LabUi.Label(title, bold: italicBold);
        name.FontStyle = FontStyles.Italic;
        name.FontSize  = italicBold ? 15 : 13;
        row.Children.Add(name);
        if (extra is not null) row.Children.Add(extra);
        row.MouseLeftButtonUp += (_, _) => click();
        return row;
    }

    private UIElement AdapterRow(AdapterSpec spec, bool topology)
    {
        var def     = StreamAdapters.Find(spec.Key);
        var problem = StreamLabSession.ProblemOf(spec);
        var row     = new DockPanel { Margin = new Thickness(38, 2, 4, 2), LastChildFill = true };
        row.PreviewMouseLeftButtonDown += (_, _) => _session.SelectStage(spec);

        void Put(UIElement e, System.Windows.Controls.Dock side)
        {
            DockPanel.SetDock(e, side);
            row.Children.Add(e);
        }

        Action<AdapterSpec, int> move   = topology ? (s, d) => _session.MoveAdapter(s, d) : (s, d) => _session.MovePost(s, d);
        Action<AdapterSpec>      remove = topology ? s => _session.RemoveAdapter(s) : s => _session.RemovePost(s);
        Action<AdapterSpec, Action<AdapterSpec>> change = topology ? (s, c) => _session.ChangeAdapter(s, c) : (s, c) => _session.ChangePost(s, c);

        Button Small(string text, string tip, Action click)
        {
            var b = LabUi.Button(text, click, tip);
            b.Padding = new Thickness(4, 0, 4, 0);
            b.Margin  = new Thickness(1, 0, 1, 0);
            b.FontFamily = Symbols;
            return b;
        }

        Put(Small("✕", "Take the adapter off", () => remove(spec)), System.Windows.Controls.Dock.Right);
        Put(Small("▼", "Move it later in the chain", () => move(spec, 1)), System.Windows.Controls.Dock.Right);
        Put(Small("▲", "Move it earlier in the chain", () => move(spec, -1)), System.Windows.Controls.Dock.Right);
        Put(LabUi.Check(string.Empty, spec.Enabled, on => change(spec, s => s.Enabled = on), "On or off: an adapter that is off stays in the chain but is left out"), System.Windows.Controls.Dock.Left);

        var name = LabUi.Label(def?.Title ?? spec.Key, bold: true);
        name.Margin  = new Thickness(0, 0, 6, 0);
        name.ToolTip = def?.Description;
        Put(name, System.Windows.Controls.Dock.Left);

        if (def is { ArgHint.Length: > 0 })
        {
            var arg = LabUi.Field(spec.Arg, v => change(spec, s => s.Arg = v), 80, def.ArgHint, true);
            if (problem is not null)
            {
                arg.BorderBrush = LabUi.Red;
                arg.ToolTip     = problem;
            }

            row.Children.Add(arg);
        }
        else
        {
            var report = LabUi.Label(string.Empty, muted: true);
            _live.Add(() => report.Text = topology ? _session.ReportOf(spec)?.Replace("\n", "  ") ?? string.Empty : string.Empty);
            row.Children.Add(report);
        }

        return row;
    }
}

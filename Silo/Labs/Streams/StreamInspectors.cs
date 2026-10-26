using System.IO;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Silo.Labs.Web;

namespace Silo.Labs.Streams;

/// <summary>The stream's properties as they are now, and the controls to change the ones that can be changed (position, length, time limits).</summary>
internal sealed class StreamPropertiesInspector(StreamLabSession session) : LiveInspector(session)
{
    private StackPanel? _facts;
    private TextBox?    _position;
    private TextBox?    _length;
    private TextBox?    _readTimeout;
    private TextBox?    _writeTimeout;
    private Button?     _goButton;
    private Button?     _lengthButton;
    private Button?     _readButton;
    private Button?     _writeButton;

    public override string Id => "properties";

    public override string Title => "Properties";

    public override string IconKey => "DataModel";

    protected override UIElement Create()
    {
        _position     = LabUi.Line(string.Empty, _ => { }, 100, "The position: 1234 or 0x4D2", true);
        _length       = LabUi.Line(string.Empty, _ => { }, 100, "The length: the stream is cut or grown to it", true);
        _readTimeout  = LabUi.Line(string.Empty, _ => { }, 100, "How long a read may wait, in milliseconds (0: for ever)", true);
        _writeTimeout = LabUi.Line(string.Empty, _ => { }, 100, "How long a write may wait, in milliseconds (0: for ever)", true);
        _goButton     = LabUi.Button("Set", () =>
                                            {
                                                if (StreamLabSession.TryNumber(_position.Text, out long n)) session.SeekTo(n);
                                                else session.Say("Type the position as a number: 1234 or 0x4D2.");
                                            }, "Move the position");
        _lengthButton = LabUi.Button("Set", () =>
                                            {
                                                if (StreamLabSession.TryNumber(_length.Text, out long n)) session.SetLength(n);
                                                else session.Say("Type the length as a number: 1234 or 0x4D2.");
                                            }, "Cut or grow the stream to that length");
        _readButton = LabUi.Button("Set", () => SetTimeout(true, _readTimeout), "Set how long a read may wait");
        _writeButton = LabUi.Button("Set", () => SetTimeout(false, _writeTimeout), "Set how long a write may wait");

        var editors = new Grid { Margin = new Thickness(6) };
        editors.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        editors.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        editors.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        int row = 0;
        void Editor(string title, TextBox box, Button button)
        {
            editors.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = LabUi.Label(title, muted: true);
            label.Margin = new Thickness(0, 2, 8, 2);
            Grid.SetRow(label, row);
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            Grid.SetRow(button, row);
            Grid.SetColumn(button, 2);
            editors.Children.Add(label);
            editors.Children.Add(box);
            editors.Children.Add(button);
            row++;
        }

        Editor("Position", _position, _goButton);
        Editor("Length", _length, _lengthButton);
        Editor("Read timeout", _readTimeout, _readButton);
        Editor("Write timeout", _writeTimeout, _writeButton);

        _facts = new StackPanel { Margin = new Thickness(6, 0, 6, 6) };
        var scroll = new ScrollViewer { Content = _facts, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        scroll.SetResourceReference(Control.BackgroundProperty, "Theme.Background");
        var copy = LabUi.Button("Copy", () => LabUi.Copy(string.Join("\r\n", session.Reel?.Facts().Select(f => $"{f.Name}: {f.Value}") ?? [])), "Put the properties on the clipboard");
        return LabUi.Layout(editors, scroll, LabUi.Bar(copy));
    }

    private void SetTimeout(bool read, TextBox box)
    {
        if (int.TryParse(box.Text.Trim(), out int ms) && ms >= 0) session.SetTimeout(read, ms);
        else session.Say("Type the time limit in milliseconds (0 for none).");
    }

    protected override void Refresh()
    {
        if (_facts is null || _position is null || _length is null || _readTimeout is null || _writeTimeout is null) return;

        bool open = session.IsOpen;
        var stream = session.Reel?.Stream;
        bool idle = !session.IsBusy;
        _position.IsEnabled = _goButton!.IsEnabled = session.CanSeek && idle;
        _length.IsEnabled   = _lengthButton!.IsEnabled = session.CanSeek && session.CanWrite && idle;
        _readTimeout.IsEnabled = _readButton!.IsEnabled = open && stream!.CanTimeout;
        _writeTimeout.IsEnabled = _writeButton!.IsEnabled = open && stream!.CanTimeout;
        if (!_position.IsKeyboardFocusWithin) _position.Text = session.Position?.ToString() ?? string.Empty;
        if (!_length.IsKeyboardFocusWithin) _length.Text = session.Length?.ToString() ?? string.Empty;
        if (open && stream!.CanTimeout)
        {
            try
            {
                if (!_readTimeout.IsKeyboardFocusWithin) _readTimeout.Text = (stream.ReadTimeout == Timeout.Infinite ? 0 : stream.ReadTimeout).ToString();
                if (!_writeTimeout.IsKeyboardFocusWithin) _writeTimeout.Text = (stream.WriteTimeout == Timeout.Infinite ? 0 : stream.WriteTimeout).ToString();
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                // a stream that says it has time limits and has not
            }
        }

        _facts.Children.Clear();
        if (!open)
        {
            _facts.Children.Add(LabUi.Label(session.State == ReelState.Failed ? (session.OpenError ?? "The reel could not be opened.") : "The reel is not open: nothing to show yet.", muted: true, wrap: true).Also(l => l.Margin = new Thickness(0, 6, 0, 0)));
            return;
        }

        string? group = null;
        Grid? grid = null;
        foreach (var fact in session.Reel!.Facts())
        {
            if (fact.Group != group)
            {
                group = fact.Group;
                _facts.Children.Add(LabUi.Heading(group));
                grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                _facts.Children.Add(grid);
            }

            int r = grid!.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = LabUi.Label(fact.Name, muted: true);
            name.Margin = new Thickness(0, 1, 6, 1);
            var value = LabUi.Label(fact.Value, mono: true, wrap: true);
            value.Margin = new Thickness(0, 1, 0, 1);
            if (fact.Value is "yes") value.Foreground = LabUi.Green;
            else if (fact.Value is "no" or "(not available)" || fact.Value.StartsWith("(not known", StringComparison.Ordinal)) value.Foreground = LabUi.Gray;
            else if (fact.Name == "Certificate problems" && fact.Value != "none") value.Foreground = LabUi.Amber;
            Grid.SetRow(name, r);
            Grid.SetRow(value, r);
            Grid.SetColumn(value, 1);
            grid.Children.Add(name);
            grid.Children.Add(value);
        }
    }
}

/// <summary>The chain of adapters: add, remove, move, switch off, choose the side, and see what each reports.</summary>
internal sealed class StreamAdaptersInspector : LiveInspector
{
    private readonly StreamLabSession _session;
    private StackPanel? _rows;
    private ComboBox?   _choice;
    private TextBlock?  _description;
    private string      _signature = string.Empty;
    private readonly List<(AdapterSpec Spec, TextBlock Report)> _reports = [];

    public StreamAdaptersInspector(StreamLabSession session) : base(session)
    {
        _session                =  session;
        session.AdaptersChanged += (_, _) => _signature = string.Empty;
    }

    public override string Id => "adapters";

    public override string Title => "Adapters";

    public override string IconKey => "Puzzle";

    protected override UIElement Create()
    {
        _choice = LabUi.Combo(StreamAdapters.All.Select(a => a.Key), StreamAdapters.All[0].Key, _ => DescribeChoice(), 100);
        _description = LabUi.Label(string.Empty, muted: true, wrap: true);
        _description.Margin = new Thickness(8, 0, 8, 4);
        var add = LabUi.Button("Add", AddChosen, "Put the adapter at the end of the chain");
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Margin = new Thickness(8, 6, 8, 2), TextWrapping = TextWrapping.Wrap, Text = "Data goes through the adapters in order: on a read from the stream outwards, on a write from what you write towards the stream." }.Muted());
        head.Children.Add(LabUi.Bar(_choice, add));
        head.Children.Add(_description);

        _rows = new StackPanel();
        var scroll = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        scroll.SetResourceReference(Control.BackgroundProperty, "Theme.Background");
        DescribeChoice();
        return LabUi.Layout(head, scroll);
    }

    private void DescribeChoice()
    {
        if (_description is null || StreamAdapters.Find(_choice?.SelectedItem as string) is not { } def) return;

        _description.Text = $"{def.Title}. {def.Description}{(def.ArgHint.Length > 0 ? " Argument: " + def.ArgHint + "." : string.Empty)} {(def.CanRead && def.CanWrite ? string.Empty : def.CanRead ? "Reads only." : "Writes only.")}";
    }

    private void AddChosen()
    {
        if (StreamAdapters.Find(_choice?.SelectedItem as string) is { } def) _session.AddAdapter(new AdapterSpec { Key = def.Key, Arg = def.ArgDefault });
    }

    protected override void Refresh()
    {
        if (_rows is null) return;

        string signature = string.Join('\n', _session.Setup.Adapters.Select(a => $"{a.Key}|{a.Arg}|{a.Side}|{a.Enabled}"));
        if (signature != _signature)
        {
            _signature = signature;
            Rebuild();
        }

        foreach (var (spec, report) in _reports)
        {
            string? text = _session.ReportOf(spec);
            report.Text       = text ?? string.Empty;
            report.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void Rebuild()
    {
        _rows!.Children.Clear();
        _reports.Clear();
        var specs = _session.Setup.Adapters;
        if (specs.Count == 0)
        {
            _rows.Children.Add(LabUi.Label("No adapters: the data goes through as it is.", muted: true, wrap: true).Also(l => l.Margin = new Thickness(8)));
            return;
        }

        for (int i = 0; i < specs.Count; i++) _rows.Children.Add(Row(specs[i], i));
    }

    private UIElement Row(AdapterSpec spec, int index)
    {
        var def   = StreamAdapters.Find(spec.Key);
        var panel = new StackPanel();

        var top = new DockPanel();
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(LabUi.Button("^", () => _session.MoveAdapter(spec, -1), "Move it earlier in the chain", index > 0));
        tools.Children.Add(LabUi.Button("v", () => _session.MoveAdapter(spec, 1), "Move it later in the chain", index < _session.Setup.Adapters.Count - 1));
        tools.Children.Add(LabUi.Button("x", () => _session.RemoveAdapter(spec), "Take it out of the chain"));
        DockPanel.SetDock(tools, System.Windows.Controls.Dock.Right);
        top.Children.Add(tools);
        var enabled = LabUi.Check($"{index + 1}. {def?.Title ?? spec.Key}", spec.Enabled, on => _session.ChangeAdapter(spec, a => a.Enabled = on), def?.Description);
        enabled.FontWeight = FontWeights.SemiBold;
        top.Children.Add(enabled);
        panel.Children.Add(top);

        var settings = new WrapPanel { Margin = new Thickness(22, 2, 0, 2) };
        if (def is null || def.ArgHint.Length > 0 || spec.Arg.Length > 0)
        {
            settings.Children.Add(LabUi.Label("Argument", muted: true).Also(l => l.Margin = new Thickness(0, 0, 4, 0)));
            settings.Children.Add(LabUi.Field(spec.Arg, v => _session.ChangeAdapter(spec, a => a.Arg = v), 130, def?.ArgHint, true));
        }

        settings.Children.Add(LabUi.Label("Side", muted: true).Also(l => l.Margin = new Thickness(8, 0, 4, 0)));
        settings.Children.Add(LabUi.Combo(["Both", "Read", "Write"], spec.Side.ToString(), v => _session.ChangeAdapter(spec, a => a.Side = Enum.Parse<AdapterSide>(v)), 70));
        panel.Children.Add(settings);

        string? problem = StreamLabSession.ProblemOf(spec);
        string? note = def is null ? null : spec.Side == AdapterSide.Read && !def.CanRead ? "It cannot be put on a read." : spec.Side == AdapterSide.Write && !def.CanWrite ? "It cannot be put on a write." :
                       spec.Side == AdapterSide.Both && !def.CanWrite ? "Reads only: left out of writes." : spec.Side == AdapterSide.Both && !def.CanRead ? "Writes only: left out of reads." : null;
        if (problem is not null) panel.Children.Add(LabUi.Label(problem, wrap: true).Also(l =>
                                                                                      {
                                                                                          l.Foreground = LabUi.Red;
                                                                                          l.Margin     = new Thickness(22, 0, 0, 2);
                                                                                      }));
        else if (note is not null) panel.Children.Add(LabUi.Label(note, muted: true, wrap: true).Also(l => l.Margin = new Thickness(22, 0, 0, 2)));

        var report = LabUi.Label(string.Empty, mono: true, wrap: true);
        report.Foreground = LabUi.Blue;
        report.Margin     = new Thickness(22, 0, 0, 2);
        report.Visibility = Visibility.Collapsed;
        panel.Children.Add(report);
        _reports.Add((spec, report));

        var box = LabUi.Box(panel);
        box.PreviewMouseLeftButtonDown += (_, _) => _session.SelectStage(spec);
        return box;
    }
}

/// <summary>The strip being looked at (or what is marked of it) as text in an encoding of your choice.</summary>
internal sealed class StreamTextInspector(StreamLabSession session) : LiveInspector(session)
{
    private static readonly string[] Encodings = ["utf-8", "utf-16", "utf-16BE", "utf-32", "us-ascii", "iso-8859-1", "windows-1252"];

    private TextBox? _text;
    private string   _encoding = "utf-8";

    public override string Id => "text";

    public override string Title => "Text";

    public override string IconKey => "Document";

    public override bool VisibleByDefault => false;

    protected override UIElement Create()
    {
        _text = LabUi.Output(wrap: true);
        var combo = LabUi.Combo(Encodings, _encoding, v =>
                                                      {
                                                          _encoding = v;
                                                          Refresh();
                                                      }, 110);
        var copy = LabUi.Button("Copy", () => LabUi.Copy(_text.Text), "Put the text on the clipboard");
        return LabUi.Layout(LabUi.Bar(LabUi.Label("As", muted: true), combo, copy), _text);
    }

    protected override void Refresh()
    {
        if (_text is null) return;

        var strip = session.Strip;
        var (from, to) = session.HasMarks ? session.MarkedRange() : (0L, (long)strip.Length);
        to = Math.Min(to, from + (256 << 10));
        try
        {
            _text.Text = Encoding.GetEncoding(_encoding).GetString(strip.Slice(from, to));
        }
        catch (ArgumentException ex)
        {
            _text.Text = ex.Message;
        }
    }
}

/// <summary>The pieces cut from the strips: put in order, joined into a new reel, saved, or written to the stream.</summary>
internal sealed class StreamCutsInspector(StreamLabSession session) : LiveInspector(session)
{
    private ListBox?   _list;
    private TextBlock? _info;
    private int        _shown = -1;

    public override string Id => "cuts";

    public override LabPaneKind Kind => LabPaneKind.Entity;

    public override string Title => "Cuts";

    public override string IconKey => "TbCopy";

    protected override UIElement Create()
    {
        _list = LabUi.List();
        _list.SelectionMode = SelectionMode.Extended;
        _list.MouseDoubleClick += (_, _) => Rename();
        _list.SelectionChanged += (_, _) =>
                                  {
                                      if (_list.SelectedItems.Count == 1 && (_list.SelectedItem as ListBoxItem)?.Tag is StreamCut picked) session.SelectStage(picked);
                                  };
        _info = LabUi.Label(string.Empty, muted: true, wrap: true);
        _info.Margin = new Thickness(8, 4, 8, 2);

        var bar1 = LabUi.Bar(LabUi.Button("Cut", () => session.Cut(), "Cut the marked stretch of the strip"),
                             LabUi.Button("^", () => Move(-1), "Earlier in the order"), LabUi.Button("v", () => Move(1), "Later in the order"),
                             LabUi.Button("Remove", Remove, "Throw the selected cuts away"));
        var bar2 = LabUi.Bar(LabUi.Button("New reel", () => session.SpliceToNewReel(Chosen()), "Join the selected cuts (all of them when none is selected) into a reel of their own, in a new window"),
                             LabUi.Button("Save...", Save, "Write the joined cuts to a file"),
                             LabUi.Button("Write", () => _ = session.WriteAsync(session.Spliced(Chosen()), "the cuts"), "Write the joined cuts to the stream, through its adapters, at its position"),
                             LabUi.Button("Copy hex", () => LabUi.Copy(HexDump.Format(session.Spliced(Chosen()))), "Put the joined cuts on the clipboard as a hex dump"));
        var bottom = new StackPanel();
        bottom.Children.Add(_info);
        bottom.Children.Add(bar1);
        bottom.Children.Add(bar2);
        return LabUi.Layout(null, _list, bottom);
    }

    private List<StreamCut> Chosen()
    {
        var selected = _list?.SelectedItems.OfType<ListBoxItem>().Select(i => i.Tag).OfType<StreamCut>().ToList() ?? [];
        return selected.Count > 0 ? session.Cuts.Where(selected.Contains).ToList() : session.Cuts.ToList();
    }

    private void Move(int delta)
    {
        var chosen = Chosen();
        if (chosen.Count != 1 || _list?.SelectedItems.Count != 1) return;

        session.MoveCut(chosen[0], delta);
    }

    private void Remove()
    {
        if (_list is null || _list.SelectedItems.Count == 0) return;

        foreach (var cut in Chosen()) session.RemoveCut(cut);
    }

    private void Rename()
    {
        if (_list?.SelectedItem is not ListBoxItem { Tag: StreamCut cut }) return;

        string? name = LabUi.Ask("Cut", "What is it called?", cut.Label);
        if (string.IsNullOrWhiteSpace(name)) return;

        cut.Label = name.Trim();
        Refresh();
    }

    private void Save()
    {
        var data = session.Spliced(Chosen());
        if (data.Length == 0)
        {
            session.Say("There is nothing cut yet.");
            return;
        }

        var dialog = new SaveFileDialog { Title = "Save the cuts", FileName = "splice.bin" };
        if (dialog.ShowDialog() != true) return;

        File.WriteAllBytes(dialog.FileName, data);
        session.Say($"Saved {data.Length:N0} byte(s) to {Path.GetFileName(dialog.FileName)}");
    }

    protected override void Refresh()
    {
        if (_list is null || _info is null) return;

        var cuts = session.Cuts;
        long total = cuts.Sum(c => (long)c.Data.Length);
        _info.Text = cuts.Count == 0 ? "Mark In and Out on the strip, then Cut. Double-click a cut to rename it." : $"{cuts.Count} cut(s), {total:N0} byte(s) in all. Double-click a cut to rename it.";

        // only redrawn when the cuts changed, so a selection is not lost with every step
        int shown = cuts.Count * 1000 + cuts.Select((c, i) => (c.Label.GetHashCode() ^ c.Data.Length) * (i + 1)).Sum() % 997;
        if (shown == _shown) return;

        _shown = shown;
        _list.Items.Clear();
        foreach (var cut in cuts)
        {
            string ascii = LabUi.Shorten(HexDump.Ascii(cut.Data.AsSpan(0, Math.Min(cut.Data.Length, 48))), 32);
            _list.Items.Add(LabUi.Item(cut, LabUi.Row("TbCut", cut.Label, $"{cut.Data.Length:N0} bytes", ascii), cut.Origin));
        }
    }
}

/// <summary>What was done, a line to a step, with the time.</summary>
internal sealed class StreamLogInspector(StreamLabSession session) : LiveInspector(session)
{
    private TextBox? _text;
    private int      _lines = -1;

    public override string Id => "log";

    public override string Title => "Log";

    public override string IconKey => "Checklist";

    public override bool VisibleByDefault => false;

    protected override UIElement Create()
    {
        _text = LabUi.Output();
        var copy = LabUi.Button("Copy", () => LabUi.Copy(_text.Text), "Put the log on the clipboard");
        return LabUi.Layout(null, _text, LabUi.Bar(copy));
    }

    protected override void Refresh()
    {
        if (_text is null || session.Log.Count == _lines) return;

        _lines     = session.Log.Count;
        _text.Text = string.Join("\r\n", session.Log);
        _text.ScrollToEnd();
    }
}

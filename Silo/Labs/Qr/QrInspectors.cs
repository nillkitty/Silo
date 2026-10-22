using System.Text;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Silo.Labs.Qr;

/// <summary>How the code is made and how it is drawn: the level of correction, the sizes it may be, the mask, the mode, the colors.</summary>
internal sealed class QrOptionsInspector(QrLabSession session) : LiveInspector(session)
{
    private ComboBox? _ecc, _mask, _mode;
    private TextBox?  _min, _max, _border, _scale, _dark, _light;
    private CheckBox? _boost, _eci;
    private TextBlock? _capacity;
    private bool       _loading;

    public override string Id => "options";

    public override string Title => "Options";

    public override string IconKey => "Settings";

    protected override UIElement Create()
    {
        _ecc  = LabUi.Combo(Enum.GetNames<QrEcc>(), nameof(QrEcc.Medium), v => Set(d => d.Ecc = Enum.Parse<QrEcc>(v)), 110);
        _ecc.ToolTip = "How much of the code can be wrong and still be read: Low about 7%, Medium 15%, Quartile 25%, High 30%";
        _min  = LabUi.Field("1", v => Set(d => d.MinVersion = Number(v, 1, 40, d.MinVersion)), 50, "The smallest version (1 is 21 modules across, 40 is 177)", true);
        _max  = LabUi.Field("40", v => Set(d => d.MaxVersion = Number(v, 1, 40, d.MaxVersion)), 50, "The biggest version the code may be; text that does not fit it is refused", true);
        _mask = LabUi.Combo(["Auto", "0", "1", "2", "3", "4", "5", "6", "7"], "Auto", v => Set(d => d.Mask = v == "Auto" ? -1 : int.Parse(v)), 80);
        _mask.ToolTip = "The pattern laid over the data to keep the code from having big blank areas or false finders. Auto takes the one with the lowest penalty.";
        _mode = LabUi.Combo(QrDoc.Modes, "Auto", v => Set(d => d.Mode = v), 110);
        _mode.ToolTip = "How the characters are packed: Auto takes the tightest that holds all of the text";
        _boost = LabUi.Check("Raise the level if there is room", true, on => Set(d => d.Boost = on), "A better level of correction costs nothing when the code has room for it anyway");
        _eci   = LabUi.Check("Mark the bytes as UTF-8", false, on => Set(d => d.Eci = on), "An extra header that tells readers the bytes are UTF-8 (otherwise many guess)");
        _border = LabUi.Field("4", v => Set(d => d.Border = Number(v, 0, 20, d.Border)), 50, "The empty margin in modules; readers want 4", true);
        _scale  = LabUi.Field("8", v => Set(d => d.Scale = Number(v, 1, 64, d.Scale)), 50, "Pixels to a module, on the screen and in a PNG", true);
        _dark   = LabUi.Field("#000000", v => Set(d => d.Dark = v.Trim()), 90, "The color of the dark modules: #RGB or #RRGGBB", true);
        _light  = LabUi.Field("#FFFFFF", v => Set(d => d.Light = v.Trim()), 90, "The color of the light modules and the margin", true);
        _capacity = LabUi.Label(string.Empty, mono: true);
        _capacity.Margin = new Thickness(8, 4, 8, 8);

        var versions = new StackPanel { Orientation = Orientation.Horizontal };
        versions.Children.Add(_min);
        versions.Children.Add(LabUi.Label("to", muted: true).Also(l => l.Margin = new Thickness(4, 0, 4, 0)));
        versions.Children.Add(_max);
        var colors = new StackPanel { Orientation = Orientation.Horizontal };
        colors.Children.Add(_dark);
        colors.Children.Add(LabUi.Label("on", muted: true).Also(l => l.Margin = new Thickness(4, 0, 4, 0)));
        colors.Children.Add(_light);

        var form = LabUi.Form([("Correction", _ecc), ("Version", versions), ("Mask", _mask), ("Mode", _mode), ("", _boost), ("", _eci), ("Margin", _border), ("Pixels/module", _scale), ("Colors", colors)], 100);
        var panel = new StackPanel();
        panel.Children.Add(form);
        panel.Children.Add(LabUi.Heading("What fits (for the version of the code)"));
        panel.Children.Add(_capacity);
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static int Number(string text, int min, int max, int fallback) => int.TryParse(text.Trim(), out int n) ? Math.Clamp(n, min, max) : fallback;

    private void Set(Action<QrDoc> change)
    {
        if (!_loading) session.EditNow(change);
    }

    protected override void Refresh()
    {
        if (_ecc is null) return;

        var d = session.Doc;
        _loading = true;
        try
        {
            _ecc!.SelectedItem  = d.Ecc.ToString();
            _mask!.SelectedItem = d.Mask < 0 ? "Auto" : d.Mask.ToString();
            _mode!.SelectedItem = d.Mode;
            _boost!.IsChecked   = d.Boost;
            _eci!.IsChecked     = d.Eci;
            Put(_min, d.MinVersion.ToString());
            Put(_max, d.MaxVersion.ToString());
            Put(_border, d.Border.ToString());
            Put(_scale, d.Scale.ToString());
            Put(_dark, d.Dark);
            Put(_light, d.Light);
        }
        finally
        {
            _loading = false;
        }

        int version = session.Build.Code?.Version ?? d.MinVersion;
        var sb = new StringBuilder($"Version {version} ({version * 4 + 17} x {version * 4 + 17})\nlevel      digits  letters   bytes\n");
        foreach (var (ecc, numeric, alpha, bytes) in QrBuilder.Capacity(version)) sb.AppendLine($"{ecc,-9} {numeric,7} {alpha,8} {bytes,7}");
        _capacity!.Text = sb.ToString().TrimEnd();
    }

    private static void Put(TextBox? box, string text)
    {
        if (box is not null && !box.IsKeyboardFocusWithin && box.Text != text) box.Text = text;
    }
}

/// <summary>How the code is built: which modules are for what, how the codewords are shared out in blocks, and what each mask scored.</summary>
internal sealed class QrAnatomyInspector(QrLabSession session) : LiveInspector(session)
{
    private Image?     _picture;
    private CheckBox?  _byBlock;
    private TextBlock? _facts;
    private WrapPanel? _legend;

    public override string Id => "anatomy";

    public override string Title => "Anatomy";

    public override string IconKey => "DataModel";

    protected override UIElement Create()
    {
        _picture = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(8), MaxHeight = 360, HorizontalAlignment = HorizontalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(_picture, BitmapScalingMode.NearestNeighbor);
        _byBlock = LabUi.Check("Color the codewords by block", false, _ => Refresh(), "Each block of data and correction codewords gets a color: you can see how the blocks are woven through the code");
        _legend  = new WrapPanel { Margin = new Thickness(8, 0, 8, 4) };
        _facts   = LabUi.Label(string.Empty, mono: true);
        _facts.Margin = new Thickness(8, 0, 8, 8);

        var panel = new StackPanel();
        panel.Children.Add(_picture);
        panel.Children.Add(LabUi.Bar(_byBlock));
        panel.Children.Add(_legend);
        panel.Children.Add(_facts);
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    protected override void Refresh()
    {
        if (_picture is null || _facts is null || _legend is null) return;

        if (session.Build.Code is not { } code)
        {
            _picture.Source = null;
            _legend.Children.Clear();
            _facts.Text = session.Build.Problem ?? "There is no code yet.";
            return;
        }

        int scale = Math.Max(2, 340 / (code.Size + 2));
        _picture.Source = QrPicture.Anatomy(code, scale, _byBlock?.IsChecked == true);

        _legend.Children.Clear();
        foreach (var (role, name, dark, light) in QrPicture.Legend)
        {
            var swatch = new Border { Width = 12, Height = 12, Background = QrPicture.BrushOf(dark), BorderBrush = QrPicture.BrushOf(light), BorderThickness = new Thickness(2), Margin = new Thickness(0, 0, 4, 0) };
            var item   = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 12, 1) };
            item.Children.Add(swatch);
            item.Children.Add(LabUi.Label(name, muted: true));
            _legend.Children.Add(item);
        }

        int blocks   = QrCode.BlockCount(code.Version, code.Ecc);
        int eccEach  = QrCode.EccCodewordsPerBlock(code.Version, code.Ecc);
        int data     = QrCode.DataCodewords(code.Version, code.Ecc);
        int total    = QrCode.RawModules(code.Version) / 8;
        int remainder = QrCode.RawModules(code.Version) % 8;
        var sb = new StringBuilder();
        sb.AppendLine($"Version {code.Version}: {code.Size} x {code.Size} modules");
        sb.AppendLine($"Codewords: {total} = {data} data + {total - data} correction");
        sb.AppendLine($"Blocks: {blocks}, with {eccEach} correction codewords each");
        sb.AppendLine($"A block can put right {eccEach / 2} wrong codewords ({QrCode.Correctable(code.Version, code.Ecc)} in all, if they are spread out)");
        sb.AppendLine($"Data used: {code.DataBits:N0} of {code.CapacityBits:N0} bits ({(double)code.DataBits / code.CapacityBits:P0})");
        if (remainder > 0) sb.AppendLine($"Remainder: {remainder} bit(s) that carry nothing");
        var align = QrCode.AlignmentPositions(code.Version);
        sb.AppendLine($"Alignment pattern centers at: {(align.Count == 0 ? "none" : string.Join(", ", align))}");
        sb.AppendLine($"Format bits: {Convert.ToString(QrCode.FormatBits(code.Ecc, code.Mask), 2).PadLeft(15, '0')} (level {code.Ecc}, mask {code.Mask})");
        if (code.Version >= 7) sb.AppendLine($"Version bits: {Convert.ToString(QrCode.VersionBits(code.Version), 2).PadLeft(18, '0')}");
        if (code.Penalties.Count == 8)
        {
            sb.AppendLine("Penalty by mask (the lowest is taken when the mask is Auto):");
            for (int m = 0; m < 8; m++) sb.AppendLine($"  {m}: {code.Penalties[m],6}{(m == code.Mask ? "  <- used" : string.Empty)}");
        }
        else if (code.Penalties.Count == 1)
            sb.AppendLine($"Penalty of mask {code.Mask}: {code.Penalties[0]}");

        _facts.Text = sb.ToString().TrimEnd();
    }
}

/// <summary>Takes the code apart again: reads it, spoils it and reads what is left, reads a grid typed in or a picture.</summary>
internal sealed class QrReadInspector(QrLabSession session) : LiveInspector(session)
{
    private Slider?    _damage;
    private TextBlock? _damageLabel;
    private TextBox?   _grid;
    private TextBox?   _result;

    public override string Id => "read";

    public override string Title => "Read";

    public override string IconKey => "DataSearching";

    protected override UIElement Create()
    {
        _damage = new Slider { Minimum = 1, Maximum = 40, Value = 3, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 140, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        _damageLabel = LabUi.Label("3 codewords", mono: true);
        _damage.ValueChanged += (_, _) => _damageLabel.Text = $"{(int)_damage.Value} codeword(s)";
        _grid = LabUi.Memo(string.Empty, _ => { }, wrap: false);
        _grid.MinHeight = 80;
        _grid.ToolTip   = "A grid of modules: 1 or # for dark, 0 or . for light, one row to a line; or the half-block text the Text pane gives";
        _result = LabUi.Output(wrap: true);

        var panel = new StackPanel();
        panel.Children.Add(LabUi.Heading("Read this code"));
        panel.Children.Add(LabUi.Bar(LabUi.Button("Read", session.ReadOwn, "Read the code as it is with the decoder here"), LabUi.Button("Use what was read", session.UseReading, "Make the text that was read what the code says")));
        panel.Children.Add(LabUi.Heading("Spoil it first"));
        panel.Children.Add(LabUi.Bar(LabUi.Label("Spoil", muted: true), _damage, _damageLabel, LabUi.Button("Spoil and read", () => session.Spoil((int)_damage.Value),
                                                                                                          "Turn over some modules in that many codewords, chosen at random, and read what is left. The code survives while no block has more wrong codewords than it can put right.")));
        panel.Children.Add(LabUi.Heading("Read something else"));
        panel.Children.Add(LabUi.Bar(LabUi.Button("A picture file...", session.ChooseImage, "A picture of a code, straight on and sharp"), LabUi.Button("The clipboard's picture", session.ReadClipboardImage, "A screenshot of a code, say")));
        panel.Children.Add(_grid);
        panel.Children.Add(LabUi.Bar(LabUi.Button("Read the grid", () => session.ReadMatrixText(_grid.Text), "Read the grid typed above"), LabUi.Button("This code's grid", () =>
                                                                                                                                                                  {
                                                                                                                                                                      if (session.Build.Code is { } c) _grid.Text = QrRender.Matrix(c);
                                                                                                                                                                  }, "Fill the box with the grid of the code, to spoil by hand")));
        panel.Children.Add(LabUi.Heading("What came of it"));
        var layout = LabUi.Layout(new ScrollViewer { Content = panel, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, _result);
        return layout;
    }

    protected override void Refresh()
    {
        if (_result is null || _damage is null) return;

        if (session.Build.Code is { } code) _damage.Maximum = Math.Max(2, QrCode.RawModules(code.Version) / 8);
        _result.Text = session.Reading is { } reading ? QrLabSession.Describe(reading) : "Nothing has been read yet.";
    }
}

/// <summary>The code as text: what it says, the SVG, text blocks, or the grid of 1s and 0s.</summary>
internal sealed class QrTextInspector(QrLabSession session) : LiveInspector(session)
{
    private ComboBox? _format;
    private TextBox?  _text;

    public override string Id => "text";

    public override string Title => "Text";

    public override string IconKey => "Document";

    public override bool VisibleByDefault => false;

    protected override UIElement Create()
    {
        _format = LabUi.Combo(["What it says", "SVG", "Text blocks", "Grid of 1 and 0"], "What it says", _ => Refresh(), 140);
        _text   = LabUi.Output();
        return LabUi.Layout(LabUi.Bar(_format, LabUi.Button("Copy", () => LabUi.Copy(_text.Text), "Put the text on the clipboard")), _text);
    }

    protected override void Refresh()
    {
        if (_text is null || _format is null) return;

        var code = session.Build.Code;
        _text.Text = (_format.SelectedItem as string) switch
                     {
                         "SVG"             => code is null ? session.Build.Problem : QrRender.Svg(code, session.Doc.Border, session.Doc.Colors),
                         "Text blocks"     => code is null ? session.Build.Problem : QrRender.Blocks(code),
                         "Grid of 1 and 0" => code is null ? session.Build.Problem : QrRender.Matrix(code),
                         _                 => session.Build.Payload
                     };
    }
}

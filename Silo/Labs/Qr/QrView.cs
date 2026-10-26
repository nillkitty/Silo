using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Silo.Labs.Qr;

/// <summary>Pictures of codes for the screen.</summary>
internal static class QrPicture
{
    public static BitmapSource Bitmap(bool[,] modules, int scale, int border, QrColors colors)
    {
        var (pixels, size) = QrRender.Pixels(modules, scale, border, colors);
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static readonly Color[] BlockColors =
    [
        Color.FromRgb(0x3B, 0x82, 0xD6), Color.FromRgb(0xD9, 0x7A, 0x1E), Color.FromRgb(0x3F, 0xA9, 0x5B), Color.FromRgb(0xB0, 0x5D, 0xD6), Color.FromRgb(0xE5, 0x48, 0x4D),
        Color.FromRgb(0x2D, 0xB8, 0xC4), Color.FromRgb(0x9A, 0x8A, 0x1E), Color.FromRgb(0xD6, 0x5D, 0x9E)
    ];

    /// <summary>What each kind of module is, with its color (dark shade for a dark module, a pale one for a light one).</summary>
    public static IReadOnlyList<(QrRole Role, string Name, Color Dark, Color Light)> Legend { get; } =
    [
        (QrRole.Finder, "Finder (the three big squares)", Color.FromRgb(0x1F, 0x4E, 0x9C), Color.FromRgb(0xB9, 0xD0, 0xF0)),
        (QrRole.Separator, "Separator", Color.FromRgb(0x1F, 0x4E, 0x9C), Color.FromRgb(0xE2, 0xEC, 0xF9)),
        (QrRole.Timing, "Timing (the dotted lines)", Color.FromRgb(0xA8, 0x6B, 0x00), Color.FromRgb(0xF4, 0xDD, 0xAA)),
        (QrRole.Alignment, "Alignment", Color.FromRgb(0x7A, 0x32, 0x9E), Color.FromRgb(0xE3, 0xC9, 0xF0)),
        (QrRole.Format, "Format (level and mask)", Color.FromRgb(0xB3, 0x1F, 0x24), Color.FromRgb(0xF3, 0xC4, 0xC6)),
        (QrRole.Version, "Version", Color.FromRgb(0xC2, 0x4E, 0x00), Color.FromRgb(0xF6, 0xCF, 0xB0)),
        (QrRole.DarkModule, "The one dark module", Color.FromRgb(0x00, 0x00, 0x00), Color.FromRgb(0x00, 0x00, 0x00)),
        (QrRole.Data, "Data", Color.FromRgb(0x33, 0x33, 0x33), Color.FromRgb(0xF2, 0xF2, 0xF2)),
        (QrRole.Ecc, "Error correction", Color.FromRgb(0x1D, 0x6B, 0x34), Color.FromRgb(0xCB, 0xE8, 0xD2)),
        (QrRole.Remainder, "Remainder bits", Color.FromRgb(0x77, 0x77, 0x77), Color.FromRgb(0xE4, 0xE4, 0xE4))
    ];

    /// <summary>The code with each module colored for what it is for; with <paramref name="byBlock" />, the data and the correction codewords are colored by the block they belong to.</summary>
    public static BitmapSource Anatomy(QrCode code, int scale, bool byBlock, int border = 1)
    {
        int n    = code.Size;
        int size = (n + border * 2) * scale;
        var data = new byte[size * size * 4];
        Array.Fill(data, (byte)255);

        var blockOf = new Dictionary<(int, int), int>();
        if (byBlock)
        {
            var places = QrCode.DataPlaces(code.Version);
            var blocks = QrDamage.BlockOfCodewords(code.Version, code.Ecc);
            for (int i = 0; i < places.Count; i++)
            {
                int cw = i / 8;
                if (cw < blocks.Count) blockOf[places[i]] = blocks[cw];
            }
        }

        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            var role  = code.RoleAt(x, y);
            var entry = Legend.First(l => l.Role == role);
            bool dark = code[x, y];
            Color color = dark ? entry.Dark : entry.Light;
            if (byBlock && blockOf.TryGetValue((x, y), out int block))
            {
                var baseColor = BlockColors[block % BlockColors.Length];
                color = dark ? baseColor : Color.FromRgb((byte)(255 - (255 - baseColor.R) / 4), (byte)(255 - (255 - baseColor.G) / 4), (byte)(255 - (255 - baseColor.B) / 4));
            }

            for (int dy = 0; dy < scale; dy++)
            for (int dx = 0; dx < scale; dx++)
            {
                int at = (((y + border) * scale + dy) * size + (x + border) * scale + dx) * 4;
                data[at]     = color.B;
                data[at + 1] = color.G;
                data[at + 2] = color.R;
                data[at + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, data, size * 4);
        bitmap.Freeze();
        return bitmap;
    }

    public static Brush BrushOf(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>
///     The main view of the QR Code Lab: on the left what the code says (its kind and the things to fill in for it), on the right the code itself, as big as there is room for, and under it what
///     making it came to.
/// </summary>
internal sealed class QrView : Grid
{
    private readonly QrLabSession _session;
    private readonly TextBox      _name;
    private readonly ComboBox     _kind;
    private readonly StackPanel   _form = new();
    private readonly Image        _image;
    private readonly TextBlock    _summary;
    private readonly TextBlock    _warnings;
    private readonly TextBlock    _spoiledNote;
    private readonly Button       _showCode;
    private readonly Border       _frame;
    private bool                  _loading;
    private QrPayloadKind?        _formKind;

    public QrView(QrLabSession session)
    {
        _session = session;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star), MinWidth = 220 });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star), MinWidth = 220 });

        // ---- what it says ---------------------------------------------------------------------------
        _name = LabUi.Line(string.Empty, v =>
                                         {
                                             if (!_loading) _session.Edit(d => d.Name = v.Trim());
                                         }, 160, "What the code is called in the Silo");
        _kind = LabUi.Combo(Enum.GetValues<QrPayloadKind>().Select(QrPayloads.TitleOf), QrPayloads.TitleOf(QrPayloadKind.Text), title =>
                                                                                                                                {
                                                                                                                                    if (_loading) return;

                                                                                                                                    var kind = Enum.GetValues<QrPayloadKind>().First(k => QrPayloads.TitleOf(k) == title);
                                                                                                                                    _session.SetKind(kind);
                                                                                                                                }, 160);
        _kind.ToolTip = "What the code says: plain text, or something a phone knows what to do with";

        var head = new StackPanel { Margin = new Thickness(6) };
        head.Children.Add(LabUi.Form([("Name", _name), ("Says", _kind)], 60));
        var scroll = new ScrollViewer { Content = _form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var left = LabUi.Layout(head, scroll);
        SetColumn(left, 0);
        Children.Add(left);

        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch };
        splitter.SetResourceReference(BackgroundProperty, "Theme.Border");
        SetColumn(splitter, 1);
        Children.Add(splitter);

        // ---- the code -------------------------------------------------------------------------------
        _image = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(10) };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
        _frame = new Border { Child = _image, BorderThickness = new Thickness(1), Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _frame.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");

        _summary     = LabUi.Label(string.Empty, wrap: true);
        _warnings    = LabUi.Label(string.Empty, wrap: true);
        _warnings.Foreground = LabUi.Amber;
        _spoiledNote = LabUi.Label("This is the spoiled copy that was read.", wrap: true);
        _spoiledNote.Foreground = LabUi.Red;
        _showCode = LabUi.Button("Show the code", _session.ClearSpoiled, "Go back to the code as it was made");

        var spoiledRow = new WrapPanel { Margin = new Thickness(8, 0, 8, 0) };
        spoiledRow.Children.Add(_spoiledNote);
        spoiledRow.Children.Add(_showCode);
        var under = new StackPanel { Margin = new Thickness(8, 2, 8, 6) };
        under.Children.Add(_summary);
        under.Children.Add(_warnings);
        under.Children.Add(spoiledRow);
        spoiledRow.Visibility = Visibility.Collapsed;

        var right = LabUi.Layout(null, _frame, under);
        SetColumn(right, 2);
        Children.Add(right);
        _spoiledRow = spoiledRow;

        this.SetResourceReference(BackgroundProperty, "Theme.Background");
        _session.Changed += (_, _) => Draw();
        _session.FieldFocusRequested += key =>
                                        {
                                            if (_fieldControls.TryGetValue(key, out var box)) box.Focus();
                                        };
        _session.Loaded  += (_, _) => LoadAll();
        LoadAll();
    }

    private readonly WrapPanel _spoiledRow;

    private void LoadAll()
    {
        _loading = true;
        try
        {
            _name.Text = _session.Doc.Name;
            _kind.SelectedItem = QrPayloads.TitleOf(_session.Doc.Kind);
        }
        finally
        {
            _loading = false;
        }

        BuildForm();
        Draw();
    }

    private readonly Dictionary<string, UIElement> _fieldControls = new();

    /// <summary>The boxes for the fields of the kind the code says.</summary>
    private void BuildForm()
    {
        var doc = _session.Doc;
        _formKind = doc.Kind;
        _form.Children.Clear();
        _fieldControls.Clear();
        var rows = new List<(string, UIElement)>();
        _loading = true;
        try
        {
            foreach (var field in QrPayloads.FieldsOf(doc.Kind))
            {
                string value = doc.Fields.TryGetValue(field.Key, out var v) ? v : field.Default;
                string key   = field.Key;
                UIElement control;
                if (field.Choices is not null)
                    control = LabUi.Combo(field.Choices, field.Choices.Contains(value) ? value : field.Default, picked => _session.SetField(key, picked), 120);
                else if (field.Multiline)
                {
                    var memo = LabUi.Memo(value, text =>
                                                 {
                                                     if (!_loading) _session.SetField(key, text);
                                                 }, wrap: true);
                    memo.MinHeight = 80;
                    memo.Paper();
                    memo.ToolTip   = field.Hint;
                    control        = memo;
                }
                else
                {
                    var line = LabUi.Line(value, text =>
                                                 {
                                                     if (!_loading) _session.SetField(key, text);
                                                 }, 160, field.Hint.Length > 0 ? field.Hint : null);
                    control = line.Paper();
                }

                _fieldControls[key]      =  control;
                control.GotKeyboardFocus += (_, _) => _session.NoteField(key);
                rows.Add((field.Title + (field.Required ? " *" : string.Empty), control));
            }
        }
        finally
        {
            _loading = false;
        }

        _form.Children.Add(LabUi.Form(rows, 120));
        if (doc.Kind != QrPayloadKind.Text && QrPayloads.FieldsOf(doc.Kind).Any(f => f.Required))
            _form.Children.Add(LabUi.Label("* is needed", muted: true).Also(l => l.Margin = new Thickness(10, 0, 0, 4)));
    }

    private void Draw()
    {
        var doc   = _session.Doc;
        var build = _session.Build;
        if (_formKind != doc.Kind) BuildForm();

        if (build.Code is { } code)
        {
            var modules = _session.Spoiled ?? code.ToMatrix();
            _image.Source = QrPicture.Bitmap(modules, Math.Clamp(doc.Scale, 1, 64), doc.Border, doc.Colors);
            _frame.Background = new SolidColorBrush(Color.FromRgb(doc.Colors.LightR, doc.Colors.LightG, doc.Colors.LightB));
            _frame.Visibility = Visibility.Visible;
            _summary.Text = _session.Summary();
            _summary.Foreground = build.Check is not null && !build.CheckPassed ? LabUi.Red : LabUi.Green;
        }
        else
        {
            _image.Source     = null;
            _frame.Visibility = Visibility.Collapsed;
            _summary.Text     = build.Problem ?? string.Empty;
            _summary.Foreground = build.Problem is null ? LabUi.Gray : LabUi.Red;
        }

        _warnings.Text         = string.Join('\n', build.Warnings.Select(w => "! " + w));
        _spoiledRow.Visibility = _session.Spoiled is null ? Visibility.Collapsed : Visibility.Visible;
    }
}

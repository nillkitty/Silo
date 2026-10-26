using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;
using Newtonsoft.Json;
using Silo.Providers.Format;

namespace Silo.Labs.Images;

/// <summary>One Image Lab window: an image with its layers, selection and history, and the tool in hand.</summary>
public sealed partial class ImageLabSession : LabSession<ImageData>, ILabLive
{
    private static readonly LabSaved<ImageData> Saved = new("image");

    private readonly Debouncer          _notify;
    private readonly List<LabInspector> _inspectors;
    private readonly List<Rgba>         _recent = [];
    private          ImageDocument      _doc    = ImageDocument.Blank(8, 8, Rgba.Transparent);
    private          int                _savedRevision;
    private          bool               _started;
    private          ImageData?         _data;
    private          int                _dataRevision = -1;

    public ImageLabSession(ImageLab lab) : base(lab)
    {
        _notify = new Debouncer(TimeSpan.FromMilliseconds(60), Notify);
        MakeParameters();
        _doc.Changed += OnDocChanged;
        _inspectors = [new ImageColorsInspector(this).OwnedBy(LabOwner.OfAspect("colors")),
                       new ImageBrushInspector(this).OwnedBy(LabOwner.OfAspect("brush")),
                       new ImageLayersInspector(this).OwnedBy(LabOwner.OfAspect("layers")),
                       new ImageFiltersInspector(this).OwnedBy(LabOwner.OfAspect("filters"))];
    }

    private ImageLab Lab_ => (ImageLab)LabOf;

    /// <summary>The image being worked on.</summary>
    public ImageDocument Doc => _doc;

    /// <summary>The colors last painted with, newest first.</summary>
    public IReadOnlyList<Rgba> RecentColors => _recent;

    /// <summary>Raised when the layers, the selection or the colors changed (for the panes).</summary>
    public event EventHandler? Changed;

    /// <summary>A different image was opened: the view starts afresh.</summary>
    public event EventHandler? Loaded;

    /// <summary>A part of the picture changed (an empty rectangle: all of it); the view draws it again.</summary>
    internal event Action<IntRect>? Painted;

    /// <summary>The selection outline, the box or the path being made changed.</summary>
    internal event EventHandler? OverlayChanged;

    /// <summary>The Text tool was clicked at a point: the view asks for the text.</summary>
    internal event Action<Pt>? TextRequested;

    /// <summary>The view is to zoom: "in", "out", "fit" or "100".</summary>
    internal event Action<string>? ZoomRequested;

    public bool IsDirty => _doc.Revision != _savedRevision;

    public override ImageData? CurrentValue
    {
        get
        {
            if (_data is null || _dataRevision != _doc.Revision)
            {
                _data         = _doc.ToData();
                _dataRevision = _doc.Revision;
            }

            return _data;
        }
    }

    public override string ValueText => string.IsNullOrWhiteSpace(_doc.Name) ? $"{_doc.Width} x {_doc.Height}" : _doc.Name;

    public override string Title => $"{LabOf.Name} - {ValueText}{(IsDirty ? " *" : string.Empty)}";

    /// <summary>3: the panes belong to aspects (the model); windows saved with the first arrangement start afresh.</summary>
    public override int LayoutVersion => 3;

    public override IReadOnlyList<LabInspector> Inspectors => _inspectors;

    protected override bool RecentOnOpen => false;

    public override bool LibraryOpensWithWindow => true;

    public void Say(string text) => Status = text;

    internal void RaiseOverlay() => OverlayChanged?.Invoke(this, EventArgs.Empty);

    internal void RaisePainted(IntRect rect) => Painted?.Invoke(rect);

    internal void RequestText(Pt at) => TextRequested?.Invoke(at);

    internal void RequestZoom(string how) => ZoomRequested?.Invoke(how);

    /// <summary>How big a pixel of the picture is on the screen now (set by the view); handles and hit tests are sized by it.</summary>
    internal double ViewScale { get; set; } = 1;

    // ---- parameters ------------------------------------------------------------------------------------------

    private LabParameterSet _colorsSet = null!, _brushSet = null!, _selectionSet = null!, _textSet = null!, _transformSet = null!, _filtersSet = null!, _gradientSet = null!, _bezierSet = null!, _moveSet = null!;

    private void MakeParameters()
    {
        string lab = LabOf.Id;
        _colorsSet = LabParameterSet.For(lab, "aspect:colors", "FG / BG Brush",
                                         [new LabParameter("fg", "Foreground", LabParameterKind.Text, "#000000", "The color the left button paints with (#RRGGBB, or #RRGGBBAA with transparency).", "Colors", hint: "#000000"),
                                          new LabParameter("bg", "Background", LabParameterKind.Text, "#FFFFFF", "The color the right button paints with, and the second color of gradients and fills.", "Colors", hint: "#FFFFFF")]);
        _brushSet = LabParameterSet.For(lab, "aspect:brush", "Brush Size / Pattern",
                                        [new LabParameter("size", "Size", LabParameterKind.Text, "12", "Across the tip, in pixels (1 to 500). The Brush, the Eraser and the stroke of a Bezier path use it.", "Tip", hint: "12"),
                                         new LabParameter("pattern", "Pattern", LabParameterKind.Choice, nameof(BrushPattern.Round), "The shape and texture of the tip.", "Tip", BrushSettings.PatternNames),
                                         new LabParameter("opacity", "Opacity (%)", LabParameterKind.Text, "100", "How much of the color each dab of the tip lays down (1 to 100).", "Paint", hint: "100"),
                                         new LabParameter("spacing", "Spacing (%)", LabParameterKind.Text, "20", "The distance between dabs, as a percent of the size (5 to 200). Small is smooth; large shows the dabs.", "Paint", hint: "20")]);
        _selectionSet = LabParameterSet.For(lab, "aspect:selection", "Pointer / Selection",
                                            [new LabParameter("shape", "Marquee shape", LabParameterKind.Choice, "Rectangle", "What the Marquee Tool draws.", "Marquee", ["Rectangle", "Ellipse"]),
                                             new LabParameter("lasso", "Lasso style", LabParameterKind.Choice, "Freehand", "Freehand follows the pointer; Polygon is made of straight pieces between clicks (double-click or Enter closes it).", "Lasso", ["Freehand", "Polygon"]),
                                             new LabParameter("mode", "New selection", LabParameterKind.Choice, nameof(SelectionMode.Replace), "How a new selection combines with the one there is.", "Combining", Enum.GetNames<SelectionMode>()),
                                             new LabParameter("feather", "Feather (pixels)", LabParameterKind.Text, "0", "Softens the edge of each new selection.", "Combining", hint: "0")]);
        _textSet = LabParameterSet.For(lab, "aspect:text", "Text Settings",
                                       [new LabParameter("font", "Font", LabParameterKind.Choice, "Segoe UI", "The typeface.", "Type", ImageText.Fonts),
                                        new LabParameter("size", "Size (pixels)", LabParameterKind.Text, "32", "Height of the type, in pixels.", "Type", hint: "32"),
                                        new LabParameter("bold", "Bold", LabParameterKind.Bool, false, null, "Type"),
                                        new LabParameter("italic", "Italic", LabParameterKind.Bool, false, null, "Type"),
                                        new LabParameter("underline", "Underline", LabParameterKind.Bool, false, null, "Type"),
                                        new LabParameter("align", "Alignment", LabParameterKind.Choice, "Left", "How the lines of several-line text line up.", "Type", ["Left", "Center", "Right"]),
                                        new LabParameter("ownLayer", "On its own layer", LabParameterKind.Bool, true, "Put each piece of text on a new layer, so it can be moved on its own. The text is pixels all the same (it cannot be edited again).", "Placing")]);
        _transformSet = new LabParameterSet(lab, TransformParameters(), "aspect:transform", false, "Rotate / Scale / Skew");
        _filtersSet = LabParameterSet.For(lab, "aspect:filters", "Filters",
                                          [new LabParameter("brightness", "Brightness (-100 to 100)", LabParameterKind.Text, "0", null, "Brightness and contrast", hint: "0"),
                                           new LabParameter("contrast", "Contrast (-100 to 100)", LabParameterKind.Text, "0", null, "Brightness and contrast", hint: "0"),
                                           new LabParameter("blur", "Blur radius (pixels)", LabParameterKind.Text, "4", null, "Blur and sharpen", hint: "4"),
                                           new LabParameter("sharpRadius", "Sharpen radius (pixels)", LabParameterKind.Text, "2", null, "Blur and sharpen", hint: "2"),
                                           new LabParameter("sharpAmount", "Sharpen amount (0 to 5)", LabParameterKind.Text, "1", null, "Blur and sharpen", hint: "1"),
                                           new LabParameter("pixel", "Pixelate block (pixels)", LabParameterKind.Text, "8", null, "Pixelate and threshold", hint: "8"),
                                           new LabParameter("threshold", "Threshold level (0 to 255)", LabParameterKind.Text, "128", null, "Pixelate and threshold", hint: "128")]);
        _gradientSet = LabParameterSet.For(lab, "tool:gradient", "Gradient Tool",
                                           [new LabParameter("kind", "Kind", LabParameterKind.Choice, nameof(GradientKind.Linear), "The shape of the gradient.", "Gradient", Enum.GetNames<GradientKind>()),
                                            new LabParameter("colors", "Colors", LabParameterKind.Choice, "Foreground to Background", "Which colors it runs between.", "Gradient",
                                                             ["Foreground to Background", "Background to Foreground", "Foreground to Transparent", "Transparent to Foreground"]),
                                            new LabParameter("opacity", "Opacity (%)", LabParameterKind.Text, "100", null, "Gradient", hint: "100")]);
        _bezierSet = LabParameterSet.For(lab, "tool:bezier", "Bezier Tool",
                                         [new LabParameter("stroke", "Stroke the path", LabParameterKind.Bool, true, "Draw along the path with the brush and the foreground color.", "Path"),
                                          new LabParameter("fill", "Fill the path", LabParameterKind.Bool, false, "Fill the inside of the path with the background color (the path is closed first).", "Path"),
                                          new LabParameter("close", "Close the path", LabParameterKind.Bool, false, "Join the last point to the first. Clicking the first point closes it too.", "Path")]);
        _moveSet = LabParameterSet.For(lab, "tool:selection", "Selection Tool",
                                       [new LabParameter("pick", "Click picks a layer", LabParameterKind.Bool, true, "Clicking on the picture makes the layer under the pointer the active one. Off: dragging always moves the active layer.", "Pointer")]);

        foreach (var set in new[] { _colorsSet, _brushSet, _selectionSet, _textSet, _filtersSet, _gradientSet, _bezierSet, _moveSet })
            set.Changed += _ => RaiseChangedSoon();

        _transformSet.Changed += OnTransformParameterChanged;
    }

    private void RaiseChangedSoon()
    {
        Changed?.Invoke(this, EventArgs.Empty);
        RaiseOverlay();
    }

    private static int IntOf(LabParameter p, int fallback, int min, int max) => int.TryParse(p.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? Math.Clamp(n, min, max) : fallback;

    private static double DoubleOf(LabParameter p, double fallback, double min, double max) => double.TryParse(p.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? Math.Clamp(n, min, max) : fallback;

    public Rgba Foreground
    {
        get => Rgba.ParseOr(_colorsSet["fg"].Text, Rgba.Black);
        set => _colorsSet["fg"].Value = value.ToHex();
    }

    public Rgba Background
    {
        get => Rgba.ParseOr(_colorsSet["bg"].Text, Rgba.White);
        set => _colorsSet["bg"].Value = value.ToHex();
    }

    public BrushSettings Brush => new BrushSettings(IntOf(_brushSet["size"], 12, 1, 500), Enum.TryParse<BrushPattern>(_brushSet["pattern"].Text, out var pattern) ? pattern : BrushPattern.Round,
                                                    IntOf(_brushSet["opacity"], 100, 1, 100) / 100.0, IntOf(_brushSet["spacing"], 20, 5, 200) / 100.0).Normalized();

    public void SetBrush(int? size = null, BrushPattern? pattern = null, int? opacityPercent = null, int? spacingPercent = null)
    {
        if (size is { } s) _brushSet["size"].Value = Math.Clamp(s, 1, 500).ToString(CultureInfo.InvariantCulture);
        if (pattern is { } p) _brushSet["pattern"].Value = p.ToString();
        if (opacityPercent is { } o) _brushSet["opacity"].Value = Math.Clamp(o, 1, 100).ToString(CultureInfo.InvariantCulture);
        if (spacingPercent is { } sp) _brushSet["spacing"].Value = Math.Clamp(sp, 5, 200).ToString(CultureInfo.InvariantCulture);
    }

    internal LabParameterSet FiltersSet => _filtersSet;

    internal void Remember(Rgba color)
    {
        color = color.WithAlpha(255);
        _recent.RemoveAll(c => c == color);
        _recent.Insert(0, color);
        while (_recent.Count > 16) _recent.RemoveAt(_recent.Count - 1);
    }

    public void SwapColors()
    {
        var f = Foreground;
        var b = Background;
        Foreground = b;
        Background = f;
    }

    public void ResetColors()
    {
        Foreground = Rgba.Black;
        Background = Rgba.White;
    }

    // ---- the image: opening, replacing, changing -------------------------------------------------------------------

    private void OnDocChanged(IntRect rect)
    {
        RaisePainted(rect);
        _notify.Poke();
    }

    /// <summary>Layers, selection or history changed: the panes, the title and the actions catch up a moment after the last change.</summary>
    private void Notify()
    {
        if (_doc.ActiveLayer is { } layer) SetActivePart("l:" + layer.Id);

        RaiseValueChanged();
        RaiseActionsChanged();
        Changed?.Invoke(this, EventArgs.Empty);
        RaiseOverlay();
        AfterDocChanged();
    }

    protected override Task OpenValueAsync(ImageData value)
    {
        OpenData(value, false);
        return Task.CompletedTask;
    }

    protected override Task OpenDefaultAsync()
    {
        if (!_started) Replace(ImageDocument.FromData(Lab_.Blank()), false);
        return Task.CompletedTask;
    }

    protected override void OnAttached()
    {
        if (!_started) _ = OpenDefaultAsync();

        if (ActiveTool is null && Tools.FirstOrDefault(t => t.Id == "brush") is { } brush) ActivateTool(brush);
    }

    protected override void OnDispose()
    {
        _notify.Cancel();
        CancelPending();
        _doc.Changed -= OnDocChanged;
    }

    private void OpenData(ImageData data, bool dirty)
    {
        if (!string.IsNullOrEmpty(data.ImportPath))
        {
            ImportAsImage(data.ImportPath);
            return;
        }

        Replace(data.IsEmpty ? ImageDocument.FromData(Lab_.Blank()) : ImageDocument.FromData(data), dirty);
    }

    public void Replace(ImageDocument doc, bool dirty)
    {
        CancelPending();
        _doc.Changed -= OnDocChanged;
        _doc         =  doc;
        _doc.Changed += OnDocChanged;
        _started     =  true;
        _data        =  null;
        _savedRevision = dirty ? -1 : _doc.Revision;
        ResetTransformState();
        Loaded?.Invoke(this, EventArgs.Empty);
        RaiseValueChanged();
        RaiseActionsChanged();
        Changed?.Invoke(this, EventArgs.Empty);
        RaisePainted(IntRect.Empty);
        RaiseOverlay();
        if (_doc.ActiveLayer is { } layer) SetActivePart("l:" + layer.Id);

        Status = $"{_doc.Width} x {_doc.Height}, {_doc.Layers.Count} layer(s)";
    }

    private bool ConfirmDiscard(string then)
    {
        return !IsDirty || LabUi.Confirm($"This image has changes that are not kept in the Silo. {then} anyway?", "Image Lab");
    }

    // ---- files -------------------------------------------------------------------------------------------------------

    private static string Stem(string path) => Path.GetFileNameWithoutExtension(path);

    private void ImportAsImage(string path)
    {
        try
        {
            var picture = ImageCodec.Load(path);
            Replace(ImageDocument.FromRaster(picture, Stem(path)), true);
            Say($"Opened {Path.GetFileName(path)} ({picture.Width} x {picture.Height})");
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or FileFormatException)
        {
            Say("That picture could not be opened: " + ex.Message);
        }
    }

    private void OpenFile()
    {
        if (!ConfirmDiscard("Open another picture")) return;

        var dialog = new OpenFileDialog { Title = "Open a picture", Filter = ImageFormat.OpenFilter };
        if (dialog.ShowDialog() == true) ImportAsImage(dialog.FileName);
    }

    private void ImportLayer()
    {
        var dialog = new OpenFileDialog { Title = "Bring a picture in as a layer", Filter = ImageFormat.OpenFilter };
        if (dialog.ShowDialog() != true) return;

        try
        {
            AddPicture(ImageCodec.Load(dialog.FileName), Stem(dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or FileFormatException)
        {
            Say("That picture could not be opened: " + ex.Message);
        }
    }

    /// <summary>Adds a picture as a layer, in the middle of the canvas (cut off where it is bigger).</summary>
    private void AddPicture(Raster picture, string name)
    {
        var layer = new Raster(_doc.Width, _doc.Height);
        layer.Paste(picture, (_doc.Width - picture.Width) / 2, (_doc.Height - picture.Height) / 2);
        _doc.AddLayer(name, pixels: layer);
        Say($"Added {name} as a layer" + (picture.Width > _doc.Width || picture.Height > _doc.Height ? " (the part outside the canvas is cut off)" : string.Empty));
    }

    private void ExportAs(ImageFormat format)
    {
        var dialog = new SaveFileDialog { Title = $"Write the image as {format.Name}", FileName = FileName(format.Extension), Filter = format.Filter };
        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllBytes(dialog.FileName, ImageCodec.Encode(_doc.Composite(), format, Lab_.FlattenColor, Lab_.JpegQuality));
            Say($"Wrote {Path.GetFileName(dialog.FileName)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Say("Could not write the file: " + ex.Message);
        }
    }

    /// <summary>Writes the image to a file; the format is the extension's. Used by the terminal.</summary>
    internal string ExportTo(string path)
    {
        var format = ImageFormat.ForFile(path)
                     ?? throw new InvalidOperationException("The file name must end in " + string.Join(", ", ImageFormat.All.SelectMany(f => f.File.DottedExtensions())) + ".");
        File.WriteAllBytes(path, ImageCodec.Encode(_doc.Composite(), format, Lab_.FlattenColor, Lab_.JpegQuality));
        return $"Wrote {path} ({new FileInfo(path).Length:N0} bytes)";
    }

    private string FileName(string extension)
    {
        string name = string.IsNullOrWhiteSpace(_doc.Name) ? "image" : _doc.Name;
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name + extension;
    }

    private void SaveImage()
    {
        string name = _doc.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = LabUi.Ask("Save image", "What is the image called?", "Untitled") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name)) return;
        }

        SaveAs(name.Trim());
    }

    private void SaveImageAs()
    {
        string? name = LabUi.Ask("Save image as", "What is the image called?", string.IsNullOrWhiteSpace(_doc.Name) ? "Untitled" : _doc.Name);
        if (!string.IsNullOrWhiteSpace(name)) SaveAs(name.Trim());
    }

    private void SaveAs(string name)
    {
        try
        {
            _doc.Name = name;
            Saved.Save(name, _doc.ToData());
            _savedRevision = _doc.Revision;
            RememberRecent(name);
            Say($"Kept {name} in the Silo");
            RaiseValueChanged();
            RaiseActionsChanged();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (InvalidOperationException ex)
        {
            Say(ex.Message);
        }
    }

    public void OpenSaved(string name)
    {
        if (Saved.Load(name) is not { } data)
        {
            Say($"There is no image called {name} in the Silo.");
            return;
        }

        if (!ConfirmDiscard("Open another image")) return;

        try
        {
            var doc = ImageDocument.FromData(data);
            if (string.IsNullOrWhiteSpace(doc.Name)) doc.Name = name;
            Replace(doc, false);
            RememberRecent(name);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException)
        {
            Say($"{name} could not be opened: {ex.Message}");
        }
    }

    private void DeleteSaved()
    {
        if (string.IsNullOrWhiteSpace(_doc.Name)) return;

        Say(Saved.Delete(_doc.Name) ? $"Removed {_doc.Name} from the Silo" : $"{_doc.Name} was not kept");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void NewImage()
    {
        if (!ConfirmDiscard("Start another image")) return;

        Replace(ImageDocument.FromData(Lab_.Blank()), false);
    }

    private void NewImageOfSize()
    {
        string? text = LabUi.Ask("New image", "Size in pixels, width x height:", $"{_doc.Width}x{_doc.Height}");
        if (string.IsNullOrWhiteSpace(text)) return;

        if (!Lab_.TryParse(text, out var data) || data is null || data.IsEmpty)
        {
            Say("Say a size like 640x480.");
            return;
        }

        if (!ConfirmDiscard("Start another image")) return;

        Replace(ImageDocument.FromData(data), false);
    }

    // ---- library and recent -------------------------------------------------------------------------------------------

    public override Task OpenRecentAsync(string text)
    {
        OpenSaved(text);
        return Task.CompletedTask;
    }

    public override IReadOnlyList<LabLibrarySection> LibrarySections(string filter)
    {
        var kept = Saved.Names().Where(n => filter.Length == 0 || n.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        .Select(name => new LabLibraryEntry(name, "Image", null, "Design", () => OpenSaved(name), name == _doc.Name, ToolTip: "Double-click to open", Delete: () => Saved.Delete(name)))
                        .ToList();
        return [new("In the Silo", "Database5", kept, Note: Saved.IsAvailable ? null : "No Silo is open")];
    }

    // ---- clipboard -----------------------------------------------------------------------------------------------------

    /// <summary>What is selected on the active layer (all of the layer when nothing is), cut to the selection's rectangle with the selection as its opacity.</summary>
    private Raster? SelectedPicture(bool merged)
    {
        var source = merged ? _doc.Composite() : _doc.ActiveLayer?.Pixels;
        if (source is null) return null;

        if (_doc.Selection is not { } mask) return source.Clone();

        var bounds = mask.Bounds();
        if (bounds.IsEmpty) return null;

        var piece = source.Crop(bounds);
        for (int y = 0; y < bounds.Height; y++)
        for (int x = 0; x < bounds.Width; x++)
        {
            int i = (y * bounds.Width + x) * 4 + 3;
            piece.Pixels[i] = (byte)(piece.Pixels[i] * mask[bounds.X + x, bounds.Y + y] / 255);
        }

        return piece;
    }

    private void CopyPicture(bool merged)
    {
        if (SelectedPicture(merged) is not { } picture)
        {
            Say("There is nothing selected to copy.");
            return;
        }

        try
        {
            ImageCodec.ToClipboard(picture);
            Say($"Copied {picture.Width} x {picture.Height} pixels");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            Say("The clipboard is in use by another program.");
        }
    }

    private void CutPicture()
    {
        CopyPicture(false);
        if (_doc.ActiveLayer is not null && _doc.Selection is not null) _doc.FillSelection(Rgba.Transparent, "Cut");
    }

    private void PastePicture()
    {
        if (ImageCodec.FromClipboard() is not { } picture)
        {
            Say("There is no picture on the clipboard.");
            return;
        }

        AddPicture(picture, "Pasted");
        var pasted = _doc.ActiveLayer!.Pixels.ContentBounds();
        if (!pasted.IsEmpty) _doc.SetSelection(Mask.Rectangle(_doc.Width, _doc.Height, pasted.X, pasted.Y, pasted.Right, pasted.Bottom), "Select pasted");

        if (Tools.FirstOrDefault(t => t.Id == "selection") is { } move) ActivateTool(move);
    }

    private void PasteAsNewImage()
    {
        if (ImageCodec.FromClipboard() is not { } picture)
        {
            Say("There is no picture on the clipboard.");
            return;
        }

        if (!ConfirmDiscard("Start another image")) return;

        Replace(ImageDocument.FromRaster(picture, string.Empty), true);
    }

    // ---- filters ----------------------------------------------------------------------------------------------------------

    /// <summary>Runs one of <see cref="ImageFilters.Names" /> on the selection of the active layer, with the settings in the Filters pane.</summary>
    internal void RunFilter(string name)
    {
        if (_doc.ActiveLayer is null) return;

        var s = _filtersSet;
        Func<Raster, Raster>? filter = name switch
                                       {
                                           "Grayscale"           => ImageFilters.Grayscale,
                                           "Invert"              => ImageFilters.Invert,
                                           "Sepia"               => ImageFilters.Sepia,
                                           "Brightness/Contrast" => r => ImageFilters.BrightnessContrast(r, IntOf(s["brightness"], 0, -100, 100), IntOf(s["contrast"], 0, -100, 100)),
                                           "Blur"                => r => ImageFilters.Blur(r, DoubleOf(s["blur"], 4, 0, 100)),
                                           "Sharpen"             => r => ImageFilters.Sharpen(r, DoubleOf(s["sharpRadius"], 2, 0.5, 50), DoubleOf(s["sharpAmount"], 1, 0, 5)),
                                           "Pixelate"            => r => ImageFilters.Pixelate(r, IntOf(s["pixel"], 8, 1, 512)),
                                           "Threshold"           => r => ImageFilters.Threshold(r, IntOf(s["threshold"], 128, 0, 255)),
                                           _                     => null
                                       };
        if (filter is null)
        {
            Say($"There is no filter called {name}.");
            return;
        }

        CommitPending();
        _doc.ApplyFilter(name, filter);
        Say($"{name} applied to " + (_doc.Selection is null ? "the layer" : "the selection"));
    }

    // ---- actions -----------------------------------------------------------------------------------------------------------

    public override IEnumerable<ProvidedCommand> GlobalActions
    {
        get
        {
            yield return ProvidedCommand.From("new-image", "_New Image", NewImage, "TbNew", null, "Start with an empty image of the Lab's usual size");
            yield return ProvidedCommand.From("new-image-size", "New Image of _Size...", NewImageOfSize, "TbNew", null, "Start with an empty image of a size you give");
            yield return ProvidedCommand.From("open-image", "_Open Picture File...", OpenFile, "TbOpen", null, "Open a PNG, JPEG, BMP, GIF, ICO or TIFF file as a new image");
            yield return ProvidedCommand.From("import-layer", "_Import Picture as Layer...", ImportLayer, "TbImport", null, "Bring a picture file in as a new layer of this image");
            yield return ProvidedCommand.From("paste-new", "Paste as New _Image", PasteAsNewImage, "TbPaste", null, "Start an image from the picture on the clipboard");
            yield return ProvidedCommand.From("show-saved", "Show _Saved Images", () => Host?.ShowInspector("saved"), "Folder17", null, "Show the images kept in the Silo");
            yield return ProvidedCommand.From("settings", "Open Se_ttings", () => Host?.ShowInspector(LabParametersInspector.InspectorId), "Settings", null, "Show the Lab's parameters");
        }
    }

    public override IEnumerable<ProvidedCommand> ContextActions
    {
        get
        {
            foreach (var c in PendingActions()) yield return c;
        }
    }

    public override IEnumerable<ProvidedCommand> ValueActions
    {
        get
        {
            yield return ProvidedCommand.From("undo", "_Undo", () => Undo(), "Undo", () => _doc.CanUndo, "Take back the last change") with { Gesture = "Ctrl+Z" };
            yield return ProvidedCommand.From("redo", "_Redo", () => Redo(), "TbRedo", () => _doc.CanRedo, "Do again what was taken back") with { Gesture = "Ctrl+Y" };
            yield return ProvidedCommand.From("copy", "_Copy", () => CopyPicture(false), "TbCopy", () => _doc.ActiveLayer is not null, "Copy the selection of the active layer (all of it when nothing is selected)") with { Gesture = "Ctrl+C" };
            yield return ProvidedCommand.From("copy-merged", "Copy _Merged", () => CopyPicture(true), "TbCopy", () => true, "Copy the selection of all the layers together");
            yield return ProvidedCommand.From("cut", "Cu_t", CutPicture, "TbCut", () => _doc.Selection is not null, "Copy the selection and clear it from the layer") with { Gesture = "Ctrl+X" };
            yield return ProvidedCommand.From("paste", "_Paste", PastePicture, "TbPaste", null, "Put the picture on the clipboard in as a new layer") with { Gesture = "Ctrl+V" };
            yield return ProvidedCommand.From("save", "_Save Image", SaveImage, "TbSave", () => Saved.IsAvailable, "Keep the image, with its layers, in the Silo") with { Gesture = "Ctrl+S" };
            yield return ProvidedCommand.From("save-as", "Save Image _As...", SaveImageAs, "TbSaveAs", () => Saved.IsAvailable, "Keep the image in the Silo under another name");
            foreach (var format in ImageFormat.All)
            {
                var f = format;
                yield return ProvidedCommand.From("export-" + f.Name.ToLowerInvariant(), $"Export {f.Name}...", () => ExportAs(f), "TbExport", null,
                                                  $"Write the image, all layers together, to a {f.Name} file" + (f.HasAlpha ? string.Empty : " (transparency is laid over the color in the Lab's settings)"));
            }

            yield return ProvidedCommand.From("delete-saved", "_Delete Saved Image", DeleteSaved, "TbDelete", () => Saved.IsAvailable && !string.IsNullOrWhiteSpace(_doc.Name) && Saved.Exists(_doc.Name), "Remove the image from the Silo") with { IsDestructive = true };
        }
    }

    public bool Undo()
    {
        CancelPending();
        bool done = _doc.Undo();
        if (done) Say("Undid: " + (_doc.RedoName ?? "a change"));
        return done;
    }

    public bool Redo()
    {
        CancelPending();
        bool done = _doc.Redo();
        if (done) Say("Did again: " + (_doc.UndoName ?? "a change"));
        return done;
    }

    // ---- terminal ----------------------------------------------------------------------------------------------------------

    private static Task<string?> Done(string? text) => Task.FromResult(text);

    public override IReadOnlyList<LabTerminalCommand> TerminalCommands =>
    [
        new("info", "info                      the image: size, layers, selection, history", _ => Done(Info())),
        new("new", "new [WxH]                 start an empty image (the Lab's size when not said)", call => Done(NewCommand(call.Rest))),
        new("layers", "layers                    the layers, top first", _ => Done(LayerList())),
        new("layer", "layer <add|dup|del|up|down|merge|show|hide|rename> [text]", call => Done(LayerCommand(call.Args))),
        new("fg", "fg [color]                the foreground color (#RRGGBB)", call => Done(ColorCommand(call.Rest, true))),
        new("bg", "bg [color]                the background color", call => Done(ColorCommand(call.Rest, false))),
        new("filter", "filter <name>             run a filter on the selection (Grayscale, Invert, Sepia, Brightness/Contrast, Blur, Sharpen, Pixelate, Threshold)", call => Done(FilterCommand(call.Rest))),
        new("select", "select <all|none|invert|rect x y w h|ellipse x y w h>", call => Done(SelectCommand(call.Args))),
        new("fill", "fill [fg|bg|clear]        fill the selection (or the layer) with a color", call => Done(FillCommand(call.Rest))),
        new("tool", "tool [name]               show the tools, or pick one", call => Done(ToolCommand(call.Rest))),
        new("undo", "undo                      take back the last change", _ => Done(Undo() ? Status : "Nothing to undo.")),
        new("redo", "redo                      do again what was taken back", _ => Done(Redo() ? Status : "Nothing to redo.")),
        new("export", "export <path>             write the image to a .png, .jpg, .bmp, .gif or .ico file", call => Done(string.IsNullOrWhiteSpace(call.Rest) ? throw new InvalidOperationException("Give a file name: export picture.png") : ExportTo(call.Rest.Trim('"')))),
        new("open", "open <name|path>          open an image kept in the Silo, or a picture file", call => Done(OpenCommand(call.Rest))),
        new("save", "save [name]               keep the image in the Silo", call => Done(SaveCommand(call.Rest))),
        new("list", "list                      the images kept in the Silo", _ => Done(Saved.IsAvailable ? string.Join('\n', Saved.Names()) : "No Silo is open.")),
        new("size", "size <WxH>                scale the whole image to a new size", call => Done(SizeCommand(call.Rest, false))),
        new("canvas", "canvas <WxH>              change the size of the canvas without scaling the layers", call => Done(SizeCommand(call.Rest, true)))
    ];

    private string Info()
    {
        var sb = new StringBuilder($"{(string.IsNullOrWhiteSpace(_doc.Name) ? "(unnamed)" : _doc.Name)}: {_doc.Width} x {_doc.Height}, {_doc.Layers.Count} layer(s), active: {_doc.ActiveLayer?.Name}");
        sb.AppendLine();
        sb.AppendLine(_doc.Selection is null ? "Nothing selected." : "Selected: " + _doc.Selection.Bounds());
        sb.Append($"History: {_doc.UndoNames.Count} step(s)" + (_doc.UndoName is null ? string.Empty : $", last: {_doc.UndoName}"));
        sb.Append($"\nTool: {ActiveTool?.Name.Replace("_", string.Empty) ?? "none"}; foreground {Foreground.ToHex()}, background {Background.ToHex()}");
        return sb.ToString();
    }

    private string NewCommand(string rest)
    {
        ImageData? data = null;
        if (!string.IsNullOrWhiteSpace(rest) && !Lab_.TryParse(rest, out data)) throw new InvalidOperationException("Say a size like 640x480.");

        Replace(ImageDocument.FromData(data ?? Lab_.Blank()), false);
        return Status;
    }

    private string LayerList()
    {
        var lines = new List<string>();
        for (int i = _doc.Layers.Count - 1; i >= 0; i--)
        {
            var l = _doc.Layers[i];
            lines.Add($"{(i == _doc.ActiveIndex ? "*" : " ")} {i,2}  {(l.Visible ? "shown " : "hidden")}  {l.Opacity:P0,5}  {l.Blend,-10}  {l.Name}");
        }

        return string.Join('\n', lines);
    }

    private string LayerCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 0) throw new InvalidOperationException("layer add|dup|del|up|down|merge|show|hide|rename|select");

        string verb = args[0].ToLowerInvariant();
        string text = string.Join(' ', args.Skip(1));
        int    at   = _doc.ActiveIndex;
        switch (verb)
        {
            case "add": _doc.AddLayer(text.Length == 0 ? "Layer" : text); break;
            case "dup": _doc.DuplicateLayer(at); break;
            case "del": _doc.DeleteLayer(at); break;
            case "up": _doc.MoveLayer(at, 1); break;
            case "down": _doc.MoveLayer(at, -1); break;
            case "merge": if (!_doc.MergeDown(at)) throw new InvalidOperationException("There is no layer below to merge into."); break;
            case "show": _doc.SetVisible(at, true); break;
            case "hide": _doc.SetVisible(at, false); break;
            case "rename": _doc.RenameLayer(at, text); break;
            case "select":
                int index = int.TryParse(text, out int n) ? n : _doc.Layers.ToList().FindIndex(l => l.Name.Equals(text, StringComparison.OrdinalIgnoreCase));
                if (index < 0 || index >= _doc.Layers.Count) throw new InvalidOperationException("There is no such layer. Type layers to see them.");

                _doc.SetActive(index);
                break;
            default: throw new InvalidOperationException("layer add|dup|del|up|down|merge|show|hide|rename|select");
        }

        return LayerList();
    }

    private string ColorCommand(string rest, bool foreground)
    {
        if (string.IsNullOrWhiteSpace(rest)) return (foreground ? Foreground : Background).ToHex();

        if (!Rgba.TryParse(rest, out var color)) throw new InvalidOperationException("Give a color as #RGB, #RRGGBB or #RRGGBBAA.");

        if (foreground) Foreground = color;
        else Background = color;

        return color.ToHex();
    }

    private string FilterCommand(string rest)
    {
        string? name = ImageFilters.Names.FirstOrDefault(n => n.Equals(rest.Trim(), StringComparison.OrdinalIgnoreCase) || n.StartsWith(rest.Trim(), StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(rest) || name is null) return "Filters: " + string.Join(", ", ImageFilters.Names) + ". Their settings are in the Filters pane (params).";

        RunFilter(name);
        return Status;
    }

    private string SelectCommand(IReadOnlyList<string> args)
    {
        string what = args.Count > 0 ? args[0].ToLowerInvariant() : string.Empty;
        switch (what)
        {
            case "all": _doc.SelectAll(); break;
            case "none": _doc.SelectNone(); break;
            case "invert": _doc.InvertSelection(); break;
            case "rect":
            case "ellipse":
                if (args.Count < 5 || !args.Skip(1).Take(4).All(a => double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
                    throw new InvalidOperationException($"select {what} <x> <y> <width> <height>");

                var v = args.Skip(1).Take(4).Select(a => double.Parse(a, CultureInfo.InvariantCulture)).ToArray();
                var shape = what == "rect" ? Mask.Rectangle(_doc.Width, _doc.Height, v[0], v[1], v[0] + v[2], v[1] + v[3]) : Mask.Ellipse(_doc.Width, _doc.Height, v[0], v[1], v[0] + v[2], v[1] + v[3]);
                _doc.Select(shape, Enum.TryParse<SelectionMode>(_selectionSet["mode"].Text, out var mode) ? mode : SelectionMode.Replace);
                break;
            default: throw new InvalidOperationException("select all|none|invert|rect x y w h|ellipse x y w h");
        }

        return _doc.Selection is null ? "Nothing selected." : "Selected " + _doc.Selection.Bounds();
    }

    private string FillCommand(string rest)
    {
        var color = rest.Trim().ToLowerInvariant() switch { "" or "fg" => Foreground, "bg" => Background, "clear" => Rgba.Transparent, _ => Rgba.TryParse(rest, out var c) ? c : throw new InvalidOperationException("fill [fg|bg|clear|#RRGGBB]") };
        _doc.FillSelection(color, color.A == 0 ? "Clear" : "Fill");
        return "Done.";
    }

    private string ToolCommand(string rest)
    {
        if (string.IsNullOrWhiteSpace(rest))
            return string.Join('\n', Tools.Select(t => $"{(t.Id == ActiveTool?.Id ? "*" : " ")} {t.Id,-10} {t.Name.Replace("_", string.Empty)}"));

        var tool = LabModelRules.FindTool(this, rest.Trim()) ?? throw new InvalidOperationException("There is no tool called " + rest.Trim() + ". Type tool to see them.");
        ActivateTool(tool);
        return "Tool: " + tool.Name.Replace("_", string.Empty);
    }

    private string OpenCommand(string rest)
    {
        if (string.IsNullOrWhiteSpace(rest)) throw new InvalidOperationException("Open which? Type list to see the kept images.");

        string text = rest.Trim().Trim('"');
        if (ImageFormat.IsPicture(text)) ImportAsImage(text);
        else OpenSaved(text);
        return Status;
    }

    private string SaveCommand(string rest)
    {
        string name = string.IsNullOrWhiteSpace(rest) ? _doc.Name : rest.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Give it a name: save <name>.");

        SaveAs(name);
        return Status;
    }

    private string SizeCommand(string rest, bool canvas)
    {
        var parts = rest.Split(new[] { 'x', 'X', ' ', '*' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out int w) || !int.TryParse(parts[1], out int h)) throw new InvalidOperationException("Say a size like 640x480.");

        try
        {
            if (canvas) _doc.ResizeCanvas(w, h);
            else _doc.ScaleImage(w, h);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(ex.Message);
        }

        return $"{_doc.Width} x {_doc.Height}";
    }

    public override UIElement BuildMainView() => new ImageView(this);
}

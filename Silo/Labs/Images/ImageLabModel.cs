using System.Globalization;
using Newtonsoft.Json;

namespace Silo.Labs.Images;

// The Image Lab as a model (see LabModel.cs):
//   subject  an image (ImageData), with its layers, selection and history
//   parts    the layers (top first); the active one is where tools paint
//   aspects  Pointer / Selection, FG / BG Brush, Brush Size / Pattern, Text Settings (of the Text tool), Rotate / Scale / Skew, Layers, Filters
//   tools    Selection, Hand, Marquee, Lasso, Scale/Skew, Rotate, Text, Brush, Eraser, Gradient, Bezier
public sealed partial class ImageLabSession
{
    private const string SubjectId = "image";

    private static readonly LabPartType LayerType = new("layer", "Layer", "Layers", "A transparent sheet of the image; tools paint on the active one",
                                                        [new("layer-transparent", "Transparent layer", "Layer|Transparent", "An empty layer", "Layers", "New layer"),
                                                         new("layer-white", "White layer", "Layer|White", "A layer filled with white", "Layers", "New layer"),
                                                         new("layer-black", "Black layer", "Layer|Black", "A layer filled with black", "Layers", "New layer"),
                                                         new("layer-gray", "Gray layer", "Layer|Gray", "A layer filled with gray", "Layers", "New layer")],
                                                        [new("layer-colored", "Colored layer", "{{name}}|{{color}}", [new("name", "Name", "Layer"), new("color", "Fill (#RRGGBB, or Transparent)", "Transparent")], "A layer filled with a color you give", "Layers", "New layer")]);

    protected override string SubjectNoun => "image";

    // ---- subject: prefabs and templates ---------------------------------------------------------------------

    protected override IEnumerable<LabSubjectType> DefineSubjectTypes()
    {
        var prefabs = new List<LabPrefab>();
        foreach (int side in new[] { 16, 24, 32, 48, 64, 128, 256, 512 })
            prefabs.Add(new LabPrefab($"icon-{side}", $"Icon {side} x {side}", $"{side}x{side}|Transparent", "Transparent, square", "Design", "Square icons"));

        void Add(string id, string name, int w, int h, string background, string group, string? note = null) => prefabs.Add(new LabPrefab(id, $"{name} ({w} x {h})", $"{w}x{h}|{background}", note, "Design", group));

        Add("screen-720", "Screen 720p", 1280, 720, "White", "Screens");
        Add("screen-1080", "Screen 1080p", 1920, 1080, "White", "Screens");
        Add("screen-4x3", "Screen 4:3", 800, 600, "White", "Screens");
        Add("paper-a4", "A4 paper", 794, 1123, "White", "Paper", "At 96 pixels to the inch");
        Add("paper-letter", "US Letter paper", 816, 1056, "White", "Paper", "At 96 pixels to the inch");
        Add("social-square", "Square post", 1080, 1080, "White", "Social");
        Add("social-banner", "Banner", 1500, 500, "White", "Social");

        var templates = new List<LabTemplate>
                        {
                            new("blank", "Blank image", "{{width}}x{{height}}|{{background}}",
                                [new("width", "Width in pixels", "800"), new("height", "Height in pixels", "600"), new("background", "Background", "White", ["White", "Black", "Gray", "Transparent"])],
                                "An empty image of the size you give", "Design")
                        };
        yield return new LabSubjectType(SubjectId, "Image", "Design", "A picture in layers, with a selection and a history", prefabs, templates);
    }

    /// <summary>A size such as <c>640x480</c>.</summary>
    private static bool TryParseSize(string text, out int width, out int height)
    {
        width  = 0;
        height = 0;
        var parts = text.Split(new[] { 'x', 'X', '*', ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
               && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out width)
               && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out height)
               && width is >= 1 and <= Raster.MaxSide && height is >= 1 and <= Raster.MaxSide && (long)width * height <= Raster.MaxPixels;
    }

    /// <summary>A color given as a name (White, Black, Gray, Transparent) or as #RRGGBB[AA]; null for nothing, which is transparent.</summary>
    private static Rgba? FillOf(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || text.Equals("Transparent", StringComparison.OrdinalIgnoreCase)) return null;

        if (Rgba.TryParse(text, out var color)) return color;

        return text.ToLowerInvariant() switch { "black" => Rgba.Black, "gray" or "grey" => new Rgba(128, 128, 128), _ => Rgba.White };
    }

    protected override Task OnNewSubjectAsync(LabSubjectType type, string content)
    {
        ImageData? data = null;
        if (content.TrimStart().StartsWith('{'))
        {
            try
            {
                data = JsonConvert.DeserializeObject<ImageData>(content);
            }
            catch (JsonException)
            {
                data = null;
            }
        }
        else
        {
            var parts = content.Split('|', 2);
            if (TryParseSize(parts[0], out int w, out int h)) data = ImageDocument.Blank(w, h, parts.Length > 1 ? FillOf(parts[1]) ?? Rgba.Transparent : ImageLab.ColorOf(Lab_.Parameters[ImageLabParameters.Background].Text)).ToData();
        }

        if (data is null || data.IsEmpty) throw new InvalidOperationException($"'{LabUi.Shorten(content, 40)}' is not an image size such as 640x480.");

        if (ConfirmDiscard("Start another image")) Replace(ImageDocument.FromData(data), false);

        return Task.CompletedTask;
    }

    // ---- parts: the layers ---------------------------------------------------------------------------------------------

    protected override IEnumerable<LabPartType> DefinePartTypes() => [LayerType];

    protected override IEnumerable<LabPart> BuildParts()
    {
        for (int i = _doc.Layers.Count - 1; i >= 0; i--)
        {
            var layer  = _doc.Layers[i];
            var detail = new List<string>();
            if (!layer.Visible) detail.Add("hidden");
            if (layer.Opacity < 0.999) detail.Add(layer.Opacity.ToString("P0", CultureInfo.InvariantCulture));
            if (layer.Blend != BlendMode.Normal) detail.Add(layer.Blend.ToString());

            yield return new LabPart(LayerType, "l:" + layer.Id, layer.Name, layer, detail.Count == 0 ? null : string.Join(", ", detail));
        }
    }

    protected override void OnPartActivated(LabPart? part)
    {
        if (part?.Tag is ImageLayer layer && _doc.IndexOf(layer.Id) is var index and >= 0) _doc.SetActive(index);
    }

    protected override Task OnAddPartAsync(LabPartType type, LabPart? parent, string content)
    {
        var parts = content.Split('|', 2);
        string name = parts[0].Trim().Length == 0 ? "Layer" : parts[0].Trim();
        _doc.AddLayer(name, parts.Length > 1 ? FillOf(parts[1]) : null);
        Say($"Added the layer {_doc.ActiveLayer?.Name}");
        return Task.CompletedTask;
    }

    // ---- helpers for the commands --------------------------------------------------------------------------------------------

    private ImageLayer? Layer => _doc.ActiveLayer;

    private bool HasLayer => _doc.ActiveLayer is not null;

    private void Guard(Action change)
    {
        try
        {
            change();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Say(ex.Message);
        }
    }

    private int? AskNumber(string title, string prompt, string initial, int min, int max)
    {
        string? text = LabUi.Ask(title, prompt, initial);
        if (text is null) return null;

        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= min && n <= max) return n;

        Say($"Say a whole number from {min} to {max}.");
        return null;
    }

    private (int Width, int Height)? AskSize(string title, string initial)
    {
        string? text = LabUi.Ask(title, "Width x height, in pixels:", initial);
        if (text is null) return null;

        if (TryParseSize(text, out int w, out int h)) return (w, h);

        Say($"Say a size such as 640x480 (each side 1 to {Raster.MaxSide}).");
        return null;
    }

    private static string Slug(string name) => new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

    // ---- aspects ---------------------------------------------------------------------------------------------------------------

    protected override IEnumerable<LabAspect> DefineAspects()
    {
        var image = LabOwner.OfSubjectType(SubjectId);

        yield return new LabAspect("selection", "Pointer / Selection", image)
                     {
                         IconKey     = "Cursor1",
                         Description = "What is selected: the Marquee and Lasso make selections, the Selection Tool moves what is selected",
                         Parameters  = _selectionSet,
                         Commands =
                         [
                             ProvidedCommand.From("sel-all", "Select _All", () => _doc.SelectAll(), "TbSelectAll", null, "Select the whole image") with { Gesture = "Ctrl+A" },
                             ProvidedCommand.From("sel-none", "_Deselect", () => _doc.SelectNone(), null, () => _doc.Selection is not null, "Select nothing") with { Gesture = "Ctrl+D" },
                             ProvidedCommand.From("sel-invert", "_Invert Selection", () => _doc.InvertSelection(), "Intersect", null, "Select what is not selected, and the other way round") with { Gesture = "Ctrl+Shift+I" },
                             ProvidedCommand.From("sel-grow", "_Grow Selection...", () => ChangeSelection(1), "Expand", () => _doc.Selection is not null, "Make the selection bigger by some pixels"),
                             ProvidedCommand.From("sel-shrink", "S_hrink Selection...", () => ChangeSelection(-1), null, () => _doc.Selection is not null, "Make the selection smaller by some pixels"),
                             ProvidedCommand.From("sel-feather", "_Feather Selection...", FeatherSelection, null, () => _doc.Selection is not null, "Soften the edge of the selection"),
                             ProvidedCommand.From("sel-layer", "Select _Layer Contents", () => _doc.SelectLayerContents(), "Layers", HasLayerFunc, "Select what is painted on the active layer"),
                             ProvidedCommand.From("sel-crop", "_Crop Image to Selection", () => Guard(() => { if (!_doc.CropToSelection()) Say("There is no selection to crop to."); }), null, () => _doc.Selection is not null, "Cut the image down to the rectangle around the selection"),
                             ProvidedCommand.From("sel-fill-fg", "Fill with _Foreground", () => FillWith(Foreground, "Fill"), "Record", HasLayerFunc, "Paint the selection (or the whole layer) with the foreground color"),
                             ProvidedCommand.From("sel-fill-bg", "Fill with _Background", () => FillWith(Background, "Fill"), null, HasLayerFunc, "Paint the selection (or the whole layer) with the background color"),
                             ProvidedCommand.From("sel-clear", "C_lear", () => FillWith(Rgba.Transparent, "Clear"), "TbDelete", HasLayerFunc, "Make the selection (or the whole layer) transparent")
                         ]
                     };
        yield return new LabAspect("colors", "FG / BG Brush", image)
                     {
                         IconKey     = "Symbol",
                         Description = "The foreground color (left button) and the background color (right button)",
                         Parameters  = _colorsSet,
                         Commands =
                         [
                             ProvidedCommand.From("col-swap", "_Swap Colors", SwapColors, "Refresh", null, "Foreground becomes background and the other way round"),
                             ProvidedCommand.From("col-reset", "_Default Colors", ResetColors, null, null, "Black on white"),
                             ProvidedCommand.From("col-pick", "_Pick Foreground from Image", PickCenterColor, "Cursor", null, "Take the foreground color from the middle of the image (Alt+click with the Brush picks where you click)")
                         ]
                     };
        yield return new LabAspect("brush", "Brush Size / Pattern", image)
                     {
                         IconKey     = "Record",
                         Description = "The size, tip, opacity and spacing of the Brush, the Eraser and the stroke of a Bezier path",
                         Parameters  = _brushSet,
                         Commands =
                         [
                             ProvidedCommand.From("brush-bigger", "_Bigger Brush", () => SetBrush(size: Math.Max(Brush.Size + 1, (int)Math.Round(Brush.Size * 1.25))), "Add", null, "Make the brush 25% bigger"),
                             ProvidedCommand.From("brush-smaller", "S_maller Brush", () => SetBrush(size: Math.Min(Brush.Size - 1, (int)Math.Round(Brush.Size * 0.8))), "Remove", null, "Make the brush 20% smaller"),
                             ProvidedCommand.From("brush-hard", "_Hard Round", () => SetBrush(pattern: BrushPattern.Round), null, null, "Switch to the hard round tip"),
                             ProvidedCommand.From("brush-soft", "So_ft Round", () => SetBrush(pattern: BrushPattern.Soft), null, null, "Switch to the soft round tip")
                         ]
                     };
        yield return new LabAspect("transform", "Rotate / Scale / Skew", image)
                     {
                         IconKey     = "Fullscreen",
                         Description = "Turn, scale, skew and move what is selected on the active layer (the whole layer when nothing is selected), with the Scale/Skew and Rotate tools or with numbers",
                         Parameters  = _transformSet,
                         Commands =
                         [
                             ProvidedCommand.From("tf-apply", "_Apply Transform", () => CommitPending(), "Check", () => _tf is { IsChanged: true }, "Make the transform final (Enter)"),
                             ProvidedCommand.From("tf-cancel", "_Cancel Transform", () => CancelPending(), null, () => _tf is { IsChanged: true }, "Put the layer back as it was (Esc)"),
                             ProvidedCommand.From("tf-reset", "_Reset Numbers", ResetTransformNumbers, "Refresh", () => _tf is not null, "Back to no turn, no scale, no skew and no move"),
                             ProvidedCommand.From("tf-flip-h", "Flip _Horizontally", () => FlipTransform(true), null, () => _tf is not null, "Mirror left to right (the scale becomes negative)"),
                             ProvidedCommand.From("tf-flip-v", "Flip _Vertically", () => FlipTransform(false), null, () => _tf is not null, "Mirror top to bottom (the scale becomes negative)")
                         ]
                     };
        yield return new LabAspect("layers", "Layers", image)
                     {
                         IconKey     = "Layers",
                         Description = "The sheets the image is made of: tools paint on the active one",
                         Commands =
                         [
                             ProvidedCommand.From("lay-add", "_New Layer", () => AddNamedLayer(false), "Add", null, "Add an empty layer above the active one") with { Gesture = "Ctrl+Shift+N" },
                             ProvidedCommand.From("lay-dup", "_Duplicate Layer", () => _doc.DuplicateLayer(_doc.ActiveIndex), "TbCopy", HasLayerFunc, "Copy the active layer"),
                             ProvidedCommand.From("lay-del", "De_lete Layer", () => _doc.DeleteLayer(_doc.ActiveIndex), "TbDelete", () => _doc.Layers.Count > 1, "Remove the active layer") with { IsDestructive = true },
                             ProvidedCommand.From("lay-merge", "_Merge Down", () => { if (!_doc.MergeDown(_doc.ActiveIndex)) Say("There is no layer below."); }, null, () => _doc.ActiveIndex > 0, "Join the active layer to the one below it"),
                             ProvidedCommand.From("lay-flatten", "_Flatten Image", () => _doc.Flatten(), null, () => _doc.Layers.Count > 1, "Join all the visible layers into one"),
                             ProvidedCommand.From("lay-up", "Move _Up", () => _doc.MoveLayer(_doc.ActiveIndex, 1), null, () => _doc.ActiveIndex < _doc.Layers.Count - 1, "Raise the layer one step"),
                             ProvidedCommand.From("lay-down", "Move Dow_n", () => _doc.MoveLayer(_doc.ActiveIndex, -1), null, () => _doc.ActiveIndex > 0, "Lower the layer one step"),
                             ProvidedCommand.From("lay-rename", "_Rename Layer...", RenameLayer, null, HasLayerFunc, "Give the layer another name"),
                             ProvidedCommand.From("lay-visible", "Layer _Shown", () => _doc.SetVisible(_doc.ActiveIndex, !(Layer?.Visible ?? true)), "Hide", HasLayerFunc, "Show or hide the layer") with { IsChecked = () => Layer?.Visible == true },
                             ProvidedCommand.From("lay-flip-h", "Flip Layer H_orizontally", () => _doc.FlipLayer(_doc.ActiveIndex, true), null, HasLayerFunc, "Mirror only this layer, left to right"),
                             ProvidedCommand.From("lay-flip-v", "Flip Layer Ver_tically", () => _doc.FlipLayer(_doc.ActiveIndex, false), null, HasLayerFunc, "Mirror only this layer, top to bottom")
                         ]
                     };
        yield return new LabAspect("filters", "Filters", image)
                     {
                         IconKey     = "DataCleaning",
                         Description = "Change the pixels of the selection (or the whole active layer) with a filter",
                         Parameters  = _filtersSet,
                         Commands    = ImageFilters.Names.Select(name => ProvidedCommand.From("filter-" + Slug(name), name + (FilterNeedsSettings(name) ? "..." : string.Empty), () => RunFilter(name), "DataCleaning", HasLayerFunc, FilterTip(name))).ToList()
                     };
    }

    private bool HasLayerFunc() => HasLayer;

    private static bool FilterNeedsSettings(string name) => name is not ("Grayscale" or "Invert" or "Sepia");

    private static string FilterTip(string name) => name switch
                                                    {
                                                        "Grayscale"           => "Take the color out",
                                                        "Invert"              => "Turn every color into its opposite",
                                                        "Sepia"               => "Tint like an old photograph",
                                                        "Brightness/Contrast" => "Lighten or darken, and stretch or squeeze the tones (the numbers are in the Filters pane)",
                                                        "Blur"                => "Soften (the radius is in the Filters pane)",
                                                        "Sharpen"             => "Make edges crisper (the radius and amount are in the Filters pane)",
                                                        "Pixelate"            => "Turn the picture into big square blocks (the block size is in the Filters pane)",
                                                        "Threshold"           => "Make every pixel black or white (the level is in the Filters pane)",
                                                        _                     => name
                                                    };

    private void ChangeSelection(int sign)
    {
        if (_doc.Selection is not { } mask) return;

        int? n = AskNumber(sign > 0 ? "Grow selection" : "Shrink selection", "By how many pixels (1 to 500)?", "2", 1, 500);
        if (n is { } pixels) _doc.SetSelection(mask.Grow(sign * pixels), sign > 0 ? "Grow selection" : "Shrink selection");
    }

    private void FeatherSelection()
    {
        if (_doc.Selection is not { } mask) return;

        int? n = AskNumber("Feather selection", "Radius in pixels (1 to 250)?", "6", 1, 250);
        if (n is { } radius) _doc.SetSelection(mask.Feather(radius), "Feather selection");
    }

    private void FillWith(Rgba color, string name)
    {
        CommitPending();
        _doc.FillSelection(color, name);
        if (color.A > 0) Remember(color);
    }

    private void PickCenterColor()
    {
        var picture = _doc.Composite();
        Foreground = picture.Get(_doc.Width / 2, _doc.Height / 2).WithAlpha(255);
    }

    private void AddNamedLayer(bool ask)
    {
        string name = _doc.UniqueLayerName("Layer");
        if (ask && LabUi.Ask("New layer", "What is the layer called?", name) is { } answer && answer.Trim().Length > 0) name = answer.Trim();

        _doc.AddLayer(name);
    }

    private void RenameLayer()
    {
        if (Layer is not { } layer) return;

        string? name = LabUi.Ask("Rename layer", "New name:", layer.Name);
        if (!string.IsNullOrWhiteSpace(name)) _doc.RenameLayer(_doc.ActiveIndex, name.Trim());
    }

    // ---- tools -------------------------------------------------------------------------------------------------------------

    protected override IEnumerable<LabTool> DefineTools()
    {
        var image = LabOwner.OfSubjectType(SubjectId);

        LabTool Tool(string id, string name, string icon, string description, LabParameterSet? parameters = null, IReadOnlyList<ProvidedCommand>? commands = null, IReadOnlyList<LabAspect>? aspects = null) =>
            new(id, name, image) { IconKey = icon, Description = description, Parameters = parameters, Commands = commands ?? [], Aspects = aspects ?? [], Activated = on => ToolSwitched(id, on) };

        yield return Tool("selection", "_Selection Tool", "Cursor", "Click a layer to make it active and drag to move it; drag inside a selection to move what is selected", _moveSet);
        yield return Tool("hand", "_Hand Tool", "OpenHand", "Drag to move the picture around the window (Space or the middle button does it with any tool; the wheel zooms)");
        yield return Tool("marquee", "_Marquee Tool", "Expand", "Drag a rectangle or an ellipse (the Pointer / Selection aspect says which); Shift makes it square, Alt draws it from the middle; a click selects nothing");
        yield return Tool("lasso", "_Lasso Tool", "Swipe", "Select by drawing around: freehand, or a polygon of clicks (double-click or Enter closes it, Esc gives up)",
                          commands: [ProvidedCommand.From("lasso-close", "_Close Polygon", () => CommitPending(), "Check", () => _lasso is { Count: >= 3 }, "Close the polygon and select what is inside")]);
        yield return Tool("scale", "Scale/S_kew Tool", "Fullscreen", "Drag the handles of the box: corners scale (Shift keeps the shape, Ctrl scales about the middle), edges scale one way, Alt on an edge skews; drag inside to move");
        yield return Tool("rotate", "_Rotate Tool", "Refresh1", "Drag around the box to turn what is selected (Shift turns by 15 degrees); the numbers are in the Rotate / Scale / Skew aspect");
        yield return Tool("text", "_Text Tool", "Keywords", "Click on the picture and type; Ctrl+Enter or clicking away places the text, Esc gives up", aspects: [TextAspect()]);
        yield return Tool("brush", "_Brush Tool", "Design", "Paint with the foreground color (left button) or the background color (right button); Shift+click draws a straight line, Alt+click picks a color");
        yield return Tool("eraser", "_Eraser Tool", "Exclude", "Rub out to transparency, with the brush's size, tip and opacity");
        yield return Tool("gradient", "_Gradient Tool", "Opacity", "Drag from where the gradient starts to where it ends", _gradientSet);
        yield return Tool("bezier", "Be_zier Tool", "Nodes", "Click for corner points and drag for smooth ones; click the first point (or press Enter) to finish, Backspace takes the last point back, Esc gives up", _bezierSet,
                          [ProvidedCommand.From("bez-finish", "_Finish Path", () => CommitPending(), "Check", () => _bezier.Count >= 2, "Draw the path with the settings above (Enter)"),
                           ProvidedCommand.From("bez-cancel", "_Cancel Path", () => CancelPending(), null, () => _bezier.Count > 0, "Throw the path away (Esc)"),
                           ProvidedCommand.From("bez-last", "Remove _Last Point", RemoveLastBezierPoint, null, () => _bezier.Count > 0, "Take back the last point (Backspace)")]);
    }

    /// <summary>The settings of the Text tool: they are there only while the tool is in hand.</summary>
    private LabAspect TextAspect() => new("text", "Text Settings", LabOwner.OfTool("text"))
                                      {
                                          IconKey     = "Keywords",
                                          Description = "The typeface and how text is placed",
                                          Parameters  = _textSet
                                      };

    // ---- commands of the image and the view -----------------------------------------------------------------------------------

    protected override IEnumerable<LabCommandEntry> DefineModelCommands()
    {
        var image = new List<ProvidedCommand>
                    {
                        ProvidedCommand.From("img-size", "Image _Size...", () => { if (AskSize("Image size", $"{_doc.Width}x{_doc.Height}") is { } s) { CommitPending(); Guard(() => _doc.ScaleImage(s.Width, s.Height)); } }, null, null, "Scale the whole image, layers and all, to a new size"),
                        ProvidedCommand.From("img-canvas", "_Canvas Size...", () => { if (AskSize("Canvas size", $"{_doc.Width}x{_doc.Height}") is { } s) { CommitPending(); Guard(() => _doc.ResizeCanvas(s.Width, s.Height)); } }, null, null, "Make the canvas bigger (with transparent room) or smaller (cutting the layers off) without scaling them"),
                        ProvidedCommand.From("img-flip-h", "Flip Image _Horizontally", () => { CommitPending(); _doc.FlipImage(true); }, null, null, "Mirror all the layers, left to right"),
                        ProvidedCommand.From("img-flip-v", "Flip Image _Vertically", () => { CommitPending(); _doc.FlipImage(false); }, null, null, "Mirror all the layers, top to bottom"),
                        ProvidedCommand.From("img-rot-cw", "Rotate 90° C_lockwise", () => { CommitPending(); _doc.RotateImage90(true); }, "Refresh1", null, "Turn the whole image a quarter clockwise"),
                        ProvidedCommand.From("img-rot-ccw", "Rotate 90° Counter-cl_ockwise", () => { CommitPending(); _doc.RotateImage90(false); }, null, null, "Turn the whole image a quarter counter-clockwise"),
                        ProvidedCommand.From("img-crop", "Crop to _Selection", () => Guard(() => { CommitPending(); if (!_doc.CropToSelection()) Say("There is no selection to crop to."); }), null, () => _doc.Selection is not null, "Cut the image down to the rectangle around the selection")
                    };
        yield return new LabCommandEntry(LabOwner.Subject, ProvidedCommand.Submenu("image-menu", "_Image", image, null, "Design", "Change the whole image"));

        var view = new List<ProvidedCommand>
                   {
                       ProvidedCommand.From("view-in", "Zoom _In", () => RequestZoom("in"), "ZoomIn", null, "Come closer") with { Gesture = "Ctrl+OemPlus" },
                       ProvidedCommand.From("view-out", "Zoom _Out", () => RequestZoom("out"), null, null, "Go further away") with { Gesture = "Ctrl+OemMinus" },
                       ProvidedCommand.From("view-fit", "_Fit in Window", () => RequestZoom("fit"), "Fullscreen", null, "Show all of the picture") with { Gesture = "Ctrl+0" },
                       ProvidedCommand.From("view-100", "_Actual Pixels", () => RequestZoom("100"), null, null, "One pixel of the picture to one pixel of the screen") with { Gesture = "Ctrl+1" }
                   };
        yield return new LabCommandEntry(LabOwner.Subject, ProvidedCommand.Submenu("view-zoom", "_Zoom", view, null, "ZoomIn", "How big the picture is shown"));
        yield return new LabCommandEntry(LabOwner.Part, ProvidedCommand.From("layer-solo", "Show Only This _Layer", ShowOnlyActive, null, () => _doc.Layers.Count > 1, "Hide all the other layers"));
        yield return new LabCommandEntry(LabOwner.Part, ProvidedCommand.From("layer-show-all", "Show _All Layers", ShowAllLayers, null, () => _doc.Layers.Any(l => !l.Visible), "Show every layer"));
    }

    private void ShowOnlyActive()
    {
        for (int i = 0; i < _doc.Layers.Count; i++) _doc.SetVisible(i, i == _doc.ActiveIndex);
    }

    private void ShowAllLayers()
    {
        for (int i = 0; i < _doc.Layers.Count; i++)
            if (!_doc.Layers[i].Visible)
                _doc.SetVisible(i, true);
    }
}

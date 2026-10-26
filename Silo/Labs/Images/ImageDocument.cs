using System.IO.Compression;

namespace Silo.Labs.Images;

public enum BlendMode { Normal, Multiply, Screen, Overlay, Darken, Lighten, Difference }

/// <summary>One layer of an image: pixels the size of the image, and how they are laid over the layers below.</summary>
public sealed class ImageLayer
{
    public ImageLayer(string name, Raster pixels)
    {
        Id     = Guid.NewGuid().ToString("N")[..8];
        Name   = name;
        Pixels = pixels;
    }

    /// <summary>Identifies the layer while the image is open (the model's parts are keyed by it).</summary>
    public string Id { get; internal set; }

    public string Name { get; internal set; }

    public bool Visible { get; internal set; } = true;

    /// <summary>0 to 1.</summary>
    public double Opacity { get; internal set; } = 1;

    public BlendMode Blend { get; internal set; }

    public Raster Pixels { get; internal set; }

    /// <summary>What to show in place of the pixels while a transform is being worked on.</summary>
    public Raster? Preview { get; internal set; }

    public Raster Shown => Preview ?? Pixels;
}

/// <summary>A step of the history.</summary>
public abstract class ImageEdit
{
    protected ImageEdit(string name) => Name = name;

    public string Name { get; }

    /// <summary>About how much memory it holds on to.</summary>
    public abstract long Bytes { get; }

    public abstract void Undo(ImageDocument doc);

    public abstract void Redo(ImageDocument doc);
}

/// <summary>Everything about an image except the contents of the pixels (which are kept by reference): what a step of history sets back.</summary>
internal sealed record DocState(int Width, int Height, IReadOnlyList<LayerState> Layers, int Active, Mask? Selection)
{
    public static DocState Capture(ImageDocument d) =>
        new(d.Width, d.Height, d.Layers.Select(l => new LayerState(l, l.Name, l.Visible, l.Opacity, l.Blend, l.Pixels)).ToList(), d.ActiveIndex, d.Selection);

    public void Restore(ImageDocument d) => d.RestoreState(this);

    /// <summary>The rasters this state holds that the other does not.</summary>
    public long OwnBytes(DocState other)
    {
        var theirs = new HashSet<Raster>(other.Layers.Select(l => l.Pixels), ReferenceEqualityComparer.Instance as IEqualityComparer<Raster>);
        long bytes = Layers.Where(l => !theirs.Contains(l.Pixels)).Sum(l => (long)l.Pixels.Pixels.Length);
        if (Selection is not null && !ReferenceEquals(Selection, other.Selection)) bytes += Selection.Values.Length;
        return bytes;
    }
}

internal sealed record LayerState(ImageLayer Layer, string Name, bool Visible, double Opacity, BlendMode Blend, Raster Pixels);

internal sealed class StateEdit(string name, DocState before, DocState after) : ImageEdit(name)
{
    public DocState Before { get; } = before;

    public DocState After { get; internal set; } = after;

    public override long Bytes => Before.OwnBytes(After) + After.OwnBytes(Before);

    public override void Undo(ImageDocument doc) => Before.Restore(doc);

    public override void Redo(ImageDocument doc) => After.Restore(doc);
}

/// <summary>A change to the pixels of one layer, kept as just the rectangle that changed.</summary>
internal sealed class PixelEdit(string name, ImageLayer layer, IntRect rect, Raster before, Raster after) : ImageEdit(name)
{
    public override long Bytes => before.Pixels.Length + after.Pixels.Length;

    public IntRect Rect => rect;

    public override void Undo(ImageDocument doc) => Write(doc, before);

    public override void Redo(ImageDocument doc) => Write(doc, after);

    private void Write(ImageDocument doc, Raster source)
    {
        layer.Pixels.Paste(source, rect.X, rect.Y);
        doc.RaiseChanged(rect);
    }
}

/// <summary>The saved form of an image: what the Silo keeps and the Lab opens.</summary>
public sealed class ImageData
{
    public string Name { get; set; } = string.Empty;

    public int Width { get; set; }

    public int Height { get; set; }

    public int Active { get; set; }

    public List<ImageLayerData> Layers { get; set; } = [];

    /// <summary>The selection (compressed, one byte a pixel), or null.</summary>
    public string? Selection { get; set; }

    /// <summary>An image file to bring in when the Lab opens this (a path typed or dropped); not part of what is kept.</summary>
    [Newtonsoft.Json.JsonIgnore]
    public string? ImportPath { get; set; }

    public bool IsEmpty => Layers.Count == 0;
}

public sealed class ImageLayerData
{
    public string Name { get; set; } = string.Empty;

    public bool Visible { get; set; } = true;

    public double Opacity { get; set; } = 1;

    public string Blend { get; set; } = nameof(BlendMode.Normal);

    /// <summary>The pixels (blue, green, red, alpha; rows from the top), compressed and written in base 64.</summary>
    public string Pixels { get; set; } = string.Empty;
}

/// <summary>
///     An image being worked on: layers, a selection, and the history of what was done. Everything that changes it goes through here, so each
///     change is one step that can be undone.
/// </summary>
public sealed class ImageDocument
{
    private const int  MaxEdits = 100;
    private const long MaxBytes = 400L * 1024 * 1024;

    private readonly List<ImageLayer> _layers = [];
    private readonly List<ImageEdit>  _undo   = [];
    private readonly List<ImageEdit>  _redo   = [];
    private          string?          _mergeKey;
    private          DateTime         _mergeTime;

    public ImageDocument(int width, int height)
    {
        Raster.CheckSize(width, height);
        Width  = width;
        Height = height;
    }

    public string Name { get; set; } = string.Empty;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public IReadOnlyList<ImageLayer> Layers => _layers;

    public int ActiveIndex { get; private set; }

    public ImageLayer? ActiveLayer => ActiveIndex >= 0 && ActiveIndex < _layers.Count ? _layers[ActiveIndex] : null;

    public Mask? Selection { get; internal set; }

    /// <summary>The selection a transform in progress will leave; null when there is none.</summary>
    public Mask? PreviewSelection { get; internal set; }

    /// <summary>The selection to show: the one being transformed, else the real one.</summary>
    public Mask? SelectionShown => PreviewSelection ?? Selection;

    /// <summary>Counts every change, previews included; lets a view tell whether it has seen the latest.</summary>
    public int Version { get; private set; }

    /// <summary>Counts the steps of history made, undone and redone (not previews): whether the image has changed since it was kept.</summary>
    public int Revision { get; private set; }

    /// <summary>Raised after any change, with the part of the picture it touched (empty: everything, or just the layers' state).</summary>
    public event Action<IntRect>? Changed;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public string? UndoName => _undo.Count > 0 ? _undo[^1].Name : null;

    public string? RedoName => _redo.Count > 0 ? _redo[^1].Name : null;

    public IReadOnlyList<string> UndoNames => _undo.Select(e => e.Name).Reverse().ToList();

    internal void RaiseChanged(IntRect rect)
    {
        Version++;
        Changed?.Invoke(rect);
    }

    // ---- making ------------------------------------------------------------------------------------------

    /// <summary>A new image of one layer called Background, filled with a color (transparent for none).</summary>
    public static ImageDocument Blank(int width, int height, Rgba background, string name = "")
    {
        var doc   = new ImageDocument(width, height) { Name = name };
        var pixels = new Raster(width, height);
        if (background.A > 0) pixels.Fill(background);

        doc._layers.Add(new ImageLayer("Background", pixels));
        return doc;
    }

    /// <summary>A new image whose only layer is the given picture.</summary>
    public static ImageDocument FromRaster(Raster picture, string name = "")
    {
        var doc = new ImageDocument(picture.Width, picture.Height) { Name = name };
        doc._layers.Add(new ImageLayer("Layer 1", picture));
        return doc;
    }

    // ---- history -----------------------------------------------------------------------------------------

    private void Push(ImageEdit edit)
    {
        Revision++;
        _redo.Clear();
        _undo.Add(edit);
        while (_undo.Count > MaxEdits || (_undo.Count > 1 && _undo.Sum(e => e.Bytes) > MaxBytes)) _undo.RemoveAt(0);
    }

    /// <summary>
    ///     Makes a change to the structure or the layers (anything but painting in place) as one step of the history. Changes with the same
    ///     <paramref name="mergeKey" /> made within a second of each other become one step (dragging a slider).
    /// </summary>
    internal void Do(string name, Action change, string? mergeKey = null)
    {
        var before = DocState.Capture(this);
        change();
        var after = DocState.Capture(this);
        if (mergeKey is not null && mergeKey == _mergeKey && DateTime.UtcNow - _mergeTime < TimeSpan.FromSeconds(1) && _undo.Count > 0 && _undo[^1] is StateEdit last && _redo.Count == 0)
        {
            last.After = after;
            Revision++;
        }
        else
            Push(new StateEdit(name, before, after));

        _mergeKey  = mergeKey;
        _mergeTime = DateTime.UtcNow;
        RaiseChanged(IntRect.Empty);
    }

    internal void RestoreState(DocState state)
    {
        Width  = state.Width;
        Height = state.Height;
        foreach (var l in state.Layers)
        {
            l.Layer.Name    = l.Name;
            l.Layer.Visible = l.Visible;
            l.Layer.Opacity = l.Opacity;
            l.Layer.Blend   = l.Blend;
            l.Layer.Pixels  = l.Pixels;
        }

        _layers.Clear();
        _layers.AddRange(state.Layers.Select(l => l.Layer));
        ActiveIndex = Math.Clamp(state.Active, 0, Math.Max(0, _layers.Count - 1));
        Selection   = state.Selection;
        RaiseChanged(IntRect.Empty);
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;

        var edit = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _mergeKey = null;
        Revision++;
        edit.Undo(this);
        _redo.Add(edit);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;

        var edit = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _mergeKey = null;
        Revision++;
        edit.Redo(this);
        _undo.Add(edit);
        return true;
    }

    public void ClearHistory()
    {
        _undo.Clear();
        _redo.Clear();
        _mergeKey = null;
    }

    // ---- painting in place --------------------------------------------------------------------------------

    /// <summary>A copy of the active layer's pixels, taken before painting on it, to find what the painting changed.</summary>
    public sealed class PixelSnapshot
    {
        internal PixelSnapshot(ImageLayer layer) => (Layer, Before) = (layer, layer.Pixels.Clone());

        public ImageLayer Layer { get; }

        internal Raster Before { get; }
    }

    public PixelSnapshot? BeginPaint() => ActiveLayer is { } layer ? new PixelSnapshot(layer) : null;

    /// <summary>Records what painting since <see cref="BeginPaint" /> changed as one step; nothing is recorded when nothing changed.</summary>
    public bool EndPaint(PixelSnapshot snapshot, string name)
    {
        var layer = snapshot.Layer;
        var rect  = layer.Pixels.DifferenceBounds(snapshot.Before);
        if (rect.IsEmpty) return false;

        Push(new PixelEdit(name, layer, rect, snapshot.Before.Crop(rect), layer.Pixels.Crop(rect)));
        _mergeKey = null;
        RaiseChanged(rect);
        return true;
    }

    // ---- layers ------------------------------------------------------------------------------------------

    public void SetActive(int index)
    {
        index = Math.Clamp(index, 0, Math.Max(0, _layers.Count - 1));
        if (index == ActiveIndex) return;

        ActiveIndex = index;
        RaiseChanged(IntRect.Empty);
    }

    public int IndexOf(string id) => _layers.FindIndex(l => l.Id == id);

    public string UniqueLayerName(string wanted)
    {
        if (!_layers.Any(l => l.Name == wanted)) return wanted;

        for (int i = 2;; i++)
            if (!_layers.Any(l => l.Name == $"{wanted} {i}"))
                return $"{wanted} {i}";
    }

    /// <summary>Adds a layer above the active one (and makes it the active one).</summary>
    public ImageLayer AddLayer(string name, Rgba? fill = null, Raster? pixels = null)
    {
        var layer = new ImageLayer(UniqueLayerName(string.IsNullOrWhiteSpace(name) ? "Layer" : name), pixels ?? new Raster(Width, Height));
        if (pixels is null && fill is { A: > 0 } color) layer.Pixels.Fill(color);

        Do("Add layer " + layer.Name, () =>
                                       {
                                           int at = _layers.Count == 0 ? 0 : ActiveIndex + 1;
                                           _layers.Insert(at, layer);
                                           ActiveIndex = at;
                                       });
        return layer;
    }

    public ImageLayer? DuplicateLayer(int index)
    {
        if ((uint)index >= (uint)_layers.Count) return null;

        var source = _layers[index];
        var copy   = new ImageLayer(UniqueLayerName(source.Name + " copy"), source.Pixels.Clone()) { Visible = source.Visible, Opacity = source.Opacity, Blend = source.Blend };
        Do("Duplicate layer " + source.Name, () =>
                                              {
                                                  _layers.Insert(index + 1, copy);
                                                  ActiveIndex = index + 1;
                                              });
        return copy;
    }

    public void DeleteLayer(int index)
    {
        if ((uint)index >= (uint)_layers.Count) return;

        string name = _layers[index].Name;
        Do("Delete layer " + name, () =>
                                    {
                                        _layers.RemoveAt(index);
                                        if (_layers.Count == 0) _layers.Add(new ImageLayer("Background", new Raster(Width, Height)));
                                        ActiveIndex = Math.Clamp(index - 1 < 0 ? 0 : index - 1, 0, _layers.Count - 1);
                                    });
    }

    /// <summary>Moves a layer up (toward the top; +1) or down (-1) in the stack.</summary>
    public bool MoveLayer(int index, int delta)
    {
        int to = index + delta;
        if ((uint)index >= (uint)_layers.Count || (uint)to >= (uint)_layers.Count || delta == 0) return false;

        Do("Move layer " + _layers[index].Name, () =>
                                                  {
                                                      var layer = _layers[index];
                                                      _layers.RemoveAt(index);
                                                      _layers.Insert(to, layer);
                                                      ActiveIndex = to;
                                                  });
        return true;
    }

    /// <summary>Folds a layer into the one below it.</summary>
    public bool MergeDown(int index)
    {
        if (index < 1 || index >= _layers.Count) return false;

        var top    = _layers[index];
        var bottom = _layers[index - 1];
        var merged = CompositeLayers([bottom, top], null);
        Do("Merge " + top.Name + " down", () =>
                                           {
                                               bottom.Pixels = merged;
                                               bottom.Opacity = 1;
                                               bottom.Blend   = BlendMode.Normal;
                                               bottom.Visible = true;
                                               _layers.RemoveAt(index);
                                               ActiveIndex = index - 1;
                                           });
        return true;
    }

    public void Flatten()
    {
        if (_layers.Count < 2) return;

        var flat = Composite();
        Do("Flatten image", () =>
                             {
                                 var keep = _layers[0];
                                 keep.Pixels  = flat;
                                 keep.Name    = "Background";
                                 keep.Visible = true;
                                 keep.Opacity = 1;
                                 keep.Blend   = BlendMode.Normal;
                                 _layers.RemoveRange(1, _layers.Count - 1);
                                 ActiveIndex = 0;
                             });
    }

    public void RenameLayer(int index, string name)
    {
        if ((uint)index >= (uint)_layers.Count || string.IsNullOrWhiteSpace(name) || _layers[index].Name == name.Trim()) return;

        Do("Rename layer", () => _layers[index].Name = name.Trim());
    }

    public void SetVisible(int index, bool visible)
    {
        if ((uint)index >= (uint)_layers.Count || _layers[index].Visible == visible) return;

        Do(visible ? "Show layer" : "Hide layer", () => _layers[index].Visible = visible);
    }

    public void SetOpacity(int index, double opacity)
    {
        if ((uint)index >= (uint)_layers.Count) return;

        opacity = Math.Clamp(opacity, 0, 1);
        if (Math.Abs(_layers[index].Opacity - opacity) < 0.0005) return;

        Do("Layer opacity", () => _layers[index].Opacity = opacity, "opacity:" + _layers[index].Id);
    }

    public void SetBlend(int index, BlendMode blend)
    {
        if ((uint)index >= (uint)_layers.Count || _layers[index].Blend == blend) return;

        Do("Layer blend mode", () => _layers[index].Blend = blend);
    }

    /// <summary>Replaces the pixels of a layer (a filter, a transform...).</summary>
    public void ReplacePixels(int index, Raster pixels, string name)
    {
        if ((uint)index >= (uint)_layers.Count) return;

        if (pixels.Width != Width || pixels.Height != Height) throw new ArgumentException("The picture is not the size of the image.");

        Do(name, () => _layers[index].Pixels = pixels);
    }

    /// <summary>Runs a filter on the active layer, only on what is selected (all of it when nothing is).</summary>
    public void ApplyFilter(string name, Func<Raster, Raster> filter)
    {
        var layer = ActiveLayer;
        if (layer is null) return;

        var result = ImageFilters.Masked(layer.Pixels, Selection, filter);
        ReplacePixels(ActiveIndex, result, name);
    }

    /// <summary>Paints the selection (all of the layer when nothing is selected) with a color; transparent erases it.</summary>
    public void FillSelection(Rgba color, string name)
    {
        var layer = ActiveLayer;
        if (layer is null) return;

        var result = layer.Pixels.Clone();
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            double m = Selection is null ? 1 : Selection.Values[y * Width + x] / 255.0;
            if (m <= 0) continue;

            if (color.A == 0) result.Erase(x, y, m);
            else result.Over(x, y, color, m);
        }

        ReplacePixels(ActiveIndex, result, name);
    }

    // ---- selection ---------------------------------------------------------------------------------------

    public void SetSelection(Mask? mask, string name = "Select")
    {
        if (mask is { } m && (m.Width != Width || m.Height != Height)) throw new ArgumentException("The selection is not the size of the image.");

        if (mask is not null && mask.IsEmpty) mask = null;
        if (ReferenceEquals(mask, Selection)) return;

        Do(name, () => Selection = mask);
    }

    /// <summary>Combines a new shape with the selection there is.</summary>
    public void Select(Mask shape, SelectionMode mode, string name = "Select")
    {
        Mask result = mode == SelectionMode.Replace ? shape
                      : Selection is null           ? (mode == SelectionMode.Subtract ? Mask.Empty(Width, Height) : shape)
                                                    : Selection.Combine(shape, mode);
        SetSelection(result, name);
    }

    public void SelectAll() => SetSelection(Mask.Full(Width, Height), "Select all");

    public void SelectNone() => SetSelection(null, "Deselect");

    public void InvertSelection()
    {
        SetSelection(Selection is null ? Mask.Full(Width, Height) : Selection.Invert(), "Invert selection");
    }

    public void SelectLayerContents()
    {
        if (ActiveLayer is { } layer) SetSelection(Mask.FromAlpha(layer.Pixels), "Select layer contents");
    }

    // ---- the picture as a whole -----------------------------------------------------------------------------

    /// <summary>Changes the size of the canvas without scaling anything: the layers keep their pixels, placed by the anchor (0 to 1 across and down).</summary>
    public void ResizeCanvas(int width, int height, double anchorX = 0.5, double anchorY = 0.5)
    {
        Raster.CheckSize(width, height);
        int dx = (int)Math.Round((width - Width) * Math.Clamp(anchorX, 0, 1)), dy = (int)Math.Round((height - Height) * Math.Clamp(anchorY, 0, 1));
        var layers = _layers.Select(l =>
                                    {
                                        var r = new Raster(width, height);
                                        r.Paste(l.Pixels, dx, dy);
                                        return r;
                                    }).ToList();
        var selection = Selection?.Place(width, height, dx, dy);
        Do("Canvas size", () =>
                           {
                               for (int i = 0; i < _layers.Count; i++) _layers[i].Pixels = layers[i];
                               Width     = width;
                               Height    = height;
                               Selection = selection;
                           });
    }

    /// <summary>Scales the whole image (every layer and the selection).</summary>
    public void ScaleImage(int width, int height)
    {
        Raster.CheckSize(width, height);
        var layers    = _layers.Select(l => l.Pixels.Resize(width, height)).ToList();
        var selection = Selection?.Resize(width, height);
        Do("Image size", () =>
                          {
                              for (int i = 0; i < _layers.Count; i++) _layers[i].Pixels = layers[i];
                              Width     = width;
                              Height    = height;
                              Selection = selection;
                          });
    }

    /// <summary>Cuts the image down to the rectangle that holds the selection.</summary>
    public bool CropToSelection()
    {
        if (Selection is null) return false;

        var rect = Selection.Bounds();
        if (rect.IsEmpty) return false;

        var layers    = _layers.Select(l => l.Pixels.Crop(rect)).ToList();
        var selection = Selection.Crop(rect);
        Do("Crop to selection", () =>
                                 {
                                     for (int i = 0; i < _layers.Count; i++) _layers[i].Pixels = layers[i];
                                     Width     = rect.Width;
                                     Height    = rect.Height;
                                     Selection = selection;
                                 });
        return true;
    }

    public void FlipImage(bool horizontal)
    {
        var layers    = _layers.Select(l => horizontal ? l.Pixels.FlipHorizontal() : l.Pixels.FlipVertical()).ToList();
        var selection = Selection is null ? null : horizontal ? Selection.FlipHorizontal() : Selection.FlipVertical();
        Do(horizontal ? "Flip image across" : "Flip image up and down", () =>
                                                                          {
                                                                              for (int i = 0; i < _layers.Count; i++) _layers[i].Pixels = layers[i];
                                                                              Selection = selection;
                                                                          });
    }

    public void RotateImage90(bool clockwise)
    {
        var layers    = _layers.Select(l => l.Pixels.Rotate90(clockwise)).ToList();
        var selection = Selection?.Rotate90(clockwise);
        int w = Height, h = Width;
        Do("Turn image", () =>
                          {
                              for (int i = 0; i < _layers.Count; i++) _layers[i].Pixels = layers[i];
                              Width     = w;
                              Height    = h;
                              Selection = selection;
                          });
    }

    /// <summary>Flips one layer's pixels.</summary>
    public void FlipLayer(int index, bool horizontal)
    {
        if ((uint)index >= (uint)_layers.Count) return;

        var l = _layers[index];
        ReplacePixels(index, horizontal ? l.Pixels.FlipHorizontal() : l.Pixels.FlipVertical(), horizontal ? "Flip layer across" : "Flip layer up and down");
    }

    // ---- compositing -----------------------------------------------------------------------------------------

    /// <summary>All visible layers laid over each other (over transparent, or over <paramref name="background" />).</summary>
    public Raster Composite(Rgba? background = null) => CompositeLayers(_layers.Where(l => l.Visible).ToList(), background, true);

    /// <summary>The same for a part of the picture only, written into <paramref name="into" /> (the size of the image); for redrawing what changed.</summary>
    public void CompositeInto(Raster into, IntRect area)
    {
        area = area.Intersect(new IntRect(0, 0, Width, Height));
        if (area.IsEmpty) return;

        BlendLayers(_layers.Where(l => l.Visible).ToList(), into, area, null, true);
    }

    private Raster CompositeLayers(IReadOnlyList<ImageLayer> layers, Rgba? background, bool useVisibility = false)
    {
        var result = new Raster(Width, Height);
        BlendLayers(layers, result, new IntRect(0, 0, Width, Height), background, useVisibility);
        return result;
    }

    private static void BlendLayers(IReadOnlyList<ImageLayer> layers, Raster into, IntRect area, Rgba? background, bool shown)
    {
        for (int y = area.Y; y < area.Bottom; y++)
        for (int x = area.X; x < area.Right; x++)
        {
            double br = 0, bg = 0, bb = 0, ba = 0;
            if (background is { } back)
            {
                br = back.R / 255.0;
                bg = back.G / 255.0;
                bb = back.B / 255.0;
                ba = back.A / 255.0;
            }

            int i = (y * into.Width + x) * 4;
            foreach (var layer in layers)
            {
                var pixels = shown ? layer.Shown : layer.Pixels;
                if (pixels.Width != into.Width || pixels.Height != into.Height) continue;

                double sa = pixels.Pixels[i + 3] / 255.0 * layer.Opacity;
                if (sa <= 0) continue;

                double sr = pixels.Pixels[i + 2] / 255.0, sg = pixels.Pixels[i + 1] / 255.0, sb = pixels.Pixels[i] / 255.0;
                if (layer.Blend != BlendMode.Normal && ba > 0)
                {
                    sr = (1 - ba) * sr + ba * Mix(layer.Blend, br, sr);
                    sg = (1 - ba) * sg + ba * Mix(layer.Blend, bg, sg);
                    sb = (1 - ba) * sb + ba * Mix(layer.Blend, bb, sb);
                }

                double oa = sa + ba * (1 - sa);
                if (oa <= 0) continue;

                br = (sr * sa + br * ba * (1 - sa)) / oa;
                bg = (sg * sa + bg * ba * (1 - sa)) / oa;
                bb = (sb * sa + bb * ba * (1 - sa)) / oa;
                ba = oa;
            }

            into.Pixels[i]     = (byte)Math.Clamp((int)Math.Round(bb * 255), 0, 255);
            into.Pixels[i + 1] = (byte)Math.Clamp((int)Math.Round(bg * 255), 0, 255);
            into.Pixels[i + 2] = (byte)Math.Clamp((int)Math.Round(br * 255), 0, 255);
            into.Pixels[i + 3] = (byte)Math.Clamp((int)Math.Round(ba * 255), 0, 255);
        }
    }

    private static double Mix(BlendMode mode, double b, double s)
    {
        return mode switch
               {
                   BlendMode.Multiply   => b * s,
                   BlendMode.Screen     => b + s - b * s,
                   BlendMode.Overlay    => b <= 0.5 ? 2 * b * s : 1 - 2 * (1 - b) * (1 - s),
                   BlendMode.Darken     => Math.Min(b, s),
                   BlendMode.Lighten    => Math.Max(b, s),
                   BlendMode.Difference => Math.Abs(b - s),
                   _                    => s
               };
    }

    /// <summary>The topmost visible layer that has something at a pixel (index), or -1.</summary>
    public int LayerAt(int x, int y)
    {
        for (int i = _layers.Count - 1; i >= 0; i--)
            if (_layers[i].Visible && _layers[i].Pixels.Get(x, y).A > 0)
                return i;

        return -1;
    }

    // ---- keeping ---------------------------------------------------------------------------------------------

    public ImageData ToData()
    {
        var data = new ImageData { Name = Name, Width = Width, Height = Height, Active = ActiveIndex };
        foreach (var l in _layers)
            data.Layers.Add(new ImageLayerData { Name = l.Name, Visible = l.Visible, Opacity = l.Opacity, Blend = l.Blend.ToString(), Pixels = Pack(l.Pixels.Pixels) });

        if (Selection is not null) data.Selection = Pack(Selection.Values);
        return data;
    }

    public static ImageDocument FromData(ImageData data)
    {
        if (data.Layers.Count == 0) throw new InvalidOperationException("The image has no layers.");

        var doc = new ImageDocument(data.Width, data.Height) { Name = data.Name };
        foreach (var l in data.Layers)
        {
            var bytes = Unpack(l.Pixels, data.Width * data.Height * 4);
            doc._layers.Add(new ImageLayer(string.IsNullOrWhiteSpace(l.Name) ? "Layer" : l.Name, new Raster(data.Width, data.Height, bytes))
                            {
                                Visible = l.Visible, Opacity = Math.Clamp(l.Opacity, 0, 1), Blend = Enum.TryParse<BlendMode>(l.Blend, out var b) ? b : BlendMode.Normal
                            });
        }

        doc.ActiveIndex = Math.Clamp(data.Active, 0, doc._layers.Count - 1);
        if (!string.IsNullOrEmpty(data.Selection)) doc.Selection = Mask.FromValues(data.Width, data.Height, Unpack(data.Selection, data.Width * data.Height));
        return doc;
    }

    private static string Pack(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Fastest, true)) z.Write(bytes, 0, bytes.Length);

        return Convert.ToBase64String(output.ToArray());
    }

    private static byte[] Unpack(string text, int expected)
    {
        using var input = new MemoryStream(Convert.FromBase64String(text));
        using var z     = new ZLibStream(input, CompressionMode.Decompress);
        var       bytes = new byte[expected];
        int       read  = 0;
        while (read < expected)
        {
            int n = z.Read(bytes, read, expected - read);
            if (n == 0) break;

            read += n;
        }

        if (read != expected) throw new InvalidOperationException("The pixels of the image are not the size they should be.");

        return bytes;
    }
}

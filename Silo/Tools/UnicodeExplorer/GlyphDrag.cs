using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Silo.Unicode;

namespace Silo.Tools.UnicodeExplorer;

/// <summary>
///     Dragging characters between the explorer's results, its collection and the palettes. The data carries the characters as text too, so they can
///     be dropped into any other program; inside Silo it also says where they came from, so a drop on the list they came from moves them instead of
///     copying them.
/// </summary>
internal static class GlyphDrag
{
    /// <summary>The data format of the characters being dragged (<see cref="Payload" />, only within this process).</summary>
    public const string Format = "Silo.Glyphs";

    /// <summary>
    ///     What is dragged: the characters, the list they came from (null: from somewhere that is not a list, like the results or another program) and
    ///     where in that list they were.
    /// </summary>
    public sealed record Payload(IReadOnlyList<int> CodePoints, object? Source, IReadOnlyList<int> Indexes);

    /// <summary>The data for a drag: the payload, and the characters as plain text.</summary>
    public static DataObject Create(IReadOnlyList<int> codePoints, object? source = null, IReadOnlyList<int>? indexes = null)
    {
        var data = new DataObject();
        data.SetData(Format, new Payload(codePoints, source, indexes ?? []), false);
        data.SetText(GlyphClipboard.ForCopy(string.Concat(codePoints.Select(cp => new CodePoint(cp).Text))));
        return data;
    }

    /// <summary>What a drop carries: a payload from Silo, else the characters of dropped text (from another program), else null.</summary>
    public static Payload? Read(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(Format) && data.GetData(Format) is Payload payload) return payload;

            if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text && text.Length > 0)
            {
                var codePoints = CodePoint.Enumerate(text).Select(c => c.Value).Where(v => v > 0xFFFF || !char.IsWhiteSpace((char)v)).ToList();
                return codePoints.Count == 0 ? null : new Payload(codePoints, null, []);
            }
        }
        catch (Exception)
        {
            // data another program offers but cannot give
        }

        return null;
    }

    /// <summary>Whether the mouse has moved far enough from where the button went down to start a drag.</summary>
    public static bool FarEnough(Point start, Point now)
    {
        return Math.Abs(now.X - start.X) >= SystemParameters.MinimumHorizontalDragDistance ||
               Math.Abs(now.Y - start.Y) >= SystemParameters.MinimumVerticalDragDistance;
    }

    /// <summary>
    ///     The index a drop at <paramref name="point" /> (in <paramref name="panel" />'s coordinates) inserts at, for tiles laid out in reading order
    ///     (a WrapPanel): before the tile under the point when the point is on its left half, after it on its right half.
    /// </summary>
    public static int DropIndex(Panel panel, Point point, int? total = null) => Spot(panel, point, total).Index;

    /// <summary>
    ///     The place of a tile or a group header in the list it shows (the item index), for panels that show less than the whole list (a folded group
    ///     leaves out its tiles) or have headers among the tiles. A child without one counts by its place among the children.
    /// </summary>
    public static readonly DependencyProperty IndexProperty = DependencyProperty.RegisterAttached("Index", typeof(int), typeof(GlyphDrag), new PropertyMetadata(-1));

    /// <summary>Marks a child as a group header: a drop on it goes to the start of its group.</summary>
    public static readonly DependencyProperty IsHeaderProperty = DependencyProperty.RegisterAttached("IsHeader", typeof(bool), typeof(GlyphDrag), new PropertyMetadata(false));

    public static int GetIndex(DependencyObject d) => (int)d.GetValue(IndexProperty);

    public static void SetIndex(DependencyObject d, int index) => d.SetValue(IndexProperty, index);

    public static bool GetIsHeader(DependencyObject d) => (bool)d.GetValue(IsHeaderProperty);

    public static void SetIsHeader(DependencyObject d, bool header) => d.SetValue(IsHeaderProperty, header);

    /// <summary>Where a drop lands (<see cref="Index" />), and which item's group the dropped characters join (<see cref="GroupFrom" />; -1 for none).</summary>
    public readonly record struct DropSpot(int Index, int GroupFrom);

    /// <summary>
    ///     Where a drop at <paramref name="point" /> lands, and whose group it takes: the group of a header dropped on, else of the tile under the
    ///     point, else (on empty space) of the tile before the place. <paramref name="total" /> is how many items the list has (the end, when the
    ///     point is past every child); it defaults to the number of children.
    /// </summary>
    public static DropSpot Spot(Panel panel, Point point, int? total = null)
    {
        var  children = panel.Children;
        int? hit      = null; // the tile the point is over
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] is not FrameworkElement child || child.Visibility != Visibility.Visible) continue;

            int index   = GetIndex(child) is var set and >= 0 ? set : i;
            var topLeft = child.TranslatePoint(new Point(0, 0), panel);
            var box     = new Rect(topLeft, new Size(child.ActualWidth, child.ActualHeight));

            if (GetIsHeader(child))
            {
                if (point.Y <= box.Bottom) return new DropSpot(index, index); // on the header, or above it: the start of its group
                continue;
            }

            if (box.Contains(point)) hit = index;
            if (point.Y < box.Top) return new DropSpot(index, hit ?? index);                                       // above this row: before its first tile
            if (point.Y <= box.Bottom && point.X < box.Left + box.Width / 2) return new DropSpot(index, hit ?? index);
        }

        int end = total ?? children.Count;
        return new DropSpot(end, hit ?? Math.Max(-1, end - 1));
    }

    /// <summary>
    ///     Shows where a drop will land: an accent bar before the tile at <paramref name="index" />, or after the last tile for the end. Drawn in the
    ///     panel's adorner layer, so the tiles themselves are not touched; one marker per panel, until <see cref="Unmark" />.
    /// </summary>
    public static void Mark(Panel panel, int index)
    {
        var visible = panel.Children.OfType<FrameworkElement>().ToList();
        if (visible.Count == 0)
        {
            Unmark(panel);
            return;
        }

        // the first child at or after the place (by item index: a folded group's tiles are not there); the end is after the last child
        int found  = visible.FindIndex(c => (GetIndex(c) is var set and >= 0 ? set : visible.IndexOf(c)) >= index);
        bool end   = found < 0;
        var  tile  = visible[end ? visible.Count - 1 : found];
        var  topLeft = tile.TranslatePoint(new Point(0, 0), panel);
        double x   = end ? topLeft.X + tile.ActualWidth + tile.Margin.Right : topLeft.X - tile.Margin.Left;
        var  line  = (new Point(x, topLeft.Y), new Point(x, topLeft.Y + tile.ActualHeight));

        if (Marked.TryGetValue(panel, out var marker))
        {
            marker.Move(line.Item1, line.Item2);
            return;
        }

        if (AdornerLayer.GetAdornerLayer(panel) is not { } layer) return;

        marker = new InsertMarker(panel, line.Item1, line.Item2);
        layer.Add(marker);
        Marked[panel] = marker;
    }

    /// <summary>Takes the drop marker of a panel away.</summary>
    public static void Unmark(Panel panel)
    {
        if (!Marked.Remove(panel, out var marker)) return;

        AdornerLayer.GetAdornerLayer(panel)?.Remove(marker);
    }

    private static readonly Dictionary<Panel, InsertMarker> Marked = [];

    /// <summary>The bar that shows where a drop lands.</summary>
    private sealed class InsertMarker : Adorner
    {
        private Point _from, _to;

        public InsertMarker(UIElement panel, Point from, Point to) : base(panel)
        {
            (_from, _to)     = (from, to);
            IsHitTestVisible = false;
        }

        public void Move(Point from, Point to)
        {
            if (from == _from && to == _to) return;

            (_from, _to) = (from, to);
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            var brush = (AdornedElement as FrameworkElement)?.TryFindResource("Theme.Accent") as Brush ?? Brushes.DodgerBlue;
            dc.DrawLine(new Pen(brush, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, _from, _to);
        }
    }
}

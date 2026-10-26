using System.Linq;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Silo.Ui.Panes;

namespace Silo.TextEditing;

/// <summary>A problem placed in the document: the stretch it covers, as offsets.</summary>
internal sealed class TextMarker(int start, int end, TextDiagnostic diagnostic) : ISegment
{
    public int            StartOffset { get; } = start;
    public int            EndOffset   { get; } = end;
    public int            Offset      => StartOffset;
    public int            Length      => EndOffset - StartOffset;
    public TextDiagnostic Diagnostic  { get; } = diagnostic;
}

/// <summary>Draws a wavy line under each problem in the text: red for errors, amber for warnings, grey for messages.</summary>
internal sealed class DiagnosticRenderer : IBackgroundRenderer
{
    private static readonly Pen ErrorPen   = Wave(Color.FromRgb(0xE5, 0x48, 0x4D));
    private static readonly Pen WarningPen = Wave(Color.FromRgb(0xD9, 0xA4, 0x1E));
    private static readonly Pen InfoPen    = Wave(Color.FromRgb(0x8A, 0x8F, 0x98));

    private IReadOnlyList<TextMarker> _markers = [];

    public KnownLayer Layer => KnownLayer.Selection;

    public IReadOnlyList<TextMarker> Markers => _markers;

    /// <summary>Places the problems in <paramref name="document" /> and redraws.</summary>
    public void Set(TextDocument document, IEnumerable<TextDiagnostic> diagnostics, TextView view)
    {
        _markers = diagnostics.Select(d => Place(document, d)).ToList();
        view.InvalidateLayer(Layer);
    }

    public void Clear(TextView view)
    {
        _markers = [];
        view.InvalidateLayer(Layer);
    }

    /// <summary>The problems at <paramref name="offset" />, worst first (for the tooltip).</summary>
    public IEnumerable<TextMarker> At(int offset) =>
        _markers.Where(m => offset >= m.StartOffset && offset <= m.EndOffset).OrderByDescending(m => m.Diagnostic.Severity);

    /// <summary>
    ///     Where a problem is in the document. A problem with no length covers the word (or the one character) at its place; one at
    ///     the very end of a line covers the character before it, so it can be seen.
    /// </summary>
    public static TextMarker Place(TextDocument document, TextDiagnostic d)
    {
        int lineNumber = Math.Clamp(d.Line, 1, document.LineCount);
        var line       = document.GetLineByNumber(lineNumber);
        int start      = line.Offset + Math.Clamp(d.Column - 1, 0, line.Length);
        int end;
        if (d.Length > 0)
        {
            end = Math.Min(start + d.Length, document.TextLength);
        }
        else
        {
            end = start;
            while (end < line.EndOffset && (char.IsLetterOrDigit(document.GetCharAt(end)) || document.GetCharAt(end) is '_' or '-' or '.')) end++;
            if (end == start) end = Math.Min(start + 1, line.EndOffset);
            if (end == start && start > line.Offset) start--;
        }

        return new TextMarker(start, Math.Max(end, start), d);
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_markers.Count == 0 || !textView.VisualLinesValid) return;

        var visible = textView.VisualLines;
        if (visible.Count == 0) return;

        int first = visible[0].FirstDocumentLine.Offset;
        int last  = visible[^1].LastDocumentLine.EndOffset;

        foreach (var marker in _markers)
        {
            if (marker.EndOffset < first || marker.StartOffset > last) continue;

            var pen = marker.Diagnostic.Severity switch
                      {
                          ProblemSeverity.Error   => ErrorPen,
                          ProblemSeverity.Warning => WarningPen,
                          _                       => InfoPen
                      };

            // an empty stretch (a problem at the end of an empty line) still gets a short line
            ISegment segment = marker.Length > 0 ? marker : new TextSegment { StartOffset = marker.StartOffset, Length = 0 };
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
            {
                double width = Math.Max(rect.Width, 6);
                var    wave  = new StreamGeometry();
                using (var g = wave.Open())
                {
                    double y = rect.Bottom - 1;
                    g.BeginFigure(new Point(rect.Left, y), false, false);
                    bool up = true;
                    for (double x = rect.Left + 2; x <= rect.Left + width + 0.5; x += 2)
                    {
                        g.LineTo(new Point(x, up ? y - 2 : y), true, false);
                        up = !up;
                    }
                }

                wave.Freeze();
                drawingContext.DrawGeometry(null, pen, wave);
            }
        }
    }

    private static Pen Wave(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var pen = new Pen(brush, 1);
        pen.Freeze();
        return pen;
    }
}

/// <summary>Outlines the bracket (or XML tag name) at the caret and its partner.</summary>
internal sealed class MatchRenderer : IBackgroundRenderer
{
    private MatchPair? _pair;

    public KnownLayer Layer => KnownLayer.Selection;

    public Brush? Fill { get; set; }

    public Pen? Outline { get; set; }

    public void Set(MatchPair? pair, TextView view)
    {
        if (_pair == pair) return;

        _pair = pair;
        view.InvalidateLayer(Layer);
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_pair is not { } pair || !textView.VisualLinesValid) return;

        var builder = new BackgroundGeometryBuilder { CornerRadius = 1, AlignToWholePixels = true };
        builder.AddSegment(textView, new TextSegment { StartOffset = pair.AStart, Length = pair.ALength });
        builder.CloseFigure();
        builder.AddSegment(textView, new TextSegment { StartOffset = pair.BStart, Length = pair.BLength });
        if (builder.CreateGeometry() is { } geometry) drawingContext.DrawGeometry(Fill, Outline, geometry);
    }
}

using System.Globalization;
using System.Windows.Documents;
using System.Windows.Media;
using Silo.Unicode;

namespace Silo.Tools.UnicodeExplorer;

/// <summary>
///     Lays a collection of characters out on pages for the printer (and the PDF printer): a grid of cells, each with the character, and under it its
///     code point and name when the page setup asks for captions. The marks are drawn the way the explorer's color mode says.
/// </summary>
internal sealed class CollectionPaginator : DocumentPaginator
{
    private const double Dip = 96; // WPF's device independent pixels are 1/96 inch

    private readonly IReadOnlyList<CollectedCharacter> _items;
    private readonly UnicodeDatabase?                  _db;
    private readonly PageSetup                         _page;
    private readonly string                            _title;
    private readonly Func<int, Color?>                 _color;
    private readonly ColorMode                         _mode;
    private readonly double                            _margin;
    private readonly double                            _cellWidth;
    private readonly double                            _cellHeight;
    private readonly int                               _columns;
    private readonly int                               _rowsPerPage;
    private Size                                       _size;

    public CollectionPaginator(IReadOnlyList<CollectedCharacter> items, UnicodeDatabase? db, PageSetup page, string title, Func<int, Color?> color, ColorMode mode, Size paper)
    {
        _items = items;
        _db    = db;
        _page  = page.Normalized();
        _title = title;
        _color = color;
        _mode  = mode;

        _margin = _page.MarginHundredthsInch / 100.0 * Dip;
        // the page is the paper the other way round for landscape; a printer that already reports it that way is left alone
        _size = _page.Landscape == paper.Width < paper.Height ? new Size(paper.Height, paper.Width) : paper;

        _cellWidth  = _page.Captions ? Math.Max(_page.GlyphSize * 2.2, 92) : _page.GlyphSize * 1.9;
        _cellHeight = _page.GlyphSize * 1.7 + (_page.Captions ? 30 : 0);

        double usableWidth  = Math.Max(_cellWidth, _size.Width - 2 * _margin);
        double usableHeight = Math.Max(_cellHeight, _size.Height - 2 * _margin - HeaderHeight - FooterHeight);
        _columns     = Math.Max(1, (int)(usableWidth / _cellWidth));
        _rowsPerPage = Math.Max(1, (int)(usableHeight / _cellHeight));
    }

    private static double HeaderHeight => 26;
    private static double FooterHeight => 18;

    private int PerPage => _columns * _rowsPerPage;

    public override bool IsPageCountValid => true;

    public override int PageCount => Math.Max(1, (_items.Count + PerPage - 1) / PerPage);

    public override Size PageSize
    {
        get => _size;
        set => _size = value;
    }

    public override IDocumentPaginatorSource? Source => null;

    public override DocumentPage GetPage(int pageNumber)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            double pixelsPerDip = 1.0;
            DrawText(dc, _title, 13, FontWeights.SemiBold, Brushes.Black, new Point(_margin, _margin), pixelsPerDip);
            DrawText(dc, $"Page {pageNumber + 1} of {PageCount}", 8, FontWeights.Normal, Brushes.Gray, new Point(_margin, _size.Height - _margin - 10), pixelsPerDip);

            double top   = _margin + HeaderHeight;
            int    first = pageNumber * PerPage;
            var    line  = new Pen(new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)), 0.75);
            line.Freeze();

            for (int i = 0; i < PerPage && first + i < _items.Count; i++)
            {
                var    item = _items[first + i];
                int    col  = i % _columns, row = i / _columns;
                double x    = _margin + col * _cellWidth, y = top + row * _cellHeight;
                DrawCell(dc, item, x, y, line, pixelsPerDip);
            }
        }

        return new DocumentPage(visual, _size, new Rect(_size), new Rect(_size));
    }

    private void DrawCell(DrawingContext dc, CollectedCharacter item, double x, double y, Pen line, double pixelsPerDip)
    {
        var    cp    = item.CodePoint;
        var    rect  = new Rect(x + 1, y + 1, _cellWidth - 2, _cellHeight - 2);
        Color? color = _page.Colors ? _color(cp.Value) : null;

        Brush glyphBrush = Brushes.Black;
        if (color is { } c)
            switch (_mode)
            {
                case ColorMode.Background:
                    dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x55, c.R, c.G, c.B)), null, rect);
                    break;
                case ColorMode.Glyph:
                    glyphBrush = new SolidColorBrush(c);
                    break;
            }

        dc.DrawRectangle(null, line, rect);

        var glyph = new FormattedText(_db?.Get(cp).Glyph ?? cp.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(UiKit.GlyphFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                                      _page.GlyphSize, glyphBrush, pixelsPerDip);
        double glyphTop = y + 4;
        dc.DrawText(glyph, new Point(x + (_cellWidth - Math.Min(glyph.WidthIncludingTrailingWhitespace, _cellWidth)) / 2, glyphTop));

        if (color is { } bar && _mode == ColorMode.Accent)
            dc.DrawRoundedRectangle(new SolidColorBrush(bar), null, new Rect(x + 6, y + _page.GlyphSize * 1.5 + 3, _cellWidth - 12, 3), 1, 1);

        if (!_page.Captions) return;

        double captionTop = y + _page.GlyphSize * 1.7;
        DrawCentered(dc, cp.Hex, 7.5, Brushes.Black, x, captionTop, pixelsPerDip);
        string name = !string.IsNullOrEmpty(item.Label) ? item.Label! : _db?.Get(cp).Name ?? string.Empty;
        if (name.Length > 0) DrawCentered(dc, name, 6.5, Brushes.Gray, x, captionTop + 11, pixelsPerDip);
    }

    private void DrawCentered(DrawingContext dc, string text, double size, Brush brush, double x, double y, double pixelsPerDip)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, pixelsPerDip)
                        {
                            MaxTextWidth = _cellWidth - 6, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center
                        };
        dc.DrawText(formatted, new Point(x + 3, y));
    }

    private static void DrawText(DrawingContext dc, string text, double size, FontWeight weight, Brush brush, Point at, double pixelsPerDip)
    {
        dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal), size, brush, pixelsPerDip), at);
    }
}

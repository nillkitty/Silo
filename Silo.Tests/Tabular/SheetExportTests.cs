using Silo.Tabular;

namespace Silo.Tests.Tabular;

public class InlineMarkupTests
{
    [TestCase("**bold** *x* [link](http://a.b)", "bold x link (http://a.b)")]
    [TestCase("a * b ** c", "a * b ** c")]
    [TestCase("2 * 3 * 4", "2 * 3 * 4")]
    [TestCase("my_var_name", "my_var_name")]
    [TestCase(@"\*not bold\*", "*not bold*")]
    public void ToPlain_flattens_formatting_and_keeps_stray_markers(string markup, string expected)
    {
        Assert.That(InlineMarkup.ToPlain(markup), Is.EqualTo(expected));
    }

    [TestCase("**b** *i* `c*d` ~~s~~ [l](http://x)", "<strong>b</strong> <em>i</em> <code>c*d</code> <del>s</del> <a href=\"http://x\">l</a>")]
    [TestCase("***x***", "<strong><em>x</em></strong>")]
    [TestCase("_hi_ there", "<em>hi</em> there")]
    [TestCase("<b> & x\ny", "&lt;b&gt; &amp; x<br>y")]
    [TestCase("[**b**](u)", "<a href=\"u\"><strong>b</strong></a>")]
    public void ToHtml_encodes_text_and_tags_formatting(string markup, string expected)
    {
        Assert.That(InlineMarkup.ToHtml(markup), Is.EqualTo(expected));
    }

    [Test]
    public void ToggleWrap_wraps_then_unwraps()
    {
        var wrapped = InlineMarkup.ToggleWrap("hello world", 0, 5, "**");
        Assert.That((wrapped.Text, wrapped.Start, wrapped.Length), Is.EqualTo(("**hello** world", 2, 5)));

        var inner = InlineMarkup.ToggleWrap("**hello** world", 2, 5, "**");
        Assert.That((inner.Text, inner.Start, inner.Length), Is.EqualTo(("hello world", 0, 5)));

        var outer = InlineMarkup.ToggleWrap("**hello** world", 0, 9, "**");
        Assert.That((outer.Text, outer.Start, outer.Length), Is.EqualTo(("hello world", 0, 5)));
    }
}

public class SheetExportTests
{
    private static SheetData Sample()
    {
        var s = new SheetData();
        s.Columns.Add(new SheetColumn { Name = "Name", Type = CellType.RichText });
        s.Columns.Add(new SheetColumn { Name = "Qty", Type = CellType.Number });
        s.Columns.Add(new SheetColumn { Name = "OK", Type = CellType.Boolean });
        s.Columns.Add(new SheetColumn { Name = "Note|x", Type = CellType.Text });
        s.Rows.Add(["**Widget** a|b", "12", "true", "a*b_c"]);
        s.Rows.Add(["Gadget\nline2", "3.5", "no", "say \"hi\"\ttab"]);
        return s.Normalize();
    }

    [Test]
    public void Markdown_is_a_padded_aligned_pipe_table()
    {
        string[] lines = SheetExport.ToMarkdown(Sample()).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.That(lines, Has.Length.EqualTo(4));
        Assert.That(lines[0], Is.EqualTo("| Name            | Qty |  OK  | Note\\|x      |"));
        Assert.That(lines[1], Is.EqualTo("| --------------- | --: | :--: | ------------ |"));
        Assert.That(lines[2], Is.EqualTo("| **Widget** a\\|b |  12 | true | a\\*b\\_c      |"));
        Assert.That(lines[3], Does.Contain("Gadget<br>line2"));
    }

    [Test]
    public void Markdown_keeps_rich_text_but_escapes_literal_text()
    {
        string md = SheetExport.ToMarkdown(Sample());
        Assert.That(md, Does.Contain("**Widget**"));
        Assert.That(md, Does.Contain("a\\*b\\_c"));
    }

    [Test]
    public void Tsv_quotes_cells_with_tabs_quotes_and_line_breaks_and_flattens_rich_text()
    {
        string tsv = SheetExport.ToTsv(Sample());
        Assert.That(tsv, Does.StartWith("Name\tQty\tOK\tNote|x\nWidget a|b\t12\ttrue\ta*b_c\n"));
        Assert.That(tsv, Does.Contain("\"Gadget\nline2\"\t3.5\tno\t\"say \"\"hi\"\"\ttab\""));
    }

    [Test]
    public void Html_has_header_cells_alignment_and_formatting()
    {
        string html = SheetExport.ToHtml(Sample());
        Assert.That(html, Does.Contain("<th style=\"text-align:right;\">Qty</th>"));
        Assert.That(html, Does.Contain("<strong>Widget</strong>"));
        Assert.That(html, Does.Contain("Gadget<br>line2"));
    }

    [Test]
    public void Json_writes_numbers_and_booleans_natively()
    {
        string json = SheetExport.ToJson(Sample());
        Assert.That(json, Does.Contain("\"Qty\": 12"));
        Assert.That(json, Does.Contain("\"OK\": true"));
        Assert.That(json, Does.Contain("\"OK\": false"));
    }

    [Test]
    public void Json_round_trips_the_sheet()
    {
        var sheet = Sample();
        Assert.That(SheetData.FromJson(sheet.ToJson()).ToJson(), Is.EqualTo(sheet.ToJson()));
    }

    [Test]
    public void Trailing_blank_rows_are_dropped_for_export()
    {
        var s = SheetData.Create(CellType.Text, 2, 4);
        s.Rows[0][0] = "x";
        Assert.That(s.TrimTrailingBlankRows().Rows, Has.Count.EqualTo(1));
    }

    [Test]
    public void Column_edits_keep_cells_aligned()
    {
        var s = Sample();
        s.MoveColumn(0, 2);
        Assert.That(s.Columns.Select(c => c.Name), Is.EqualTo(new[] { "Qty", "OK", "Name", "Note|x" }));
        Assert.That(s.Rows[0], Is.EqualTo(new[] { "12", "true", "**Widget** a|b", "a*b_c" }));

        s.RemoveColumn(1);
        Assert.That(s.Rows[0], Is.EqualTo(new[] { "12", "**Widget** a|b", "a*b_c" }));

        s.InsertColumn(0, new SheetColumn { Name = s.NextColumnName() });
        Assert.That(s.Rows[1], Has.Length.EqualTo(4));
        Assert.That(s.Rows[1][0], Is.EqualTo(string.Empty));
    }

    [Test]
    public void Clipboard_html_header_offsets_point_at_the_fragment()
    {
        string cf = SheetClipboard.ToCfHtml("<table></table>");
        var    m  = System.Text.RegularExpressions.Regex.Match(cf, @"StartFragment:(\d+)\r\nEndFragment:(\d+)");
        byte[] b  = System.Text.Encoding.UTF8.GetBytes(cf);
        int    a  = int.Parse(m.Groups[1].Value), e = int.Parse(m.Groups[2].Value);
        Assert.That(System.Text.Encoding.UTF8.GetString(b, a, e - a), Is.EqualTo("<table></table>"));
    }
}

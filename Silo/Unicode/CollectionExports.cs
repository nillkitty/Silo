using System.Globalization;
using System.Net;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json;

namespace Silo.Unicode;

/// <summary>
///     The other ways to write a collection: XML, YAML and an HTML gallery (with <see cref="CollectionFormats" />' text, JSON, TSV, CSV and code point
///     list, they are the formats the Unicode Explorer exports to). XML can be read back; YAML is read through the application's YAML reader (see
///     <see cref="CollectionFormats.FromJson" />).
/// </summary>
public static class CollectionExports
{
    // ---- XML -----------------------------------------------------------------------------------------------------

    /// <summary>
    ///     <c>&lt;characters count="2"&gt;&lt;character codePoint="U+1F7E2" name="…" label="…" category="So" block="…"&gt;🟢&lt;/character&gt;…</c>. A character
    ///     that XML 1.0 cannot hold (most controls) has the attributes but no text.
    /// </summary>
    public static string WriteXml(IEnumerable<CollectedCharacter> items, UnicodeDatabase? db = null)
    {
        db ??= UnicodeDatabase.Default;
        var list = items.ToList();
        var root = new XElement("characters", new XAttribute("count", list.Count));

        foreach (var i in list)
        {
            var c = db.Get(i.CodePoint);
            var e = new XElement("character", new XAttribute("codePoint", i.CodePoint.Hex), new XAttribute("name", c.Name));
            if (!string.IsNullOrEmpty(i.Label)) e.Add(new XAttribute("label", XmlSafe(i.Label)));
            e.Add(new XAttribute("category", c.Category));
            if (c.Block is { } b) e.Add(new XAttribute("block", b.Name));
            if (CanBeXmlText(i.CodePoint)) e.Value = i.CodePoint.Text;
            root.Add(e);
        }

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        using var sw = new Utf8StringWriter();
        using (var xw = XmlWriter.Create(sw, new XmlWriterSettings { Indent = true, IndentChars = "  ", OmitXmlDeclaration = false, Encoding = new UTF8Encoding(false) }))
            doc.Save(xw);

        return sw.ToString() + "\n";
    }

    /// <summary>The text without the characters XML 1.0 cannot hold.</summary>
    private static string XmlSafe(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (int k = 0; k < text.Length; k++)
        {
            if (char.IsHighSurrogate(text[k]) && k + 1 < text.Length && XmlConvert.IsXmlSurrogatePair(text[k + 1], text[k]))
            {
                sb.Append(text[k]).Append(text[k + 1]);
                k++;
            }
            else if (XmlConvert.IsXmlChar(text[k])) sb.Append(text[k]);
        }

        return sb.ToString();
    }

    private static bool CanBeXmlText(CodePoint cp)
    {
        if (!cp.IsScalar) return false;

        string text = cp.Text;
        return text.Length == 1 ? XmlConvert.IsXmlChar(text[0]) : XmlConvert.IsXmlSurrogatePair(text[1], text[0]);
    }

    /// <summary>Reads characters from XML written by <see cref="WriteXml" /> (or any XML whose <c>character</c> elements have a <c>codePoint</c> attribute, or are the character).</summary>
    /// <exception cref="FormatException">The text is not XML, or an entry is not a code point.</exception>
    public static IReadOnlyList<CollectedCharacter> ReadXml(string text)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(text.TrimStart('﻿'));
        }
        catch (XmlException ex)
        {
            throw new FormatException("The file is not valid XML: " + ex.Message, ex);
        }

        var result = new List<CollectedCharacter>();
        foreach (var e in doc.Descendants("character"))
        {
            string? written = (string?)e.Attribute("codePoint") ?? e.Value;
            if (!CodePoint.TryParse(written, out var cp)) throw new FormatException("An entry of the XML has no codePoint: " + e.ToString(SaveOptions.DisableFormatting));
            string? label = (string?)e.Attribute("label");
            result.Add(new CollectedCharacter(cp, string.IsNullOrWhiteSpace(label) ? null : label));
        }

        return result;
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding { get; } = new UTF8Encoding(false);
    }

    // ---- YAML ----------------------------------------------------------------------------------------------------

    /// <summary>
    ///     A YAML list of mappings: <c>- codePoint: "U+1F7E2"</c> and so on. Strings are written in double quotes with JSON escapes, which YAML reads as it
    ///     reads them in JSON, so no character can upset the file.
    /// </summary>
    public static string WriteYaml(IEnumerable<CollectedCharacter> items, UnicodeDatabase? db = null)
    {
        db ??= UnicodeDatabase.Default;
        var sb = new StringBuilder();
        sb.Append("# Unicode Explorer collection\n");
        sb.Append("characters:\n");

        int count = 0;
        foreach (var i in items)
        {
            var c = db.Get(i.CodePoint);
            sb.Append("  - codePoint: ").Append(Q(i.CodePoint.Hex)).Append('\n');
            sb.Append("    character: ").Append(Q(i.CodePoint.IsScalar ? i.CodePoint.Text : string.Empty)).Append('\n');
            sb.Append("    name: ").Append(Q(c.Name)).Append('\n');
            if (!string.IsNullOrEmpty(i.Label)) sb.Append("    label: ").Append(Q(i.Label)).Append('\n');
            sb.Append("    category: ").Append(Q(c.Category)).Append('\n');
            if (c.Block is { } b) sb.Append("    block: ").Append(Q(b.Name)).Append('\n');
            count++;
        }

        if (count == 0) return "# Unicode Explorer collection\ncharacters: []\n";
        return sb.ToString();
    }

    private static string Q(string value) => JsonConvert.ToString(value, '"', StringEscapeHandling.Default);

    // ---- HTML gallery --------------------------------------------------------------------------------------------

    /// <summary>
    ///     A web page with one card per character: the character large, its code point, name and label. Marked colors are shown the way
    ///     <paramref name="mode" /> says. The page is self-contained (its style is in it) and follows the viewer's light or dark preference.
    /// </summary>
    /// <param name="items">The characters.</param>
    /// <param name="title">The page title.</param>
    /// <param name="colorHex">The mark color of a code point as <c>#RRGGBB</c>, or null for none.</param>
    /// <param name="mode">How the mark color is shown.</param>
    /// <param name="db">The database for names (the default one when null).</param>
    public static string WriteHtmlGallery(IEnumerable<CollectedCharacter> items, string title, Func<int, string?>? colorHex = null, ColorMode mode = ColorMode.Glyph, UnicodeDatabase? db = null)
    {
        db ??= UnicodeDatabase.Default;
        var list = items.ToList();
        var sb = new StringBuilder();
        string t = WebUtility.HtmlEncode(title);

        sb.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<title>").Append(t).Append("</title>\n<style>\n");
        sb.Append(":root{--bg:#ffffff;--card:#f6f6f8;--line:#d8d8de;--text:#1d1d22;--muted:#6a6a75}\n");
        sb.Append("@media (prefers-color-scheme:dark){:root{--bg:#17171b;--card:#222228;--line:#3a3a44;--text:#ececf0;--muted:#9a9aa6}}\n");
        sb.Append("body{margin:0;padding:24px 16px;background:var(--bg);color:var(--text);font:14px/1.4 system-ui,'Segoe UI',sans-serif}\n");
        sb.Append("h1{font-size:20px;margin:0 0 4px}p.sub{margin:0 0 20px;color:var(--muted)}\n");
        sb.Append(".grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(150px,1fr));gap:12px}\n");
        sb.Append(".card{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:12px 10px;text-align:center;overflow:hidden}\n");
        sb.Append(".glyph{font:44px/1.2 'Segoe UI Emoji','Segoe UI Symbol','Segoe UI',system-ui,sans-serif;min-height:56px;border-radius:6px}\n");
        sb.Append(".cp{font:12px ui-monospace,'Cascadia Mono',Consolas,monospace;margin-top:6px}\n.name{color:var(--muted);font-size:12px;overflow-wrap:anywhere}\n.label{font-weight:600;margin-top:2px}\n");
        sb.Append("</style>\n</head>\n<body>\n<h1>").Append(t).Append("</h1>\n<p class=\"sub\">").Append(list.Count.ToString("N0", CultureInfo.InvariantCulture)).Append(list.Count == 1 ? " character" : " characters").Append("</p>\n<div class=\"grid\">\n");

        foreach (var i in list)
        {
            var c = db.Get(i.CodePoint);
            string glyph = WebUtility.HtmlEncode(c.Glyph);
            string style = string.Empty;
            if (colorHex?.Invoke(i.CodePoint.Value) is { Length: > 0 } hex)
                style = mode switch
                        {
                            ColorMode.Background => $" style=\"background:{hex}66\"",
                            ColorMode.Accent     => $" style=\"border-bottom:4px solid {hex}\"",
                            _                    => $" style=\"color:{hex}\""
                        };

            sb.Append("<div class=\"card\"><div class=\"glyph\"").Append(style).Append('>').Append(glyph).Append("</div><div class=\"cp\">").Append(i.CodePoint.Hex).Append("</div>");
            if (!string.IsNullOrEmpty(i.Label)) sb.Append("<div class=\"label\">").Append(WebUtility.HtmlEncode(i.Label)).Append("</div>");
            sb.Append("<div class=\"name\">").Append(WebUtility.HtmlEncode(c.Name)).Append("</div></div>\n");
        }

        sb.Append("</div>\n</body>\n</html>\n");
        return sb.ToString();
    }
}

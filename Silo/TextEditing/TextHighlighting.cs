using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using Serilog;

namespace Silo.TextEditing;

/// <summary>
///     The syntax colouring of each language, in a light and a dark set of colours (AvalonEdit's own definitions are for a white
///     page only). The definitions are written here as XSHD, with the colours put in, and loaded once per language and theme.
/// </summary>
public static class TextHighlighting
{
    private static readonly Dictionary<(TextLanguage, bool), IHighlightingDefinition?> Cache = new();

    /// <summary>The colouring of <paramref name="language" /> for a dark or a light page; null for plain text.</summary>
    public static IHighlightingDefinition? For(TextLanguage language, bool dark)
    {
        if (language == TextLanguage.PlainText) return null;

        lock (Cache)
        {
            if (Cache.TryGetValue((language, dark), out var cached)) return cached;

            IHighlightingDefinition? definition = null;
            try
            {
                string xshd = language switch
                              {
                                  TextLanguage.Json => JsonXshd,
                                  TextLanguage.Xml  => XmlXshd,
                                  TextLanguage.Yaml => YamlXshd,
                                  _                 => throw new ArgumentOutOfRangeException(nameof(language))
                              };

                using var reader = XmlReader.Create(new StringReader(Colour(xshd, dark)));
                definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            }
            catch (Exception ex)
            {
                // no colouring is better than no editor
                Log.Warning(ex, "The {Language} highlighting could not be loaded", language);
            }

            Cache[(language, dark)] = definition;
            return definition;
        }
    }

    // ---- colours -------------------------------------------------------------------------------

    /// <summary>The colour of each kind of token: light page, dark page (the dark ones follow VS Code's Dark+).</summary>
    private static readonly Dictionary<string, (string Light, string Dark)> Palette = new(StringComparer.Ordinal)
                                                                                      {
                                                                                          ["Key"]         = ("#0451A5", "#9CDCFE"),
                                                                                          ["String"]      = ("#A31515", "#CE9178"),
                                                                                          ["Number"]      = ("#098658", "#B5CEA8"),
                                                                                          ["Literal"]     = ("#0000FF", "#569CD6"),
                                                                                          ["Comment"]     = ("#008000", "#6A9955"),
                                                                                          ["Punctuation"] = ("#5A5A5A", "#A0A0A0"),
                                                                                          ["Tag"]         = ("#800000", "#569CD6"),
                                                                                          ["AttrName"]    = ("#E50000", "#9CDCFE"),
                                                                                          ["AttrValue"]   = ("#0000FF", "#CE9178"),
                                                                                          ["Entity"]      = ("#B8860B", "#D7BA7D"),
                                                                                          ["CData"]       = ("#707070", "#9A9A9A"),
                                                                                          ["Declaration"] = ("#7A3E9D", "#C586C0"),
                                                                                          ["Anchor"]      = ("#AF00DB", "#C586C0"),
                                                                                          ["YamlTag"]     = ("#267F99", "#4EC9B0"),
                                                                                          ["Marker"]      = ("#7A7A7A", "#8A8A8A")
                                                                                      };

    /// <summary>Puts the colours of the theme into the <c>$Name$</c> places of a definition.</summary>
    private static string Colour(string xshd, bool dark)
    {
        var text = new StringBuilder(xshd);
        foreach (var (name, (light, darkColour)) in Palette) text.Replace("$" + name + "$", dark ? darkColour : light);
        return text.ToString();
    }

    private const string Header = "<SyntaxDefinition name=\"{0}\" xmlns=\"http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008\">";

    // ---- the definitions -----------------------------------------------------------------------

    private static readonly string JsonXshd = Header.Replace("{0}", "Silo JSON") + """
          <Color name="Key" foreground="$Key$" />
          <Color name="String" foreground="$String$" />
          <Color name="Number" foreground="$Number$" />
          <Color name="Literal" foreground="$Literal$" fontWeight="bold" />
          <Color name="Comment" foreground="$Comment$" fontStyle="italic" />
          <Color name="Punctuation" foreground="$Punctuation$" />
          <RuleSet>
            <Span color="Comment" begin="//" />
            <Span color="Comment" multiline="true" begin="/\*" end="\*/" />
            <Rule color="Key">"(?:[^"\\]|\\.)*"(?=\s*:)</Rule>
            <Span color="String">
              <Begin>"</Begin>
              <End>"</End>
              <RuleSet>
                <Span begin="\\" end="." />
              </RuleSet>
            </Span>
            <Keywords color="Literal">
              <Word>true</Word>
              <Word>false</Word>
              <Word>null</Word>
            </Keywords>
            <Rule color="Number">-?\b\d+(\.\d+)?([eE][+-]?\d+)?\b</Rule>
            <Rule color="Punctuation">[{}\[\],:]</Rule>
          </RuleSet>
        </SyntaxDefinition>
        """;

    private static readonly string XmlXshd = Header.Replace("{0}", "Silo XML") + """
          <Color name="Comment" foreground="$Comment$" fontStyle="italic" />
          <Color name="CData" foreground="$CData$" />
          <Color name="Declaration" foreground="$Declaration$" />
          <Color name="Tag" foreground="$Tag$" />
          <Color name="AttrName" foreground="$AttrName$" />
          <Color name="AttrValue" foreground="$AttrValue$" />
          <Color name="Entity" foreground="$Entity$" />
          <RuleSet name="Entities">
            <Rule color="Entity">&amp;[\w\d\#]+;</Rule>
          </RuleSet>
          <RuleSet>
            <Span color="Comment" multiline="true">
              <Begin>&lt;!--</Begin>
              <End>--&gt;</End>
            </Span>
            <Span color="CData" multiline="true">
              <Begin>&lt;!\[CDATA\[</Begin>
              <End>\]\]&gt;</End>
            </Span>
            <Span color="Declaration" multiline="true">
              <Begin>&lt;!DOCTYPE</Begin>
              <End>&gt;</End>
            </Span>
            <Span color="Declaration" multiline="true">
              <Begin>&lt;\?</Begin>
              <End>\?&gt;</End>
            </Span>
            <Span color="Tag" multiline="true">
              <Begin>&lt;</Begin>
              <End>&gt;</End>
              <RuleSet>
                <Span color="AttrValue" multiline="true" ruleSet="Entities">
                  <Begin>"</Begin>
                  <End>"</End>
                </Span>
                <Span color="AttrValue" multiline="true" ruleSet="Entities">
                  <Begin>'</Begin>
                  <End>'</End>
                </Span>
                <Rule color="AttrName">[\w\-\.:]+(?=\s*=)</Rule>
              </RuleSet>
            </Span>
            <Import ruleSet="Entities" />
          </RuleSet>
        </SyntaxDefinition>
        """;

    private static readonly string YamlXshd = Header.Replace("{0}", "Silo YAML") + """
          <Color name="Key" foreground="$Key$" />
          <Color name="String" foreground="$String$" />
          <Color name="Number" foreground="$Number$" />
          <Color name="Literal" foreground="$Literal$" fontWeight="bold" />
          <Color name="Comment" foreground="$Comment$" fontStyle="italic" />
          <Color name="Punctuation" foreground="$Punctuation$" />
          <Color name="Anchor" foreground="$Anchor$" />
          <Color name="YamlTag" foreground="$YamlTag$" />
          <Color name="Marker" foreground="$Marker$" fontWeight="bold" />
          <RuleSet>
            <Span color="Comment" begin="(?&lt;=^|\s)\#" />
            <Rule color="Marker">^(---|\.\.\.)(?=\s|$)</Rule>
            <Rule color="Marker">^%.*$</Rule>
            <Rule color="Punctuation">^\s*(-\s+)+</Rule>
            <Rule color="Key">"(?:[^"\\]|\\.)*"(?=\s*:(\s|$))</Rule>
            <Rule color="Key">'(?:[^']|'')*'(?=\s*:(\s|$))</Rule>
            <Rule color="Key">[^\s\#'"\[\]{},&amp;*!|&gt;%@`][^\#]*?(?=\s*:(\s|$))</Rule>
            <Span color="String">
              <Begin>"</Begin>
              <End>"</End>
              <RuleSet>
                <Span begin="\\" end="." />
              </RuleSet>
            </Span>
            <Span color="String">
              <Begin>'</Begin>
              <End>'</End>
            </Span>
            <Rule color="Anchor">[&amp;*][^\s,\[\]{}]+</Rule>
            <Rule color="YamlTag">!!?[^\s,\[\]{}]*</Rule>
            <Rule color="Punctuation">[|&gt;][+-]?\d*(?=\s*(\#|$))</Rule>
            <Keywords color="Literal">
              <Word>true</Word>
              <Word>false</Word>
              <Word>null</Word>
              <Word>True</Word>
              <Word>False</Word>
              <Word>Null</Word>
              <Word>TRUE</Word>
              <Word>FALSE</Word>
              <Word>NULL</Word>
            </Keywords>
            <Rule color="Literal">(?&lt;=[\s:\[,]|^)~(?=\s|,|\]|\}|$)</Rule>
            <Rule color="Number">(?&lt;=[\s:\[,]|^)[-+]?(\.\d+|\d+(\.\d*)?)([eE][-+]?\d+)?(?=\s*($|\#|,|\]|\}))</Rule>
            <Rule color="Number">(?&lt;=[\s:\[,]|^)(0x[0-9a-fA-F]+|0o[0-7]+|[-+]?\.(inf|Inf|INF)|\.(nan|NaN|NAN))(?=\s*($|\#|,|\]|\}))</Rule>
            <Rule color="Punctuation">[\[\]{},:?]</Rule>
          </RuleSet>
        </SyntaxDefinition>
        """;
}

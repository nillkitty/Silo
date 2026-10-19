using Silo.Unicode;

namespace Silo.Tests.Unicode;

[TestFixture]
public class UnicodeEncodingsTests
{
    private static string Value(IReadOnlyList<EncodingEntry> entries, string name) => entries.First(e => e.Name == name).Value;

    private static IReadOnlyList<EncodingEntry> For(int cp) => UnicodeEncodings.Describe(new CodePoint(cp), UnicodeDatabase.Default.Get(cp));

    [Test]
    public void WritesAnEmojiInTheUnicodeEncodings()
    {
        var e = For(0x1F7E2);
        Assert.That(Value(e, "Code point"), Is.EqualTo("U+1F7E2"));
        Assert.That(Value(e, "Decimal"), Is.EqualTo("128994"));
        Assert.That(Value(e, "Hexadecimal"), Is.EqualTo("0x1F7E2"));
        Assert.That(Value(e, "Octal"), Is.EqualTo("0373742"));
        Assert.That(Value(e, "Binary"), Is.EqualTo("0001 1111 0111 1110 0010"));
        Assert.That(Value(e, "UTF-8 (hex bytes)"), Is.EqualTo("F0 9F 9F A2"));
        Assert.That(Value(e, "UTF-8 (decimal bytes)"), Is.EqualTo("240 159 159 162"));
        Assert.That(Value(e, "UTF-16 (code units)"), Is.EqualTo("D83D DFE2"));
        Assert.That(Value(e, "UTF-16LE (hex bytes)"), Is.EqualTo("3D D8 E2 DF"));
        Assert.That(Value(e, "UTF-16BE (hex bytes)"), Is.EqualTo("D8 3D DF E2"));
        Assert.That(Value(e, "UTF-32 (code unit)"), Is.EqualTo("0001F7E2"));
        Assert.That(Value(e, "UTF-32LE (hex bytes)"), Is.EqualTo("E2 F7 01 00"));
    }

    [Test]
    public void WritesAnEmojiAsMarkupAndSourceCodeEscapes()
    {
        var e = For(0x1F7E2);
        Assert.That(Value(e, "HTML / XML (decimal)"), Is.EqualTo("&#128994;"));
        Assert.That(Value(e, "HTML / XML (hex)"), Is.EqualTo("&#x1F7E2;"));
        Assert.That(Value(e, "CSS escape"), Is.EqualTo("\\1F7E2"));
        Assert.That(Value(e, "URL (percent-encoded UTF-8)"), Is.EqualTo("%F0%9F%9F%A2"));
        Assert.That(Value(e, "C# / Java / JavaScript / JSON"), Is.EqualTo("\\uD83D\\uDFE2"));
        Assert.That(Value(e, "C# / C / C++ / Python / Go (32-bit)"), Is.EqualTo("\\U0001F7E2"));
        Assert.That(Value(e, "JavaScript (ES6) / Rust / Swift / PowerShell 7"), Is.EqualTo("\\u{1F7E2}"));
        Assert.That(Value(e, "C / C++ (UTF-8 bytes)"), Is.EqualTo("\\xF0\\x9F\\x9F\\xA2"));
        Assert.That(Value(e, "Perl / PCRE / Ruby (regex)"), Is.EqualTo("\\x{1F7E2}"));
        Assert.That(Value(e, "PostgreSQL"), Is.EqualTo("U&'\\+01F7E2'"));
        Assert.That(Value(e, "Python (by name)"), Is.EqualTo("\\N{LARGE GREEN CIRCLE}"));
        Assert.That(Value(e, "Alt code (Unicode, hex)"), Is.EqualTo("Alt+(+)1F7E2"));
        Assert.That(Value(e, "Word / WordPad (Alt+X)"), Does.StartWith("1F7E2"));
        Assert.That(e.Any(x => x.Name == "Alt code (OEM)"), Is.False);
        Assert.That(e.Any(x => x.Name == "Alt code (ANSI)"), Is.False);
    }

    [Test]
    public void ListsTheHtmlEntityNames()
    {
        var e = For(0x2192);
        Assert.That(e.Where(x => x.Name == "HTML entity").Select(x => x.Value).ToList(), Does.Contain("&rarr;"));
        Assert.That(e.Where(x => x.Name == "HTML entity").Select(x => x.Value).ToList(), Does.Contain("&RightArrow;"));
    }

    [Test]
    public void GivesTheAltCodesOfALatinLetter()
    {
        var e = For(0xE9);
        Assert.That(Value(e, "Alt code (OEM)"), Is.EqualTo("Alt+130"));
        Assert.That(Value(e, "Alt code (ANSI)"), Is.EqualTo("Alt+0233"));
        Assert.That(Value(e, "UTF-8 (hex bytes)"), Is.EqualTo("C3 A9"));
        Assert.That(Value(e, "C# / Java / JavaScript / JSON"), Is.EqualTo("\\u00E9"));
        Assert.That(Value(e, "Uppercase"), Is.EqualTo("U+00C9"));
        Assert.That(Value(For(0x41), "Alt code (ANSI)"), Is.EqualTo("Alt+0065"));
        Assert.That(Value(For(0x41), "Alt code (OEM)"), Is.EqualTo("Alt+65"));
    }

    [Test]
    public void KnowsTheOemPictureCodes()
    {
        Assert.That(AltCodes.Oem(0x263A), Is.EqualTo(1));
        Assert.That(AltCodes.Oem(0x2665), Is.EqualTo(3));
        Assert.That(AltCodes.Oem(0x25BC), Is.EqualTo(31));
        Assert.That(AltCodes.Oem(0x2302), Is.EqualTo(127));
        Assert.That(AltCodes.Oem(0x20AC), Is.Null);
        Assert.That(AltCodes.Oem(0x1F7E2), Is.Null);
        Assert.That(AltCodes.Oem(0x1263A), Is.Null); // not U+263A with the top bits chopped off
        Assert.That(AltCodes.Oem(0xA0), Is.EqualTo(255));
        Assert.That(AltCodes.Oem(0xB0), Is.EqualTo(248));
    }

    [Test]
    public void KnowsTheAnsiCodes()
    {
        Assert.That(AltCodes.Ansi(0x20AC), Is.EqualTo(128));
        Assert.That(AltCodes.Ansi(0x2122), Is.EqualTo(153));
        Assert.That(AltCodes.Ansi(0xA9), Is.EqualTo(169));
        Assert.That(AltCodes.Ansi(0x7F), Is.Null);
        Assert.That(AltCodes.Ansi(0x263A), Is.Null);
        Assert.That(AltCodes.Ansi(0x81), Is.Null);
    }

    [Test]
    public void AltCodesRoundTrip()
    {
        for (int n = 1; n <= 255; n++)
        {
            string? text = AltCodes.FromOem(n);
            Assert.That(text, Is.Not.Null, "Alt+" + n);
            Assert.That(AltCodes.Oem(char.ConvertToUtf32(text!, 0)), Is.EqualTo(n), "Alt+" + n);
        }

        for (int n = 32; n <= 255; n++)
        {
            string? text = AltCodes.FromAnsi(n);
            if (text is null) continue;
            Assert.That(AltCodes.Ansi(char.ConvertToUtf32(text, 0)), Is.EqualTo(n), "Alt+0" + n);
        }

        Assert.That(AltCodes.FromOem(0), Is.Null);
        Assert.That(AltCodes.FromOem(256), Is.Null);
        Assert.That(AltCodes.FromAnsi(129), Is.Null);
        Assert.That(AltCodes.FromOem(130), Is.EqualTo("é"));
        Assert.That(AltCodes.FromAnsi(233), Is.EqualTo("é"));
        Assert.That(AltCodes.FromAnsi(128), Is.EqualTo("€"));
    }

    [Test]
    public void ASurrogateHasNoUtf8Form()
    {
        var e = UnicodeEncodings.Describe(new CodePoint(0xD83D));
        Assert.That(e.Any(x => x.Name == "UTF-8 (hex bytes)"), Is.False);
        Assert.That(e.Any(x => x.Name == "UTF-32 (code unit)"), Is.False);
        Assert.That(Value(e, "UTF-16 (code units)"), Is.EqualTo("D83D"));
        Assert.That(e.Any(x => x.Name == "Not a scalar value"), Is.True);
    }

    [Test]
    public void ShowsNormalizationWhereItChangesTheText()
    {
        if ("é".Normalize(System.Text.NormalizationForm.FormD).Length != 2) return; // a runtime without normalization data

        var e = For(0xE9);
        Assert.That(Value(e, "NFD (decomposed)"), Is.EqualTo("U+0065 U+0301"));
        Assert.That(e.Any(x => x.Name == "NFC (composed)"), Is.False);
        Assert.That(Value(e, "Decomposition (canonical)"), Is.EqualTo("U+0065 U+0301"));
    }

    [Test]
    public void EveryEntryBelongsToAKnownGroup()
    {
        foreach (int cp in new[] { 0x41, 0xE9, 0x1F7E2, 0xD83D, 0x0, 0x10FFFF })
            foreach (var entry in For(cp))
            {
                Assert.That(UnicodeEncodings.Groups, Does.Contain(entry.Group), entry.Name);
                Assert.That(entry.Value, Is.Not.Empty, entry.Name);
            }
    }
}

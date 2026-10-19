using Silo.Unicode;

namespace Silo.Tests.Unicode;

[TestFixture]
public class UnicodeDatabaseTests
{
    private static UnicodeDatabase Db => UnicodeDatabase.Default;

    private static int[] Find(string text) => Db.Search(new UnicodeQuery { Text = text });

    // ---- the data ------------------------------------------------------------------------------

    [Test]
    public void LoadsTheEmbeddedData()
    {
        Assert.That(Db.Version, Does.StartWith("1"));
        Assert.That(Db.AssignedCount, Is.GreaterThan(150000));
        Assert.That(Db.Blocks.Count, Is.GreaterThan(300));
        Assert.That(Db.Scripts.Count, Is.GreaterThan(100));
        Assert.That(Db.Ages, Does.Contain("1.1"));
    }

    [Test]
    public void KnowsAnOrdinaryLetter()
    {
        var a = Db.Get(0x41);
        Assert.That(a.Name, Is.EqualTo("LATIN CAPITAL LETTER A"));
        Assert.That(a.Category, Is.EqualTo("Lu"));
        Assert.That(a.CategoryName, Is.EqualTo("Uppercase Letter"));
        Assert.That(a.MajorCategoryName, Is.EqualTo("Letter"));
        Assert.That(a.Block!.Name, Is.EqualTo("Basic Latin"));
        Assert.That(a.Script, Is.EqualTo("Latin"));
        Assert.That(a.ScriptCode, Is.EqualTo("Latn"));
        Assert.That(a.Age, Is.EqualTo("1.1"));
        Assert.That(a.IsAssigned, Is.True);
        Assert.That(a.Plane, Is.EqualTo(0));
        Assert.That(a.BidiClass, Is.EqualTo("L"));
        Assert.That(a.Glyph, Is.EqualTo("A"));
    }

    [Test]
    public void KnowsAnEmojiWithItsTags()
    {
        var green = Db.Get(0x1F7E2);
        Assert.That(green.Name, Is.EqualTo("LARGE GREEN CIRCLE"));
        Assert.That(green.IsEmoji, Is.True);
        Assert.That(green.IsEmojiPresentation, Is.True);
        Assert.That(green.EmojiGroup, Is.EqualTo("symbols"));
        Assert.That(green.Keywords, Does.Contain("green"));
        Assert.That(green.Keywords, Does.Contain("circle"));
        Assert.That(green.Keywords, Does.Contain("green_circle"));
        Assert.That(green.Plane, Is.EqualTo(1));
        Assert.That(green.PlaneName, Does.Contain("Supplementary"));
        Assert.That(green.Age, Is.EqualTo("12.0"));
    }

    [Test]
    public void KnowsAControlByItsStandardNameAndAbbreviations()
    {
        var lf = Db.Get(0xA);
        Assert.That(lf.Name, Is.EqualTo("LINE FEED"));
        Assert.That(lf.Aliases, Does.Contain("LF"));
        Assert.That(lf.Category, Is.EqualTo("Cc"));
        Assert.That(lf.Glyph, Is.EqualTo("␊"));
        Assert.That(Db.Get(0x7F).Glyph, Is.EqualTo("␡"));
    }

    [Test]
    public void KnowsHtmlEntitiesAndDecompositions()
    {
        var nbsp = Db.Get(0xA0);
        Assert.That(nbsp.HtmlEntities, Does.Contain("nbsp"));
        Assert.That(nbsp.DecompositionKind, Is.EqualTo("noBreak"));
        Assert.That(nbsp.DecompositionCodePoints.Select(c => c.Value).ToList(), Is.EqualTo(new List<int> { 0x20 }));
        Assert.That(nbsp.Glyph, Is.EqualTo("␣"));

        var eAcute = Db.Get(0xE9);
        Assert.That(eAcute.DecompositionKind, Is.EqualTo("canonical"));
        Assert.That(eAcute.DecompositionCodePoints.Select(c => c.Value).ToList(), Is.EqualTo(new List<int> { 0x65, 0x301 }));
    }

    [Test]
    public void NamesTheCodePointsWhoseNamesFollowARule()
    {
        Assert.That(Db.Get(0x4E00).Name, Is.EqualTo("CJK UNIFIED IDEOGRAPH-4E00"));
        Assert.That(Db.Get(0x9FA5).Name, Is.EqualTo("CJK UNIFIED IDEOGRAPH-9FA5"));
        Assert.That(Db.Get(0xAC00).Name, Is.EqualTo("HANGUL SYLLABLE GA"));
        Assert.That(Db.Get(0xD7A3).Name, Is.EqualTo("HANGUL SYLLABLE HIH"));
        Assert.That(Db.Get(0xD55C).Name, Is.EqualTo("HANGUL SYLLABLE HAN"));
        Assert.That(Db.Get(0xE000).Name, Is.EqualTo("<private-use-E000>"));
        Assert.That(Db.Get(0xD800).Name, Is.EqualTo("<surrogate-D800>"));
        Assert.That(Db.Get(0xFFFF).Name, Is.EqualTo("<noncharacter-FFFF>"));
        Assert.That(Db.Get(0xFDD0).Name, Is.EqualTo("<noncharacter-FDD0>"));
        Assert.That(Db.Get(0xF900).Name, Does.StartWith("CJK COMPATIBILITY IDEOGRAPH-"));
    }

    [Test]
    public void SaysWhenACodePointIsUnassigned()
    {
        var gap = Db.Get(0x378);
        Assert.That(gap.IsKnown, Is.False);
        Assert.That(gap.IsAssigned, Is.False);
        Assert.That(gap.Category, Is.EqualTo("Cn"));
        Assert.That(gap.Name, Is.EqualTo("(unassigned)"));
        Assert.That(gap.Age, Is.Null);
        Assert.That(gap.Block!.Name, Is.EqualTo("Greek and Coptic"));
        Assert.That(Db.Get(0xE000).IsAssigned, Is.False);
        Assert.Throws<ArgumentOutOfRangeException>(() => Db.Get(0x110000));
    }

    [Test]
    public void BlocksDoNotOverlap()
    {
        for (int i = 1; i < Db.Blocks.Count; i++)
            Assert.That(Db.Blocks[i].Start, Is.GreaterThan(Db.Blocks[i - 1].End), Db.Blocks[i].Name);

        var latin = Db.Blocks.First(b => b.Name == "Basic Latin");
        Assert.That(latin.Start, Is.EqualTo(0));
        Assert.That(latin.End, Is.EqualTo(0x7F));
        Assert.That(latin.Count, Is.EqualTo(128));
    }

    [Test]
    public void KnownCodePointsComeInOrderWithoutRepeats()
    {
        int last = -1;
        int count = 0;
        foreach (int cp in Db.KnownCodePoints())
        {
            if (cp <= last) Assert.That(cp, Is.GreaterThan(last));
            last = cp;
            count++;
        }

        Assert.That(count, Is.GreaterThan(Db.AssignedCount));
    }

    [Test]
    public void RefusesAFileThatIsNotTheData()
    {
        Assert.Throws<FormatException>(() => UnicodeDatabase.Parse(new StringReader("junk\nmore junk")));
        Assert.Throws<FormatException>(() => UnicodeDatabase.Parse(new StringReader("#UCD\t1.0\t1\n41\tA\tLu")));
    }

    [Test]
    public void FindsCharactersByName()
    {
        Assert.That(Db.FindByName("large green circle")!.Value, Is.EqualTo(0x1F7E2));
        Assert.That(Db.FindByName("LARGE-GREEN CIRCLE")!.Value, Is.EqualTo(0x1F7E2));
        Assert.That(Db.FindByName("nbsp")!.Value, Is.EqualTo(0xA0));
        Assert.That(Db.FindByName("CJK UNIFIED IDEOGRAPH-4E01")!.Value, Is.EqualTo(0x4E01));
        Assert.That(Db.FindByName("hangul syllable han")!.Value, Is.EqualTo(0xD55C));
        Assert.That(Db.FindByName("no such character"), Is.Null);
        Assert.That(Db.FindByName(""), Is.Null);
    }

    // ---- searching -------------------------------------------------------------------------------

    [Test]
    public void FindsByWordsInTheNameTagsAndAliases()
    {
        Assert.That(Find("green circle"), Does.Contain(0x1F7E2));
        Assert.That(Find("green_circle"), Does.Contain(0x1F7E2));
        Assert.That(Find("nbsp"), Does.Contain(0xA0));
        Assert.That(Find("rarr"), Does.Contain(0x2192));
        Assert.That(Find("GREEN   circle"), Does.Contain(0x1F7E2));
        Assert.That(Find("greeny circle zzz"), Is.Empty);
        Assert.That(Find("xyzzyxyzzy"), Is.Empty);
    }

    [Test]
    public void PutsTheBestMatchesFirst()
    {
        int[] r = Find("large green circle");
        Assert.That(r[0], Is.EqualTo(0x1F7E2));

        int[] soft = Find("circle");
        Assert.That(soft.Length, Is.GreaterThan(50));
        // a substring-only match comes after whole-word ones
        int[] part = Find("circ");
        Assert.That(part.Length, Is.GreaterThan(soft.Length - 1));
    }

    [Test]
    public void LooksUpCodePointsRangesAndCharacters()
    {
        Assert.That(Find("U+1F7E2"), Is.EqualTo(new[] { 0x1F7E2 }));
        Assert.That(Find("0x41"), Is.EqualTo(new[] { 0x41 }));
        Assert.That(Find("\U0001F7E2"), Is.EqualTo(new[] { 0x1F7E2 }));
        Assert.That(Find("éè"), Is.EqualTo(new[] { 0xE8, 0xE9 }));
        Assert.That(Find("U+41-5A").Length, Is.EqualTo(26));
        Assert.That(Find("U+41..5A").Length, Is.EqualTo(26));
        Assert.That(Find("&#x1F7E2;"), Is.EqualTo(new[] { 0x1F7E2 }));
    }

    [Test]
    public void ABareHexRunIsACodePointAsWellAsAWord()
    {
        Assert.That(Find("1F7E2"), Does.Contain(0x1F7E2));
        Assert.That(Find("41"), Does.Contain(0x41));
    }

    [Test]
    public void FiltersByCategoryBlockScriptAgeAndFlags()
    {
        Assert.That(Find("cat:Sc"), Does.Contain(0x20AC));
        Assert.That(Find("cat:Sc"), Does.Not.Contain(0x41));
        Assert.That(Find("cat:Sc cat:Sm"), Is.Empty);
        Assert.That(Find("cat:L").Length, Is.GreaterThan(100000));
        Assert.That(Find("cat:Lu,Ll"), Does.Contain(0x61));

        Assert.That(Find("block:arrows"), Does.Contain(0x2192));
        Assert.That(Find("block:arrows"), Does.Not.Contain(0x41));
        Assert.That(Find("block:\"Basic Latin\"").Length, Is.EqualTo(128));

        Assert.That(Find("script:Greek"), Does.Contain(0x3B1));
        Assert.That(Find("script:grek"), Does.Contain(0x3B1));
        Assert.That(Find("script:grek"), Does.Not.Contain(0x41));

        Assert.That(Find("age:1.1 U+20-7E").Length, Is.EqualTo(95));
        Assert.That(Find("age:>=17"), Is.Not.Empty);
        Assert.That(Find("age:>=17"), Does.Not.Contain(0x41));
        Assert.That(Find("age:<2 U+41"), Is.EqualTo(new[] { 0x41 }));

        Assert.That(Find("is:emoji"), Does.Contain(0x1F7E2));
        Assert.That(Find("is:emoji"), Does.Not.Contain(0x41));
        Assert.That(Find("is:mirrored"), Does.Contain(0x28));
        Assert.That(Find("is:ascii").Length, Is.EqualTo(128));
        Assert.That(Find("plane:1"), Does.Contain(0x1F7E2));
        Assert.That(Find("plane:1"), Does.Not.Contain(0x41));
        Assert.That(Find("bidi:AL"), Does.Contain(0x627));
    }

    [Test]
    public void CombinesTextWithFilters()
    {
        int[] r = Find("arrow cat:Sm block:arrows");
        Assert.That(r, Does.Contain(0x2192));
        Assert.That(r.All(cp => Db.Get(cp).Category == "Sm" && Db.Get(cp).Block!.Name == "Arrows"), Is.True);
    }

    [Test]
    public void UsesTheFilterFieldsLikeTheQualifiers()
    {
        var q = new UnicodeQuery { Categories = ["Sc"], Block = "Currency Symbols" };
        int[] r = Db.Search(q);
        Assert.That(r, Does.Contain(0x20AC));
        Assert.That(r, Does.Not.Contain(0x24));

        Assert.That(Db.Search(new UnicodeQuery { Script = "Grek", EmojiOnly = false }), Does.Contain(0x3B1));
        Assert.That(Db.Search(new UnicodeQuery { EmojiOnly = true, Text = "green" }), Does.Contain(0x1F7E2));
        Assert.That(Db.Search(new UnicodeQuery { Block = "No such block" }), Is.Empty);
        Assert.That(Db.Search(new UnicodeQuery { Age = "nonsense" }), Is.Empty);
        Assert.That(new UnicodeQuery().IsEmpty, Is.True);
        Assert.That(q.IsEmpty, Is.False);
    }

    [Test]
    public void ANonsenseQualifierGivesNothing()
    {
        Assert.That(Find("block:zzzznotablock"), Is.Empty);
        Assert.That(Find("script:zzzznotascript"), Is.Empty);
        Assert.That(Find("age:abc"), Is.Empty);
        Assert.That(Find("plane:99"), Is.Empty);
    }

    [Test]
    public void ListsEveryAssignedCharacterForAnEmptyQuery()
    {
        int[] all = Db.Search(new UnicodeQuery());
        Assert.That(all.Length, Is.EqualTo(Db.AssignedCount));
        Assert.That(all[0], Is.EqualTo(0));
        Assert.That(all, Does.Not.Contain(0xE000));
        Assert.That(Find("cat:Co"), Does.Contain(0xE000));
        Assert.That(Find("block:\"Private Use Area\"").Length, Is.EqualTo(6400));
        Assert.That(Find("U+E000"), Is.EqualTo(new[] { 0xE000 }));
        Assert.That(all.Distinct().Count(), Is.EqualTo(all.Length));
    }

    [Test]
    public void ABlockCanBeBrowsedWithItsGaps()
    {
        var hebrew = Db.Blocks.First(b => b.Name == "Hebrew");
        int[] without = Db.Search(new UnicodeQuery { Block = "Hebrew" });
        int[] with    = Db.Search(new UnicodeQuery { Block = "Hebrew", IncludeUnassigned = true });
        Assert.That(with.Length, Is.EqualTo(hebrew.Count));
        Assert.That(without.Length, Is.LessThan(with.Length));
        Assert.That(with, Is.EqualTo(Enumerable.Range(hebrew.Start, hebrew.Count).ToArray()));
    }

    [Test]
    public void ASearchCanBeCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => Db.Search(new UnicodeQuery(), cts.Token));
    }

    [Test]
    public void NormalizesTextForTheSearch()
    {
        Assert.That(UnicodeNames.Normalize("No-Break  Space!"), Is.EqualTo(" no break space "));
        Assert.That(UnicodeNames.Normalize(""), Is.EqualTo(" "));
        Assert.That(UnicodeNames.Normalize("---"), Is.EqualTo(" "));
        Assert.That(UnicodeNames.HangulSyllableName(0xAC01), Is.EqualTo("HANGUL SYLLABLE GAG"));
        Assert.Throws<ArgumentOutOfRangeException>(() => UnicodeNames.HangulSyllableName(0x41));
    }
}

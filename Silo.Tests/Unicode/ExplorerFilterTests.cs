using Silo.Unicode;

namespace Silo.Tests.Unicode;

[TestFixture]
public class ExplorerFilterTests
{
    private static UnicodeDatabase Db => UnicodeDatabase.Default;

    // ---- versions ------------------------------------------------------------------------------

    [TestCase(VersionOp.Before, "6.0", "<6.0")]
    [TestCase(VersionOp.Through, "6.0", "<=6.0")]
    [TestCase(VersionOp.Starting, "6.0", ">=6.0")]
    [TestCase(VersionOp.After, "6.0", ">6.0")]
    public void TheVersionComparisonIsWrittenTheWayTheQueryReadsIt(VersionOp op, string version, string expected)
    {
        Assert.That(new VersionFilter(VersionMode.Include, op, version).Condition, Is.EqualTo(expected));
    }

    [Test]
    public void AVersionFilterThatIsOffHasNoCondition()
    {
        Assert.That(VersionFilter.Off.IsActive, Is.False);
        Assert.That(VersionFilter.Off.Condition, Is.Null);
        Assert.That(new VersionFilter(VersionMode.Include, VersionOp.After, null).IsActive, Is.False);
    }

    [TestCase(VersionOp.Before, "6.0", "5.2", true)]
    [TestCase(VersionOp.Before, "6.0", "6.0", false)]
    [TestCase(VersionOp.Through, "6.0", "6.0", true)]
    [TestCase(VersionOp.Through, "6.0", "6.1", false)]
    [TestCase(VersionOp.Starting, "6.0", "6.0", true)]
    [TestCase(VersionOp.Starting, "6.0", "5.2", false)]
    [TestCase(VersionOp.After, "6.0", "6.0", false)]
    [TestCase(VersionOp.After, "6.0", "7.0", true)]
    public void IncludeKeepsTheCharactersTheComparisonHolds(VersionOp op, string version, string age, bool kept)
    {
        Assert.That(new VersionFilter(VersionMode.Include, op, version).Keeps(age), Is.EqualTo(kept));
        Assert.That(new VersionFilter(VersionMode.Exclude, op, version).Keeps(age), Is.EqualTo(!kept));
    }

    [Test]
    public void OffKeepsEverything()
    {
        Assert.That(VersionFilter.Off.Keeps("1.1"), Is.True);
        Assert.That(VersionFilter.Off.Keeps(null), Is.True);
    }

    // ---- the query -----------------------------------------------------------------------------

    [Test]
    public void TheQueryCarriesTheChoices()
    {
        var state = new FilterState
                    {
                        Category = "Lu", Block = "Basic Latin", Script = "Latn", Kind = GlyphKind.EmojiOnly,
                        Version  = new VersionFilter(VersionMode.Include, VersionOp.Starting, "6.0")
                    };

        var q = state.ToQuery("a b");
        Assert.That(q.Text, Is.EqualTo("a b"));
        Assert.That(q.Categories, Is.EquivalentTo(new[] { "Lu" }));
        Assert.That(q.Block, Is.EqualTo("Basic Latin"));
        Assert.That(q.Script, Is.EqualTo("Latn"));
        Assert.That(q.Age, Is.EqualTo(">=6.0"));
        Assert.That(q.EmojiOnly, Is.True);
    }

    [Test]
    public void ExcludingAVersionLeavesItOutOfTheQueryAndDoesItAfterwards()
    {
        var state = new FilterState { Version = new VersionFilter(VersionMode.Exclude, VersionOp.After, "6.0") };
        Assert.That(state.ToQuery("").Age, Is.Null);
    }

    [Test]
    public void UnassignedCodePointsAreListedForEverythingOnlyWithABlock()
    {
        Assert.That(new FilterState { Kind = GlyphKind.Everything }.ToQuery("").IncludeUnassigned, Is.False, "otherwise the private use planes flood the list");
        Assert.That(new FilterState { Kind = GlyphKind.Everything, Block = "Arrows" }.ToQuery("").IncludeUnassigned, Is.True);
        Assert.That(new FilterState { Block = "Arrows" }.ToQuery("").IncludeUnassigned, Is.False);
    }

    [Test]
    public void ResettingPutsTheStartingChoicesBack()
    {
        var state = new FilterState { Category = "L", Block = "x", Script = "y", Kind = GlyphKind.PrintableOnly, ColorFilter = 3, Version = new VersionFilter(VersionMode.Include, VersionOp.Before, "3.0") };
        Assert.That(state.IsNarrowed, Is.True);

        state.Reset();
        Assert.That(state.IsNarrowed, Is.False);
        Assert.That(state.Kind, Is.EqualTo(GlyphKind.AvailableOnly));
        Assert.That(state.Version.IsActive, Is.False);
    }

    [Test]
    public void AChosenBlockOrScriptOverridesTheSwitchedOffBlocks()
    {
        Assert.That(new FilterState().OverridesBlockPolicy("moon"), Is.False);
        Assert.That(new FilterState { Block = "Hangul Syllables" }.OverridesBlockPolicy(""), Is.True);
        Assert.That(new FilterState { Script = "Hani" }.OverridesBlockPolicy(""), Is.True);
        Assert.That(new FilterState().OverridesBlockPolicy("漢"), Is.True);
    }

    // ---- the filter ----------------------------------------------------------------------------

    private static CodePointSet Fonts(params int[] drawn)
    {
        var set = new CodePointSet();
        set.AddRange(drawn);
        return set;
    }

    private static int[] Keep(ResultFilter filter, params int[] cps) => filter.Apply(Db, cps).Kept;

    [Test]
    public void AvailableOnlyDropsWhatNoFontDrawsButKeepsWhatIsInvisibleByDesign()
    {
        var filter = new ResultFilter { Kind = GlyphKind.AvailableOnly, Coverage = Fonts(0x41) };
        var outcome = filter.Apply(Db, [0x41, 0x4E00, 0x200D, 0x20]);

        Assert.That(outcome.Kept, Is.EqualTo(new[] { 0x41, 0x200D, 0x20 }));
        Assert.That(outcome.NoGlyph, Is.EqualTo(1));
    }

    [Test]
    public void AssignedOnlyAndEverythingDoNotLookAtTheFonts()
    {
        Assert.That(Keep(new ResultFilter { Kind = GlyphKind.AssignedOnly, Coverage = Fonts() }, 0x41, 0x4E00), Is.EqualTo(new[] { 0x41, 0x4E00 }));
        Assert.That(Keep(new ResultFilter { Kind = GlyphKind.Everything, Coverage = Fonts() }, 0x41, 0x378), Is.EqualTo(new[] { 0x41, 0x378 }));
    }

    [Test]
    public void PrintableOnlyDropsControlsSpacesAndFormatCharacters()
    {
        var filter = new ResultFilter { Kind = GlyphKind.PrintableOnly, Coverage = Fonts(0x41, 0x20, 0x7, 0x200D, 0x3A9) };
        Assert.That(Keep(filter, 0x41, 0x20, 0x7, 0x200D, 0x3A9), Is.EqualTo(new[] { 0x41, 0x3A9 }));
    }

    [Test]
    public void EmojiAndNonEmojiSplitTheList()
    {
        var fonts = Fonts(0x41, 0x1F600, 0x1F7E2);
        Assert.That(Keep(new ResultFilter { Kind = GlyphKind.EmojiOnly, Coverage = fonts }, 0x41, 0x1F600, 0x1F7E2), Is.EqualTo(new[] { 0x1F600, 0x1F7E2 }));
        Assert.That(Keep(new ResultFilter { Kind = GlyphKind.NonEmojiOnly, Coverage = fonts }, 0x41, 0x1F600, 0x1F7E2), Is.EqualTo(new[] { 0x41 }));
    }

    [Test]
    public void JoinableOnlyIsTheJoinersAndThePictographsTheyJoin()
    {
        var fonts = Fonts(0x41, 0x1F468, 0x200C, 0x200D, 0x2764);
        var kept  = Keep(new ResultFilter { Kind = GlyphKind.JoinableOnly, Coverage = fonts }, 0x41, 0x1F468, 0x200C, 0x200D, 0x2764);

        Assert.That(kept, Does.Contain(0x200D));
        Assert.That(kept, Does.Contain(0x200C));
        Assert.That(kept, Does.Contain(0x1F468), "MAN is part of family and profession sequences");
        Assert.That(kept, Does.Contain(0x2764), "HEAVY BLACK HEART joins into couple sequences");
        Assert.That(kept, Does.Not.Contain(0x41));
    }

    [Test]
    public void SwitchedOffBlocksAreCountedAndLeftOut()
    {
        var filter = new ResultFilter { Kind = GlyphKind.Everything, DisabledBlocks = BlockPolicy.DisabledCodePoints(Db.Blocks, null) };
        var outcome = filter.Apply(Db, [0x41, 0x4E00, 0x4E01, 0xAC00]);

        Assert.That(outcome.Kept, Is.EqualTo(new[] { 0x41 }));
        Assert.That(outcome.BlocksOff, Is.EqualTo(3));
    }

    [Test]
    public void ExcludingAVersionRemovesTheCharactersItAdded()
    {
        // LATIN CAPITAL LETTER A is from 1.1; the green circle from 12.0
        var filter = new ResultFilter { Kind = GlyphKind.Everything, Version = new VersionFilter(VersionMode.Exclude, VersionOp.Starting, "12.0") };
        Assert.That(Keep(filter, 0x41, 0x1F7E2), Is.EqualTo(new[] { 0x41 }));
    }

    [Test]
    public void TheColorFilterKeepsMarkedOrUnmarkedCharacters()
    {
        var colors = new CharacterColorMap();
        colors.Set(0x41, 2);

        Assert.That(Keep(new ResultFilter { Kind = GlyphKind.Everything, Colors = colors, ColorFilter = -1 }, 0x41, 0x42), Is.EqualTo(new[] { 0x41 }));
        Assert.That(Keep(new ResultFilter { Kind = GlyphKind.Everything, Colors = colors, ColorFilter = -2 }, 0x41, 0x42), Is.EqualTo(new[] { 0x42 }));
        Assert.That(Keep(new ResultFilter { Kind = GlyphKind.Everything, Colors = colors, ColorFilter = 2 }, 0x41, 0x42), Is.EqualTo(new[] { 0x41 }));
    }

    [Test]
    public void WithoutKnownFontsNothingCountsAsMissingAGlyph()
    {
        var outcome = new ResultFilter { Kind = GlyphKind.AvailableOnly, Coverage = null }.Apply(Db, [0x41, 0x4E00]);
        Assert.That(outcome.Kept.Length, Is.EqualTo(2));
        Assert.That(outcome.NoGlyph, Is.EqualTo(0));
    }

    [Test]
    public void TheSearchHonoursAnIncludedVersion()
    {
        var state = new FilterState { Kind = GlyphKind.Everything, Version = new VersionFilter(VersionMode.Include, VersionOp.Starting, "12.0"), Block = "Geometric Shapes Extended" };
        var found = Db.Search(state.ToQuery(""));

        var since = new VersionFilter(VersionMode.Include, VersionOp.Starting, "12.0");

        Assert.That(found, Does.Contain(0x1F7E2));
        Assert.That(found, Does.Not.Contain(0x1F780), "BLACK LEFT-POINTING ISOSCELES RIGHT TRIANGLE is from 7.0");
        Assert.That(found.All(cp => since.Satisfies(Db.Get(cp).Age)), Is.True);
    }
}

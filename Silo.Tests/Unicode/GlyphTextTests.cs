using Silo.Unicode;

namespace Silo.Tests.Unicode;

[TestFixture]
public class GlyphTextTests
{
    private static string AsText(string text)
    {
        var db = UnicodeDatabase.Default;
        return GlyphText.AsText(text, cp => db.Get(cp));
    }

    private const string Sel = "\uFE0E";

    [Test]
    public void An_emoji_gets_the_text_selector()
    {
        Assert.That(AsText("\U0001F600"), Is.EqualTo("\U0001F600" + Sel), "a picture-by-default emoji");
        Assert.That(AsText("\u2600"), Is.EqualTo("\u2600" + Sel), "a text-by-default emoji");
        Assert.That(AsText("\u2764\u2600"), Is.EqualTo("\u2764" + Sel + "\u2600" + Sel));
    }

    [Test]
    public void Plain_text_comes_back_as_it_was()
    {
        string text = "Abc 123 #* \u03C0 \u25B6";
        Assert.That(AsText(text), Is.SameAs(text).Or.EqualTo(text.Replace("\u25B6", "\u25B6" + Sel)), "ASCII (the digits, # and * are emoji only in keycaps) and Greek are left alone");
        Assert.That(AsText("1#*"), Is.EqualTo("1#*"));
        Assert.That(AsText(string.Empty), Is.Empty);
    }

    [Test]
    public void The_picture_selector_is_changed_to_the_text_selector()
    {
        Assert.That(AsText("\u2764\uFE0F"), Is.EqualTo("\u2764" + Sel));
        Assert.That(AsText("\u2764" + Sel), Is.EqualTo("\u2764" + Sel), "one that has it is left");
    }

    [Test]
    public void Sequences_that_would_break_are_left_alone()
    {
        string flag   = "\U0001F1FA\U0001F1F8";
        string tone   = "\U0001F44D\U0001F3FD";
        string joined = "\u2764\uFE0F\u200D\U0001F525";
        string keycap = "1\uFE0F\u20E3";

        Assert.That(AsText(flag), Is.EqualTo(flag), "a flag is two regional indicators");
        Assert.That(AsText(tone), Is.EqualTo(tone), "the skin tone has to touch its base");
        Assert.That(AsText(joined), Is.EqualTo(joined), "a joined sequence");
        Assert.That(AsText(keycap), Is.EqualTo(keycap));
    }

    [Test]
    public void Without_a_database_the_text_is_not_touched()
    {
        Assert.That(GlyphText.AsText("\U0001F600", null), Is.EqualTo("\U0001F600"));
    }

    [Test]
    public void The_option_is_on_unless_turned_off_and_survives_storage()
    {
        Assert.That(new ExplorerSettings().CopyEmojiAsText, Is.True);

        var back = Newtonsoft.Json.JsonConvert.DeserializeObject<ExplorerSettings>(Newtonsoft.Json.JsonConvert.SerializeObject(new ExplorerSettings { CopyEmojiAsText = false }))!;
        Assert.That(back.CopyEmojiAsText, Is.False);
        Assert.That(Newtonsoft.Json.JsonConvert.DeserializeObject<ExplorerSettings>("{}")!.CopyEmojiAsText, Is.True, "settings kept before the option existed get it on");
    }
}

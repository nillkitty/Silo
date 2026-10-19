using Newtonsoft.Json;
using Silo.Expressions;
using Silo.Hotkeys;
using Silo.Unicode;

namespace Silo.Tests.Hotkeys;

[TestFixture]
public class ShortcutKeyTests
{
    [Test]
    public void ParsesAndPrintsInFixedOrder()
    {
        Assert.That(ShortcutKey.TryParse("Ctrl+Win+J", out var k), Is.True);
        Assert.That(k.Modifiers, Is.EqualTo(ShortcutModifiers.Control | ShortcutModifiers.Windows));
        Assert.That(k.VirtualKey, Is.EqualTo(0x4A));
        Assert.That(k.ToString(), Is.EqualTo("Ctrl+Win+J"));

        Assert.That(ShortcutKey.TryParse("win + shift + CONTROL + alt + f5", out var all), Is.True);
        Assert.That(all.ToString(), Is.EqualTo("Ctrl+Alt+Shift+Win+F5"));
    }

    [Test]
    public void ParsesThePlusKey()
    {
        Assert.That(ShortcutKey.TryParse("Ctrl++", out var k), Is.True);
        Assert.That(k.Modifiers, Is.EqualTo(ShortcutModifiers.Control));
        Assert.That(k.VirtualKey, Is.EqualTo(0xBB));
        Assert.That(ShortcutKey.TryParse(k.ToString(), out var again), Is.True);
        Assert.That(again, Is.EqualTo(k));

        Assert.That(ShortcutKey.TryParse("+", out var bare), Is.True);
        Assert.That(bare.Modifiers, Is.EqualTo(ShortcutModifiers.None));
    }

    [Test]
    public void RejectsNonsense()
    {
        foreach (string bad in new[] { "", "  ", "Ctrl+", "Ctrl+Alt", "J+K", "Ctrl+Nope", "Ctrl++J", "+Ctrl" })
            Assert.That(ShortcutKey.TryParse(bad, out _), Is.False, bad);
    }

    [Test]
    public void EveryNamedKeyRoundTrips()
    {
        for (int vk = 1; vk < 256; vk++)
        {
            var key = new ShortcutKey(ShortcutModifiers.Control, vk);
            if (key.KeyName.StartsWith("VK")) continue;
            Assert.That(ShortcutKey.TryParse(key.ToString(), out var back), Is.True, key.ToString());
            Assert.That(back.VirtualKey, Is.EqualTo(vk), key.ToString());
        }
    }

    [Test]
    public void GlobalNeedsAStrongModifier()
    {
        Assert.That(ShortcutKey.TryParse("J", out var plain) && !plain.IsValidGlobal(out string? p1) && p1!.Contains("Ctrl"), Is.True);
        Assert.That(ShortcutKey.TryParse("Shift+J", out var shift) && !shift.IsValidGlobal(out _), Is.True);
        Assert.That(ShortcutKey.TryParse("Win+J", out var win) && win.IsValidGlobal(out _), Is.True);
        Assert.That(ShortcutKey.TryParse("F13", out var f13) && f13.IsValidGlobal(out _), Is.True);
        Assert.That(ShortcutKey.None.IsValidGlobal(out string? none), Is.False);
        Assert.That(none, Is.EqualTo("Choose a key."));
    }

    [Test]
    public void WritesJsonAsText()
    {
        ShortcutKey.TryParse("Ctrl+Win+J", out var k);
        string json = JsonConvert.SerializeObject(k);
        Assert.That(json, Is.EqualTo("\"Ctrl+Win+J\""));
        Assert.That(JsonConvert.DeserializeObject<ShortcutKey>(json), Is.EqualTo(k));
        Assert.That(JsonConvert.DeserializeObject<ShortcutKey>("\"\""), Is.EqualTo(ShortcutKey.None));
        Assert.Throws<JsonSerializationException>(() => JsonConvert.DeserializeObject<ShortcutKey>("\"Ctrl+Nope\""));
    }
}

[TestFixture]
public class ShortcutBindingTests
{
    private static ShortcutBinding Binding(string key, string action, ShortcutScope scope = ShortcutScope.Global)
    {
        ShortcutKey.TryParse(key, out var k);
        return new ShortcutBinding { Name = "blue", Key = k, Action = action, Scope = scope };
    }

    [Test]
    public void ValidatesTheDiagramsExample()
    {
        Assert.That(Binding("Ctrl+Win+J", "type ${your:blue}").Validate(out string? problem), Is.True, problem);
    }

    [Test]
    public void UnboundIsValidAndDoesNothing()
    {
        var none = ShortcutBinding.Unbound("blue");
        Assert.That(none.Validate(out _), Is.True);
        Assert.That(none.IsBound, Is.False);
    }

    [Test]
    public void ExplainsWhatIsWrong()
    {
        Assert.That(Binding("J", "type x").Validate(out string? p1), Is.False);
        Assert.That(p1, Does.Contain("Ctrl"));

        Assert.That(Binding("J", "type x", ShortcutScope.Application).Validate(out _), Is.True);

        Assert.That(Binding("Ctrl+J", "").Validate(out string? p2), Is.False);
        Assert.That(p2, Does.Contain("Start the action"));

        Assert.That(Binding("Ctrl+J", "dance now").Validate(out string? p3), Is.False);
        Assert.That(p3, Does.Contain("dance"));

        Assert.That(Binding("Ctrl+J", "type").Validate(out string? p4), Is.False);
        Assert.That(p4, Does.Contain("Say what to type"));

        Assert.That(Binding("Ctrl+J", "type ${your:").Validate(out _), Is.False);

        Assert.That((Binding("Ctrl+J", "type x") with { Name = " " }).Validate(out string? p5), Is.False);
        Assert.That(p5, Does.Contain("name"));

        Assert.That(new ShortcutBinding { Name = "x", Action = "type x" }.Validate(out string? p6), Is.False);
        Assert.That(p6, Is.EqualTo("Choose a key."));
    }

    [Test]
    public void RoundTripsThroughJson()
    {
        var b = Binding("Ctrl+Win+J", "type ${your:blue}") with { Enabled = false, Scope = ShortcutScope.Application };
        string json = JsonConvert.SerializeObject(b);
        Assert.That(json, Does.Contain("\"Ctrl+Win+J\""));
        Assert.That(json, Does.Contain("\"Application\""));
        Assert.That(json, Does.Not.Contain("IsBound"));
        Assert.That(JsonConvert.DeserializeObject<ShortcutBinding>(json), Is.EqualTo(b));
    }
}

[TestFixture]
public class ShortcutActionTests
{
    private static IVariableScope Scope()
    {
        var your = new VariableScope().Set("blue", new CodePoint(0x1F535)).Set("word", "hello");
        return new VariableScope().AddNamespace("your", your);
    }

    [Test]
    public void ParsesVerbAndTemplate()
    {
        Assert.That(ShortcutActionText.TryParse("  TYPE   ${your:blue} ok", out var a, out _), Is.True);
        Assert.That(a!.Verb, Is.EqualTo("type"));
        Assert.That(a.Template.Source, Is.EqualTo("${your:blue} ok"));
        Assert.That(ShortcutActionText.TryParse("type${your:blue}", out var tight, out _), Is.True);
        Assert.That(tight!.Template.Source, Is.EqualTo("${your:blue}"));
    }

    [Test]
    public void RunsTheVerbWithTheExpandedText()
    {
        string? typed = null, copied = null;
        var verbs = new ShortcutVerbs().Add(ShortcutVerbs.Type, t => typed = t).Add(ShortcutVerbs.Copy, t => copied = t);

        var r = verbs.Run("type ${your:blue}!", Scope());
        Assert.That(r.Success, Is.True);
        Assert.That(typed, Is.EqualTo("🔵!"));
        Assert.That(copied, Is.Null);

        verbs.Run("copy ${upper(your:word)}", Scope());
        Assert.That(copied, Is.EqualTo("HELLO"));
    }

    [Test]
    public void FailuresAreResultsNotExceptions()
    {
        var verbs = new ShortcutVerbs().Add(ShortcutVerbs.Type, _ => throw new InvalidOperationException("window closed"));

        var missing = verbs.Run("type ${your:nope}", Scope());
        Assert.That(missing.Success, Is.False);
        Assert.That(missing.Error, Does.Contain("nope"));

        var thrown = verbs.Run("type x", Scope());
        Assert.That(thrown.Success, Is.False);
        Assert.That(thrown.Error, Is.EqualTo("window closed"));
        Assert.That(thrown.Output, Is.EqualTo("x"));

        var noHandler = verbs.Run("copy x", Scope());
        Assert.That(noHandler.Success, Is.False);
        Assert.That(noHandler.Error, Does.Contain("copy"));

        var bad = verbs.Run("fly x", Scope());
        Assert.That(bad.Success, Is.False);
        Assert.That(bad.Output, Is.Null);
    }

    [Test]
    public void LiteralDollarBraceNeedsDoubling()
    {
        string? typed = null;
        new ShortcutVerbs().Add("type", t => typed = t).Run("type $${x}", Scope());
        Assert.That(typed, Is.EqualTo("${x}"));
    }
}

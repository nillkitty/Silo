using Silo.Contracts.Core;
using Silo.Hotkeys;
using Silo.Providers.Data;
using Silo.Storage;
using Silo.Unicode;

namespace Silo.Tests.Storage;

[TestFixture]
public class GlobalShortcutPrefTests
{
    [SetUp]
    public void SetUp()
    {
        _store = new MemoryStoredStore();
        _your  = new YourConstants(() => _store);
        _typed = [];
        _verbs = new ShortcutVerbs().Add(ShortcutVerbs.Type, t => _typed.Add(t));
        _prefs = new GlobalShortcutPrefs(() => _store, _verbs, () => _your.AsScope());
    }

    [TearDown]
    public void TearDown()
    {
        _prefs.Dispose();
    }

    private MemoryStoredStore   _store = null!;
    private YourConstants       _your  = null!;
    private ShortcutVerbs       _verbs = null!;
    private List<string>        _typed = null!;
    private GlobalShortcutPrefs _prefs = null!;

    private static ShortcutBinding Binding(string        name,                         string key, string action = "type ${your:blue}",
                                           ShortcutScope scope = ShortcutScope.Global, bool   enabled = true)
    {
        ShortcutKey.TryParse(key, out var k);
        return new ShortcutBinding { Name = name, Key = k, Action = action, Scope = scope, Enabled = enabled };
    }

    [Test]
    public void TheDiagramsBindingIsAPreferenceAndRunsTheAction()
    {
        _your.SetCodePoint("blue", new CodePoint(0x1F535));
        var pref = _prefs.Save(Binding("blue", "Ctrl+Win+J"));

        Assert.That(pref,                     Is.InstanceOf<IGlobalShortcutPref>());
        Assert.That(pref,                     Is.InstanceOf<IPreference<ShortcutBinding>>());
        Assert.That(pref.Key,                 Is.EqualTo("shortcut:blue"));
        Assert.That(pref.Shortcut.ToString(), Is.EqualTo("Ctrl+Win+J"));
        Assert.That(pref.Action,              Is.EqualTo("type ${your:blue}"));
        Assert.That(pref.IsBound,             Is.True);
        Assert.That(pref.IsDefault,           Is.False);
        Assert.That(pref.Description,         Does.Contain("Ctrl+Win+J"));

        var result = pref.Run();
        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(_typed,         Is.EqualTo(new[] { "🔵" }));

        // the constant changes, the shortcut follows
        _your.SetCodePoint("blue", new CodePoint(0x1F499));
        pref.Run();
        Assert.That(_typed[^1], Is.EqualTo("💙"));
    }

    [Test]
    public void IsStoredAsAStoredBindingTheWholeProviderListsAsPreferences()
    {
        _prefs.Save(Binding("b", "Ctrl+Alt+B"));
        _prefs.Save(Binding("a", "Ctrl+Alt+A"));

        var record = _store.GetStored("shortcut:a")!;
        Assert.That(record.TypeName, Does.StartWith("Silo.Hotkeys.ShortcutBinding, "));
        Assert.That(record.Data,     Does.Contain("Ctrl+Alt+A"));

        Assert.That(_prefs.Preferences.Select(p => p.Key), Is.EqualTo(new[] { "shortcut:a", "shortcut:b" }));
    }

    [Test]
    public void TheNonGenericViewWorksToo()
    {
        var         pref    = _prefs.Save(Binding("x", "Ctrl+Alt+X", "copy hi"));
        IPreference general = pref;
        Assert.That(general.ValueType, Is.EqualTo(typeof(ShortcutBinding)));
        Assert.That(general.Value,     Is.EqualTo(pref.Value));
        Assert.Throws<ArgumentException>(() => general.Value = "nope");

        general.Value = Binding("x", "Ctrl+Alt+Y", "copy hi");
        Assert.That(pref.Shortcut.ToString(), Is.EqualTo("Ctrl+Alt+Y"));
    }

    [Test]
    public void ResettingRemovesTheRow()
    {
        var pref = _prefs.Save(Binding("x", "Ctrl+Alt+X"));
        Assert.That(_store.GetStored("shortcut:x"), Is.Not.Null);
        pref.Reset();
        Assert.That(_store.GetStored("shortcut:x"), Is.Null);
        Assert.That(pref.IsDefault,                 Is.True);
        Assert.That(pref.IsBound,                   Is.False);
        Assert.That(pref.Run().Success,             Is.False);
    }

    [Test]
    public void RefusesBindingsThatCannotWork()
    {
        var pref = _prefs.Save(Binding("x", "Ctrl+Alt+X"));
        Assert.Throws<ArgumentException>(() => pref.Value = Binding("x", "J")); // a global needs Ctrl, Alt or Win
        Assert.Throws<ArgumentException>(() => pref.Value = Binding("x", "Ctrl+J", "fly away"));
        Assert.That(pref.Shortcut.ToString(), Is.EqualTo("Ctrl+Alt+X"));
    }

    [Test]
    public void TheNameIsTheKeysNotTheValues()
    {
        var pref = _prefs.Save(Binding("x", "Ctrl+Alt+X"));
        pref.Value = Binding("other", "Ctrl+Alt+Z");
        Assert.That(pref.Value.Name,                Is.EqualTo("x"));
        Assert.That(_store.ListStored("shortcut:"), Has.Count.EqualTo(1));
    }

    [Test]
    public void TwoBindingsCannotShareAKeyInOneScope()
    {
        _prefs.Save(Binding("a", "Ctrl+Alt+J"));

        Assert.That(_prefs.Validate(Binding("b", "Ctrl+Alt+J"), null, out string? problem), Is.False);
        Assert.That(problem,                                                                Does.Contain("\"a\""));
        Assert.Throws<ArgumentException>(() => _prefs.Save(Binding("b", "Ctrl+Alt+J")));

        Assert.That(_prefs.Validate(Binding("b", "Ctrl+Alt+J", scope: ShortcutScope.Application), null, out _), Is.True);
        Assert.That(_prefs.Validate(Binding("b", "Ctrl+Alt+J", enabled: false),                   null, out _), Is.True);

        // the binding itself may be saved again
        Assert.That(_prefs.Validate(Binding("a", "Ctrl+Alt+J", "copy x"), "a",  out _),            Is.True);
        // a new one may not take its name
        Assert.That(_prefs.Validate(Binding("A", "Ctrl+Alt+K"),           null, out string? same), Is.False);
        Assert.That(same,                                                                          Does.Contain("already"));
    }

    [Test]
    public void RenamingMovesTheRow()
    {
        _prefs.Save(Binding("old", "Ctrl+Alt+J"));
        _prefs.Save(Binding("new", "Ctrl+Alt+J"), replacing: "old");
        Assert.That(_prefs.Shortcuts.Select(p => p.Name), Is.EqualTo(new[] { "new" }));
        Assert.That(_store.GetStored("shortcut:old"),     Is.Null);
    }

    [Test]
    public void EditingAShortcutCanMoveItBetweenPersonalAndShared()
    {
        _prefs.Save(Binding("x", "Ctrl+Alt+X"));
        var moved = _prefs.Save(Binding("x", "Ctrl+Alt+Y"), StoredScope.Shared, "x");

        Assert.That(moved.Storage,                                 Is.EqualTo(StoredScope.Shared));
        Assert.That(_store.GetStored("shortcut:x"),                Is.Null);
        Assert.That(_prefs.Shortcuts.Single().Shortcut.ToString(), Is.EqualTo("Ctrl+Alt+Y"));
    }

    [Test]
    public void AnEditThatDoesNotValidateKeepsTheOldShortcut()
    {
        _prefs.Save(Binding("x", "Ctrl+Alt+X"));
        _prefs.Save(Binding("y", "Ctrl+Alt+Y"));

        Assert.Throws<ArgumentException>(() => _prefs.Save(Binding("x", "Ctrl+Alt+Y"), replacing: "x"));
        Assert.That(_prefs.Find("x")!.Shortcut.ToString(), Is.EqualTo("Ctrl+Alt+X"));
    }

    [Test]
    public void PersonalAndSharedBindingsBothShowAndThePersonalOneWins()
    {
        _prefs.Save(Binding("team", "Ctrl+Alt+T", "copy shared"), StoredScope.Shared);
        _prefs.Save(Binding("mine", "Ctrl+Alt+M"));
        Assert.That(_prefs.Shortcuts.Select(p => p.Name),               Is.EqualTo(new[] { "mine", "team" }));
        Assert.That(((GlobalShortcutPref)_prefs.Find("team")!).Storage, Is.EqualTo(StoredScope.Shared));

        // the same name in both: the person's own
        _store.SetStored("shortcut:team", "x", "json", StoredCodec.Serialize(Binding("team", "Ctrl+Alt+Q", "copy mine")));
        Assert.That(_prefs.Find("team")!.Action, Is.EqualTo("copy mine"));
    }

    [Test]
    public void RemoveTakesBothCopies()
    {
        _prefs.Save(Binding("x", "Ctrl+Alt+X"));
        _store.SetStored("shortcut:x", "x", "json", "{}", StoredScope.Shared);
        Assert.That(_prefs.Remove("X"), Is.True);
        Assert.That(_prefs.Shortcuts,   Is.Empty);
        Assert.That(_prefs.Remove("x"), Is.False);
    }

    [Test]
    public void SaysWhenSomethingChanged()
    {
        int changes = 0;
        _prefs.Changed += (_, _) => changes++;
        var pref = _prefs.Save(Binding("x", "Ctrl+Alt+X"));
        Assert.That(changes, Is.EqualTo(1));
        pref.Value = Binding("x", "Ctrl+Alt+Y"); // through the preference itself
        Assert.That(changes, Is.EqualTo(2));
        _prefs.Remove("x");
        Assert.That(changes, Is.EqualTo(3));

        _prefs.Dispose();
        _store.SetStored("shortcut:z", "x", "json", "{}");
        Assert.That(changes, Is.EqualTo(3));
    }

    [Test]
    public void NoSiloMeansNoShortcuts()
    {
        var none = new GlobalShortcutPrefs(() => null, _verbs, () => _your.AsScope());
        Assert.That(none.Shortcuts, Is.Empty);
        Assert.Throws<InvalidOperationException>(() => none.Save(Binding("x", "Ctrl+Alt+X")));
        none.Dispose();
    }
}
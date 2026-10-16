using System.Linq;
using Silo.Contracts.Providers.Data;
using Silo.Model;

namespace Silo.Tests.ViewModel;

public class PreferenceTests
{
    private sealed class FakeStore : IPreferenceStore
    {
        public Dictionary<string, string> Rows   { get; } = [];
        public List<string>               Writes { get; } = [];

        public IReadOnlyList<PreferenceRecord> GetPreferences() => Rows.Select(r => new PreferenceRecord(r.Key, r.Value)).ToList();

        public void SetPreference(string key, string data)
        {
            Rows[key] = data;
            Writes.Add(key);
        }
    }

    private enum Mode
    {
        Quick,
        Careful
    }

    private class Sample : ModelBase
    {
        public FakeStore Store { get; } = new();

        protected override IPreferenceStore? PreferenceStore => Store;

        [Toggle("test.on", "Starts on", true)]
        public bool On { get; set; }

        [Toggle("test.off", "Starts off")]
        public bool Off { get; set; }

        [Toggle("test.declared", "Declared default beats the initial value", true)]
        public bool Declared { get; set; }

        [Preference("test.count", "A number", 5, Category = "Numbers")]
        public int Count { get; set; } = 1;

        [Preference("test.ratio", "A decimal number", 2)]
        public double Ratio { get; set; }

        [Preference("test.mode", "A choice", "Careful")]
        public Mode Mode { get; set; }

        [Preference("test.name", "Text")]
        public string Name { get; set; } = "initial";

        public bool NotAPreference { get; set; } = true;

        public void Load() => LoadPreferences();
    }

    private sealed class Derived : Sample
    {
        [Toggle("test.derived", "Added by a subclass", false)]
        public bool Extra { get; set; }
    }

    private static Preference Pref(ModelBase m, string key) => m.Preferences.Single(p => p.Key == key);

    [Test]
    public void Marked_properties_are_the_preferences_and_others_are_not()
    {
        var keys = new Sample().Preferences.Select(p => p.Key);

        Assert.That(keys, Is.EquivalentTo(new[] { "test.on", "test.off", "test.declared", "test.count", "test.ratio", "test.mode", "test.name" }));
    }

    [Test]
    public void A_subclass_has_its_own_preferences_after_those_it_inherits()
    {
        var keys = new Derived().Preferences.Select(p => p.Key).ToList();

        Assert.That(keys.Last(),  Is.EqualTo("test.derived"));
        Assert.That(keys.Count,   Is.EqualTo(8));
    }

    [Test]
    public void A_preference_knows_what_the_attribute_says()
    {
        var count = Pref(new Sample(), "test.count");

        Assert.That(count.Description, Is.EqualTo("A number"));
        Assert.That(count.Category,    Is.EqualTo("Numbers"));
        Assert.That(count.ValueType,   Is.EqualTo(typeof(int)));
        Assert.That(count.IsToggle,    Is.False);
        Assert.That(Pref(new Sample(), "test.on").IsToggle, Is.True);
    }

    [Test]
    public void The_default_is_the_attributes_else_what_the_property_started_as()
    {
        var m = new Sample();

        Assert.That(Pref(m, "test.count").Default, Is.EqualTo(5), "declared");
        Assert.That(Pref(m, "test.name").Default,  Is.EqualTo("initial"), "none declared: the initial value");
        Assert.That(Pref(m, "test.off").Default,   Is.EqualTo(false));
        Assert.That(Pref(m, "test.ratio").Default, Is.EqualTo(2.0), "an int constant for a double property");
        Assert.That(Pref(m, "test.mode").Default,  Is.EqualTo(Mode.Careful), "a string constant for an enum property");
    }

    [Test]
    public void Loading_with_nothing_stored_applies_the_declared_defaults()
    {
        var m = new Sample();

        m.Load();

        Assert.That(m.On,       Is.True);
        Assert.That(m.Declared, Is.True, "the property started false, the attribute says true");
        Assert.That(m.Count,    Is.EqualTo(5));
        Assert.That(m.Mode,     Is.EqualTo(Mode.Careful));
        Assert.That(m.Name,     Is.EqualTo("initial"));
        Assert.That(m.Store.Writes, Is.Empty, "loading writes nothing");
    }

    [Test]
    public void Loading_applies_what_is_stored()
    {
        var m = new Sample();
        m.Store.Rows["test.on"]    = "false";
        m.Store.Rows["test.count"] = "42";
        m.Store.Rows["test.mode"]  = "Quick";
        m.Store.Rows["test.name"]  = "kept";

        m.Load();

        Assert.That(m.On,    Is.False);
        Assert.That(m.Count, Is.EqualTo(42));
        Assert.That(m.Mode,  Is.EqualTo(Mode.Quick));
        Assert.That(m.Name,  Is.EqualTo("kept"));
        Assert.That(m.Store.Writes, Is.Empty);
    }

    [Test]
    public void A_stored_value_that_is_not_the_right_type_is_ignored()
    {
        var m = new Sample();
        m.Store.Rows["test.count"] = "many";

        m.Load();

        Assert.That(m.Count, Is.EqualTo(5), "the default");
    }

    [Test]
    public void Setting_the_value_sets_the_property_and_writes_it_to_the_store()
    {
        var m = new Sample();
        m.Load();

        Pref(m, "test.count").Value = 9;

        Assert.That(m.Count,                      Is.EqualTo(9));
        Assert.That(m.Store.Rows["test.count"],   Is.EqualTo("9"));
    }

    [Test]
    public void A_value_that_reads_as_the_type_is_accepted()
    {
        var m = new Sample();

        Pref(m, "test.ratio").Value = 3;   // an int for a double
        Pref(m, "test.mode").Value  = "Quick";

        Assert.That(m.Ratio, Is.EqualTo(3.0));
        Assert.That(m.Mode,  Is.EqualTo(Mode.Quick));
    }

    [Test]
    public void A_value_that_is_not_the_type_throws_and_changes_nothing()
    {
        var m = new Sample();

        Assert.Throws<ArgumentException>(() => Pref(m, "test.count").Value = "many");
        Assert.Throws<ArgumentException>(() => Pref(m, "test.count").Value = null);
        Assert.That(m.Count, Is.EqualTo(1));
        Assert.That(m.Store.Writes, Is.Empty);
    }

    [Test]
    public void Setting_the_value_it_already_has_with_nothing_stored_writes_nothing()
    {
        var m = new Sample();
        m.Load();

        Pref(m, "test.count").Value = 5;

        Assert.That(m.Store.Writes, Is.Empty);
    }

    [Test]
    public void Setting_the_same_value_twice_writes_once()
    {
        var m = new Sample();
        m.Load();

        Pref(m, "test.count").Value = 9;
        Pref(m, "test.count").Value = 9;

        Assert.That(m.Store.Writes, Is.EqualTo(new[] { "test.count" }));
    }

    [Test]
    public void Reset_goes_back_to_the_default_and_writes_it_over_a_stored_choice()
    {
        var m = new Sample();
        m.Store.Rows["test.count"] = "42";
        m.Load();

        Pref(m, "test.count").Reset();

        Assert.That(m.Count,                    Is.EqualTo(5));
        Assert.That(m.Store.Rows["test.count"], Is.EqualTo("5"));
        Assert.That(Pref(m, "test.count").IsDefault, Is.True);
    }

    [Test]
    public void Saving_writes_what_code_changed_and_nothing_else()
    {
        var m = new Sample();
        m.Load();

        m.Count = 7;
        m.On    = false;
        m.SavePreferences();

        Assert.That(m.Store.Writes, Is.EquivalentTo(new[] { "test.count", "test.on" }));
        Assert.That(m.Store.Rows["test.count"], Is.EqualTo("7"));
        Assert.That(m.Store.Rows["test.on"],    Is.EqualTo("false"));

        m.SavePreferences();
        Assert.That(m.Store.Writes, Has.Count.EqualTo(2), "a second save has nothing to add");
    }

    [Test]
    public void Changing_a_property_back_to_a_stored_choices_default_still_writes_it()
    {
        var m = new Sample();
        m.Store.Rows["test.count"] = "42";
        m.Load();

        m.Count = 5; // the default again, but the Silo holds 42
        m.SavePreferences();

        Assert.That(m.Store.Rows["test.count"], Is.EqualTo("5"));
    }

    [Test]
    public void A_preference_says_when_it_is_the_default()
    {
        var m = new Sample();
        m.Load();
        var count = Pref(m, "test.count");

        Assert.That(count.IsDefault, Is.True);
        count.Value = 6;
        Assert.That(count.IsDefault, Is.False);
    }

    [Test]
    public void Setting_the_value_tells_whoever_is_watching()
    {
        var m       = new Sample();
        var count   = Pref(m, "test.count");
        var changed = new List<string?>();
        count.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        count.Value = 3;

        Assert.That(changed, Does.Contain(nameof(Preference.Value)));
        Assert.That(changed, Does.Contain(nameof(Preference.IsDefault)));
    }

    [Test]
    public void A_model_with_nowhere_to_keep_preferences_still_has_them()
    {
        var m = new NoStore();

        Pref(m, "test.x").Value = true;
        m.SavePreferences();

        Assert.That(m.X, Is.True);
    }

    private sealed class NoStore : ModelBase
    {
        [Toggle("test.x", "x", false)]
        public bool X { get; set; }
    }

    // ---- mistakes in the code are loud ----------------------------------------------------------

    private sealed class DuplicateKeys : ModelBase
    {
        [Toggle("test.same", "a", false)] public bool A { get; set; }
        [Toggle("test.same", "b", false)] public bool B { get; set; }
    }

    private sealed class ToggleOfNumber : ModelBase
    {
        [Toggle("test.num", "n")] public int N { get; set; }
    }

    private sealed class UnsupportedType : ModelBase
    {
        [Preference("test.list", "l")] public List<int> L { get; set; } = [];
    }

    private sealed class ReadOnly : ModelBase
    {
        [Preference("test.ro", "r")] public int R { get; } = 1;
    }

    private sealed class BadDefault : ModelBase
    {
        [Preference("test.bad", "b", "not a number")] public int B { get; set; }
    }

    [Test]
    public void A_mistake_in_the_markings_is_an_error_naming_the_property()
    {
        Assert.That(() => new DuplicateKeys().Preferences,  Throws.InvalidOperationException.With.Message.Contains("test.same"));
        Assert.That(() => new ToggleOfNumber().Preferences, Throws.InvalidOperationException.With.Message.Contains("ToggleOfNumber.N"));
        Assert.That(() => new UnsupportedType().Preferences, Throws.InvalidOperationException.With.Message.Contains("UnsupportedType.L"));
        Assert.That(() => new ReadOnly().Preferences,       Throws.InvalidOperationException.With.Message.Contains("setter"));
        Assert.That(() => new BadDefault().Preferences,     Throws.InvalidOperationException.With.Message.Contains("default"));
    }

    // ---- the Silo's own ------------------------------------------------------------------------

    private static IEnumerable<Type> ModelTypes()
    {
        Type?[] types;
        try
        {
            types = typeof(ModelBase).Assembly.GetTypes();
        }
        catch (System.Reflection.ReflectionTypeLoadException ex)
        {
            types = ex.Types;
        }

        return types.OfType<Type>().Where(t => typeof(ModelBase).IsAssignableFrom(t));
    }

    [Test]
    public void No_two_preferences_in_the_app_share_a_key()
    {
        Assert.That(PreferenceDefinition.DuplicateKeys(ModelTypes()), Is.Empty);
    }

    [Test]
    public void Every_preference_in_the_app_is_valid_and_described()
    {
        foreach (var type in ModelTypes())
            foreach (var d in PreferenceDefinition.For(type))
                Assert.That(d.Description, Is.Not.Empty, $"{type.Name}.{d.Property.Name} needs a description");
    }

    [Test]
    public void The_Silos_toggles_are_found_with_the_keys_they_were_given()
    {
        var keys = PreferenceDefinition.For(typeof(OpenSilo)).Select(d => d.Key).ToList();

        Assert.That(keys, Does.Contain("silo.rememberConnections"));
        Assert.That(keys, Does.Contain("silo.rememberExplorerTree"));
        Assert.That(keys, Does.Contain("silo.reopenMe"));
        Assert.That(keys, Is.Unique);
    }
}

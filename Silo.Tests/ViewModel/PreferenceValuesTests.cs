using System.Globalization;
using Silo.Model;

namespace Silo.Tests.ViewModel;

public class PreferenceValuesTests
{
    private enum Shade
    {
        Light,
        Dark
    }

    private static void RoundTrips(Type type, object? value)
    {
        string text = PreferenceValues.ToText(value);

        Assert.That(PreferenceValues.TryFromText(type, text, out object? back), Is.True, $"\"{text}\" reads back");
        Assert.That(back, Is.EqualTo(value));
    }

    [Test]
    public void Values_survive_being_written_as_text()
    {
        RoundTrips(typeof(bool),           true);
        RoundTrips(typeof(bool),           false);
        RoundTrips(typeof(int),            -42);
        RoundTrips(typeof(long),           long.MaxValue);
        RoundTrips(typeof(double),         0.1);
        RoundTrips(typeof(double),         1234567.891);
        RoundTrips(typeof(float),          1.5f);
        RoundTrips(typeof(decimal),        12.50m);
        RoundTrips(typeof(string),         "a, b: c\r\nd");
        RoundTrips(typeof(char),           'x');
        RoundTrips(typeof(Shade),          Shade.Dark);
        RoundTrips(typeof(TimeSpan),       TimeSpan.FromMinutes(90));
        RoundTrips(typeof(Guid),           Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff"));
        RoundTrips(typeof(Uri),            new Uri("https://example.com/a?b=c"));
        RoundTrips(typeof(DateTime),       new DateTime(2026, 10, 16, 4, 5, 6, DateTimeKind.Utc));
        RoundTrips(typeof(DateTimeOffset), new DateTimeOffset(2026, 10, 16, 4, 5, 6, TimeSpan.FromHours(2)));
    }

    [Test]
    public void The_text_does_not_depend_on_the_culture()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            Assert.That(PreferenceValues.ToText(1234.5), Is.EqualTo("1234.5"));
            Assert.That(PreferenceValues.TryFromText(typeof(double), "1234.5", out object? value), Is.True);
            Assert.That(value, Is.EqualTo(1234.5));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Test]
    public void Booleans_are_written_in_lower_case()
    {
        Assert.That(PreferenceValues.ToText(true),  Is.EqualTo("true"));
        Assert.That(PreferenceValues.ToText(false), Is.EqualTo("false"));
    }

    [Test]
    public void Nothing_is_empty_text_and_empty_text_is_nothing_for_types_that_can_be_nothing()
    {
        Assert.That(PreferenceValues.ToText(null), Is.EqualTo(string.Empty));

        Assert.That(PreferenceValues.TryFromText(typeof(int?), "", out object? n), Is.True);
        Assert.That(n, Is.Null);
        Assert.That(PreferenceValues.TryFromText(typeof(Uri), "", out object? u), Is.True);
        Assert.That(u, Is.Null);
        Assert.That(PreferenceValues.TryFromText(typeof(string), "", out object? s), Is.True);
        Assert.That(s, Is.EqualTo(string.Empty));
    }

    [Test]
    public void Empty_text_is_not_a_value_for_a_type_that_cannot_be_nothing()
    {
        Assert.That(PreferenceValues.TryFromText(typeof(int),  "", out _), Is.False);
        Assert.That(PreferenceValues.TryFromText(typeof(bool), "", out _), Is.False);
    }

    [Test]
    public void Text_that_is_not_a_value_is_refused()
    {
        Assert.That(PreferenceValues.TryFromText(typeof(bool),     "yes",        out _), Is.False);
        Assert.That(PreferenceValues.TryFromText(typeof(int),      "12x",        out _), Is.False);
        Assert.That(PreferenceValues.TryFromText(typeof(byte),     "300",        out _), Is.False);
        Assert.That(PreferenceValues.TryFromText(typeof(Shade),    "Purple",     out _), Is.False);
        Assert.That(PreferenceValues.TryFromText(typeof(Guid),     "not a guid", out _), Is.False);
        Assert.That(PreferenceValues.TryFromText(typeof(char),     "ab",         out _), Is.False);
        Assert.That(PreferenceValues.TryFromText(typeof(TimeSpan), "soon",       out _), Is.False);
    }

    [Test]
    public void An_enum_is_read_whatever_its_case()
    {
        Assert.That(PreferenceValues.TryFromText(typeof(Shade), "dARK", out object? value), Is.True);
        Assert.That(value, Is.EqualTo(Shade.Dark));
    }

    [Test]
    public void A_nullable_reads_as_its_underlying_type()
    {
        Assert.That(PreferenceValues.TryFromText(typeof(int?), "5", out object? value), Is.True);
        Assert.That(value, Is.EqualTo(5));
    }

    [Test]
    public void Only_types_that_can_be_written_as_text_are_supported()
    {
        Assert.That(PreferenceValues.IsSupported(typeof(bool)),   Is.True);
        Assert.That(PreferenceValues.IsSupported(typeof(int?)),   Is.True);
        Assert.That(PreferenceValues.IsSupported(typeof(Shade)),  Is.True);
        Assert.That(PreferenceValues.IsSupported(typeof(Shade?)), Is.True);
        Assert.That(PreferenceValues.IsSupported(typeof(object)), Is.False);
        Assert.That(PreferenceValues.IsSupported(typeof(List<int>)), Is.False);
    }
}

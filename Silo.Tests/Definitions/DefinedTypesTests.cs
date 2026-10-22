using Silo.DbModel.Meta;
using Silo.Providers.Data;
using Silo.Providers.Data.Shape;
using Silo.Providers.Definitions;

namespace Silo.Tests.Definitions;

[TestFixture]
public class DefinedTypesTests
{
    private sealed class FakeEnums : IEnumStore
    {
        public List<EnumRecord> Items { get; } = [];

        public IReadOnlyList<EnumRecord> GetEnums()
        {
            return Items;
        }

        public EnumRecord? GetEnum(int id)
        {
            return Items.FirstOrDefault(e => e.Id == id);
        }

        public EnumRecord? FindEnum(string name)
        {
            return Items.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        public EnumRecord SaveEnum(EnumRecord definition)
        {
            throw new NotSupportedException();
        }

        public bool DeleteEnum(int id)
        {
            throw new NotSupportedException();
        }

        public EnumValueRecord SaveEnumValue(int enumId, EnumValueRecord value)
        {
            throw new NotSupportedException();
        }

        public bool DeleteEnumValue(int valueId)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeSyntaxes : ISyntaxStore
    {
        public List<SyntaxRecord> Items { get; } = [];

        public IReadOnlyList<SyntaxRecord> GetSyntaxes()
        {
            return Items;
        }

        public SyntaxRecord? GetSyntax(int id)
        {
            return Items.FirstOrDefault(s => s.Id == id);
        }

        public SyntaxRecord? FindSyntax(string nameOrAlias)
        {
            return Items.FirstOrDefault(s => string.Equals(s.Name,  nameOrAlias, StringComparison.OrdinalIgnoreCase)) ??
                   Items.FirstOrDefault(s => string.Equals(s.Alias, nameOrAlias, StringComparison.OrdinalIgnoreCase));
        }

        public SyntaxRecord SaveSyntax(SyntaxRecord syntax)
        {
            throw new NotSupportedException();
        }

        public bool DeleteSyntax(int id)
        {
            throw new NotSupportedException();
        }

        public int RestoreDefaults()
        {
            throw new NotSupportedException();
        }
    }

    private static TypeLookup Look(string prefix, string name)
    {
        return new(new Uri("ns://test"), prefix, name);
    }

    [Test]
    public void An_enum_resolves_by_name_without_regard_to_case()
    {
        var store = new FakeEnums();
        store.Items.Add(new EnumRecord(1, "Status", null, EnumType.Integral, EnumFlags.None,
                                       [new EnumValueRecord(1, "Open", "0"), new EnumValueRecord(2, "Closed", "1")]));

        var type = DefinedTypes.LookupEnum(store, Look("enum", "status")) as DefinedEnum;
        Assert.That(type,                      Is.Not.Null);
        Assert.That(type!.Name,                Is.EqualTo("Status"));
        Assert.That(type.Prefix,               Is.EqualTo("enum"));
        Assert.That(type.Kind,                 Is.EqualTo(DataKind.Nominal));
        Assert.That(type.Shape,                Is.EqualTo(DataShape.Scalar));
        Assert.That(type.Namespace.ToString(), Is.EqualTo("ns://enums/")); // a URI with only a host gets its trailing slash
        Assert.That(type.Uri.ToString(),       Is.EqualTo("ns://enums/Status"));
        Assert.That(type.LabelFor("1"),        Is.EqualTo("Closed"));
        Assert.That(type.ValueFor("open"),     Is.EqualTo("0"));
    }

    [Test]
    public void An_enum_is_found_by_the_last_segment_of_a_qualified_name_and_the_prefix_is_matched_without_regard_to_case()
    {
        var store = new FakeEnums();
        store.Items.Add(EnumRecord.Create("Status") with { Id = 1 });

        Assert.That(DefinedTypes.LookupEnum(store, Look("ENUM", "ns://enums/Status")), Is.Not.Null);
        Assert.That(DefinedTypes.LookupEnum(store, Look("enum", " Status ")),          Is.Not.Null);
    }

    [Test]
    public void An_enum_lookup_that_does_not_apply_gives_null()
    {
        var store = new FakeEnums();
        store.Items.Add(EnumRecord.Create("Status") with { Id = 1 });

        Assert.That(DefinedTypes.LookupEnum(store, Look("syntax", "Status")),  Is.Null); // another prefix
        Assert.That(DefinedTypes.LookupEnum(store, Look("enum",   "Missing")), Is.Null); // no such enum
        Assert.That(DefinedTypes.LookupEnum(store, Look("enum",   "")),        Is.Null);
        Assert.That(DefinedTypes.LookupEnum(null,  Look("enum",   "Status")),  Is.Null); // no Silo / a store that cannot keep enums
        Assert.Throws<ArgumentNullException>(() => DefinedTypes.LookupEnum(store, null!));
    }

    [Test]
    public void A_syntax_resolves_by_name_or_alias_and_exposes_its_ISyntax()
    {
        var store = new FakeSyntaxes();
        store.Items.AddRange(SyntaxSeeds.Defaults.Select((s, i) => s with { Id = i + 1 }));

        var byName  = DefinedTypes.LookupSyntax(store, Look("syntax", "jobject")) as DefinedSyntax;
        var byAlias = DefinedTypes.LookupSyntax(store, Look("syntax", "JOBJ")) as DefinedSyntax;
        Assert.That(byName,                              Is.Not.Null);
        Assert.That(byAlias,                             Is.Not.Null);
        Assert.That(byAlias!.Definition.Name,            Is.EqualTo("JObject"));
        Assert.That(byAlias.Kind,                        Is.EqualTo(DataKind.Syntax));
        Assert.That(byAlias.Prefix,                      Is.EqualTo("syntax"));
        Assert.That(byAlias.Uri.ToString(),              Is.EqualTo("ns://syntaxes/JObject"));
        Assert.That(byAlias.Syntax.IsValid("{\"a\":1}"), Is.True);
        Assert.That(byAlias.Syntax.IsValid("[1]"),       Is.False);
    }

    [Test]
    public void A_syntax_lookup_that_does_not_apply_gives_null()
    {
        var store = new FakeSyntaxes();
        store.Items.Add(SyntaxRecord.ForParser("Broken", "nosuchparser") with { Id = 1 });
        store.Items.Add(SyntaxRecord.ForParser("Fine",   "guid") with { Id = 2 });

        Assert.That(DefinedTypes.LookupSyntax(store, Look("enum",   "Fine")),    Is.Null);
        Assert.That(DefinedTypes.LookupSyntax(store, Look("syntax", "Missing")), Is.Null);
        Assert.That(DefinedTypes.LookupSyntax(store, Look("syntax", "Broken")),  Is.Null); // a row that cannot be built is not a type
        Assert.That(DefinedTypes.LookupSyntax(null,  Look("syntax", "Fine")),    Is.Null);
        Assert.That(DefinedTypes.LookupSyntax(store, Look("syntax", "Fine")),    Is.Not.Null);
    }
}
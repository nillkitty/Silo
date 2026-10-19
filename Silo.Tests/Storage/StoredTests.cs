using Silo.Contracts.Providers.Data;
using Silo.Storage;
using Silo.Unicode;

namespace Silo.Tests.Storage;

[TestFixture]
public class StoredTests
{
    private MemoryStoredStore _store = null!;

    [SetUp]
    public void SetUp() => _store = new MemoryStoredStore();

    private Stored<T> Make<T>(string key, StoredScope scope = StoredScope.Personal) => new(key, scope, _store);

    public sealed record Shape(string Name, int[] Sides, Dictionary<string, double>? Extra = null);

    [Test]
    public void KeepsValuesOfAnyShape()
    {
        var shape = new Shape("tri", [3, 4, 5], new Dictionary<string, double> { ["area"] = 6.0 });
        var s = Make<Shape>("shape:tri");
        s.Set(shape);

        var back = s.Get()!;
        Assert.That(back.Name, Is.EqualTo("tri"));
        Assert.That(back.Sides, Is.EqualTo(new[] { 3, 4, 5 }));
        Assert.That(back.Extra!["area"], Is.EqualTo(6.0));

        Make<int[]>("numbers").Set([1, 2, 3]);
        Assert.That(Make<int[]>("numbers").Get(), Is.EqualTo(new[] { 1, 2, 3 }));
        Make<string>("text").Set("hi \"there\"");
        Assert.That(Make<string>("text").Get(), Is.EqualTo("hi \"there\""));
    }

    [Test]
    public void RecordsTheFullTypeAndTheFormat()
    {
        Make<Shape>("shape:tri").Set(new Shape("tri", []));
        var record = _store.GetStored("shape:tri")!;
        Assert.That(record.TypeName, Does.StartWith("Silo.Tests.Storage.StoredTests+Shape, "));
        Assert.That(record.Format, Is.EqualTo("json"));
        Assert.That(record.Scope, Is.EqualTo(StoredScope.Personal));
        Assert.That(Make<Shape>("shape:tri").StoredTypeName, Is.EqualTo(record.TypeName));
    }

    [Test]
    public void StoredTextNeverNamesATypeToCreate()
    {
        // a hostile value cannot make the reader create the type it names
        _store.SetStored("evil", "x", "json", "{\"$type\":\"System.IO.FileInfo, System.IO.FileSystem\",\"Name\":\"x\"}");
        var read = Make<Shape>("evil").Get();
        Assert.That(read, Is.InstanceOf<Shape>());
    }

    [Test]
    public void TheModifiedTimeMovesOnlyWhenTheValueDoes()
    {
        var s = Make<string>("k");
        s.Set("a");
        var first = s.Modified;
        var created = _store.GetStored("k")!.Created;

        s.Set("a");
        Assert.That(s.Modified, Is.EqualTo(first));

        s.Set("b");
        Assert.That(s.Modified, Is.GreaterThan(first!));
        Assert.That(_store.GetStored("k")!.Created, Is.EqualTo(created));
    }

    [Test]
    public void KeysIgnoreCaseAndScopesAreApart()
    {
        Make<string>("Your:Green").Set("a");
        Assert.That(Make<string>("your:green").Get(), Is.EqualTo("a"));
        Assert.That(Make<string>("your:green", StoredScope.Shared).Exists, Is.False);

        Make<string>("your:green", StoredScope.Shared).Set("shared");
        Assert.That(Make<string>("your:green").Get(), Is.EqualTo("a"));
        Assert.That(Make<string>("your:green", StoredScope.Shared).Get(), Is.EqualTo("shared"));
    }

    [Test]
    public void ReadsTheFallbackWhenNothingIsStored()
    {
        var s = Make<int>("none");
        Assert.That(s.Exists, Is.False);
        Assert.That(s.Get(42), Is.EqualTo(42));
        Assert.That(s.TryGet(out _), Is.False);
    }

    [Test]
    public void UnreadableTextIsAnError()
    {
        _store.SetStored("bad", "x", "json", "{not json");
        var s = Make<Shape>("bad");
        Assert.That(s.TryGet(out _), Is.False);
        var ex = Assert.Throws<StoredDataException>(() => s.Get());
        Assert.That(ex.Key, Is.EqualTo("bad"));
        Assert.That(ex.Message, Does.Contain("Shape"));
    }

    [Test]
    public void DeletesAndSaysWhetherThereWasAnything()
    {
        var s = Make<string>("k");
        Assert.That(s.Delete(), Is.False);
        s.Set("a");
        Assert.That(s.Delete(), Is.True);
        Assert.That(s.Exists, Is.False);
    }

    [Test]
    public void WithNoSiloItCannotStoreButReadsAsEmpty()
    {
        var s = new Stored<string>("k", StoredScope.Personal, () => null);
        Assert.That(s.IsAvailable, Is.False);
        Assert.That(s.Exists, Is.False);
        Assert.That(s.Delete(), Is.False);
        Assert.Throws<InvalidOperationException>(() => s.Set("a"));
    }

    [Test]
    public void TellsWhoIsListening()
    {
        var seen = new List<string>();
        var props = new List<string?>();
        void Handler(object? o, StoredChangedEventArgs e) => seen.Add((e.Removed ? "-" : "+") + e.Key);
        Silo.Storage.Stored.Changed += Handler;
        try
        {
            var s = Make<string>("watched");
            s.PropertyChanged += (_, e) => props.Add(e.PropertyName);
            s.Set("a");
            s.Set("a"); // nothing changed, nothing said
            s.Set("b");
            s.Delete();
        }
        finally
        {
            Silo.Storage.Stored.Changed -= Handler;
        }

        Assert.That(seen, Is.EqualTo(new[] { "+watched", "+watched", "-watched" }));
        Assert.That(props, Does.Contain("Value"));
    }

    [Test]
    public void KeepsCodePointsAsReadableText()
    {
        Make<CodePoint>("one").Set(new CodePoint(0x1F7E2));
        Make<CodePoint[]>("many").Set([new CodePoint(0x25B6), new CodePoint(0x221A)]);
        Assert.That(_store.GetStored("one")!.Data, Is.EqualTo("\"U+1F7E2\""));
        Assert.That(_store.GetStored("many")!.Data, Is.EqualTo("[\"U+25B6\",\"U+221A\"]"));
    }
}

[TestFixture]
public class StoredTierTests
{
    [Test]
    public void PermanentAndSessionItemsWithTheSameKeyAreApart()
    {
        var store     = new MemoryStoredStore();
        var permanent = new Stored<string>("window:last", StoredScope.Personal, store);
        var session   = new Stored<string>("window:last", StoredScope.Personal, store, StoredTier.Session);

        permanent.Set("kept");
        Assert.That(session.Exists, Is.False);

        session.Set("only this session");
        Assert.That(permanent.Get(), Is.EqualTo("kept"));
        Assert.That(session.Get(), Is.EqualTo("only this session"));
        Assert.That(store.ListStored("window:", tier: StoredTier.Session).Count, Is.EqualTo(1));

        Assert.That(session.Delete(), Is.True);
        Assert.That(permanent.Exists, Is.True);
    }

    [Test]
    public void TheDefaultTierIsPermanent()
    {
        var store = new MemoryStoredStore();
        var s     = new Stored<int>("n", StoredScope.Personal, store);
        Assert.That(s.Tier, Is.EqualTo(StoredTier.Permanent));
        s.Set(1);
        Assert.That(store.GetStored("n", tier: StoredTier.Permanent), Is.Not.Null);
        Assert.That(store.GetStored("n", tier: StoredTier.Session), Is.Null);
    }

    [Test]
    public void ChangeEventsSayWhichTierChanged()
    {
        var store = new MemoryStoredStore();
        var seen  = new List<StoredTier>();
        void Handler(object? o, StoredChangedEventArgs e) => seen.Add(e.Tier);
        Silo.Storage.Stored.Changed += Handler;
        try
        {
            new Stored<int>("a", StoredScope.Personal, store, StoredTier.Session).Set(1);
            new Stored<int>("b", StoredScope.Personal, store).Set(1);
        }
        finally
        {
            Silo.Storage.Stored.Changed -= Handler;
        }

        Assert.That(seen, Is.EqualTo(new[] { StoredTier.Session, StoredTier.Permanent }));
    }
}

[TestFixture]
public class YourConstantsTests
{
    private MemoryStoredStore _store = null!;
    private YourConstants     _your = null!;

    [SetUp]
    public void SetUp()
    {
        _store = new MemoryStoredStore();
        _your  = new YourConstants(() => _store);
    }

    [Test]
    public void SavesTheDiagramsConstants()
    {
        _your.SetCodePoint("green", new CodePoint(0x1F7E2));
        _your.SetCodePoints("mychars", CodePoint.Enumerate("▶√®™▼▽"));

        var green = _your.Get("green")!;
        Assert.That(green.Text, Is.EqualTo("🟢"));
        Assert.That(green.IsList, Is.False);
        Assert.That(green.FullName, Is.EqualTo("your:green"));

        var chars = _your.Get("MyChars")!;
        Assert.That(chars.Text, Is.EqualTo("▶√®™▼▽"));
        Assert.That(chars.IsList, Is.True);
        Assert.That(chars.CodePoints, Has.Count.EqualTo(6));

        Assert.That(_store.GetStored("your:green")!.TypeName, Does.StartWith("Silo.Unicode.CodePoint, "));
        Assert.That(_store.GetStored("your:mychars")!.TypeName, Does.StartWith("Silo.Unicode.CodePoint[], "));
        Assert.That(_store.GetStored("your:green")!.Scope, Is.EqualTo(StoredScope.Personal));
    }

    [Test]
    public void ListsThemByName()
    {
        _your.SetCodePoint("yellow", new CodePoint(0x1F7E1));
        _your.SetCodePoint("blue", new CodePoint(0x1F535));
        _your.SetCodePoints("Alpha", CodePoint.Enumerate("ab"));
        _store.SetStored("shortcut:blue", "x", "json", "{}");
        _store.SetStored("your:broken name", "x", "json", "\"U+41\"");
        _store.SetStored("your:junk", "x", "json", "{\"a\":1}");

        Assert.That(_your.List().Select(c => c.Name), Is.EqualTo(new[] { "Alpha", "blue", "yellow" }));
    }

    [Test]
    public void ReplacingChangesWhetherItIsAList()
    {
        _your.SetCodePoint("x", new CodePoint('a'));
        _your.SetCodePoints("x", CodePoint.Enumerate("abc"));
        Assert.That(_your.Get("x")!.IsList, Is.True);
        _your.SetCodePoint("X", new CodePoint('z'));
        Assert.That(_your.Get("x")!.Text, Is.EqualTo("z"));
        Assert.That(_your.List(), Has.Count.EqualTo(1));
    }

    [Test]
    public void RefusesNamesThatCouldNotBeWrittenInAnExpression()
    {
        foreach (string bad in new[] { "", " ", "1a", "a b", "a:b", "a-b", "a.b", new string('x', 65) })
        {
            Assert.That(YourConstants.IsValidName(bad), Is.False, bad);
            Assert.Throws<ArgumentException>(() => _your.SetCodePoint(bad, new CodePoint('a')), bad);
        }

        Assert.That(YourConstants.IsValidName("_x9"), Is.True);
        Assert.Throws<ArgumentException>(() => _your.SetCodePoints("ok", []));
    }

    [Test]
    public void DeletesByName()
    {
        _your.SetCodePoint("green", new CodePoint(0x1F7E2));
        Assert.That(_your.Delete("GREEN"), Is.True);
        Assert.That(_your.Get("green"), Is.Null);
        Assert.That(_your.Delete("green"), Is.False);
        Assert.That(_your.Delete("not a name"), Is.False);
    }

    [Test]
    public void TheConstantsAreVariablesOfTheYourNamespace()
    {
        _your.SetCodePoint("blue", new CodePoint(0x1F535));
        _your.SetCodePoints("mychars", CodePoint.Enumerate("▶√®"));
        var scope = _your.AsScope(new Silo.Expressions.VariableScope().Set("who", "me"));

        Assert.That(Silo.Expressions.Template.Expand("${your:blue}|${YOUR:Blue}|${your:mychars}|${who}", scope), Is.EqualTo("🔵|🔵|▶√®|me"));

        var ex = Assert.Throws<Silo.Expressions.ExpressionException>(() => Silo.Expressions.Template.Expand("${your:nope}", scope));
        Assert.That(ex.MissingVariableName, Is.Not.Null);

        // a constant saved after the scope was made is seen
        _your.SetCodePoint("late", new CodePoint('L'));
        Assert.That(Silo.Expressions.Template.Expand("${your:late}", scope), Is.EqualTo("L"));
    }

    [Test]
    public void WithNoSiloThereAreNoConstants()
    {
        var none = new YourConstants(() => null);
        Assert.That(none.IsAvailable, Is.False);
        Assert.That(none.List(), Is.Empty);
        Assert.That(none.Get("a"), Is.Null);
        Assert.Throws<InvalidOperationException>(() => none.SetCodePoint("a", new CodePoint('a')));
    }
}

using System.IO;
using Silo.DbModel.Meta;
using Silo.DbModel.Sqlite;
using Silo.DbModel.Worlds;
using Silo.Providers.Data;
using Silo.Providers.Data.Validation;
using Silo.Sync;

namespace Silo.Tests;

public class MetaverseSyncTests
{
    private string _dir = null!;

    [SetUp]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "silo-mv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
            // still open: left for the temp cleaner
        }
    }

    private sealed class FakeSource(string name, params (string Key, string Name, int Qty)[] rows) : ISchema
    {
        public string SchemaProvider    => "fake";
        public string SchemaDisplayName => name;
        public Uri?   SchemaUri         => new("fake://" + name + "/");

        public IReadOnlyList<SchemaType> GetSchema() =>
        [
            new SchemaType("Item", "Item",
            [
                new SchemaField("Id",   "string", typeof(string), WorldFieldFlags.Key),
                new SchemaField("Name", "string", typeof(string)),
                new SchemaField("Qty",  "int",    typeof(int))
            ])
        ];

        public IEnumerable<WorldObject> GetObjects(string typeName, CancellationToken cancel = default) =>
            rows.Select(r => new WorldObject(r.Key, null, new Dictionary<string, object?> { ["Id"] = r.Key, ["Name"] = r.Name, ["Qty"] = r.Qty }));
    }

    private (IDatabaseProvider Db, IEntityStore Store) NewSilo()
    {
        var db = new SqliteNewSiloBuilder().Build(Path.Combine(_dir, "test.silo"));
        return (db, (IEntityStore)db);
    }

    [Test]
    public void SchemaIsAbsorbedAndStaysTheSameWhenAbsorbedAgain()
    {
        var (db, store) = NewSilo();
        var source = new FakeSource("a");

        var first  = WorldSchemaAbsorber.Absorb(db, store, "fake-a", source);
        var second = WorldSchemaAbsorber.Absorb(db, store, "fake-a", source);

        Assert.Multiple(() =>
                        {
                            Assert.That(first.TypesAdded,    Is.EqualTo(1));
                            Assert.That(first.FieldsAdded,   Is.EqualTo(3));
                            Assert.That(second.TypesAdded,   Is.EqualTo(0));
                            Assert.That(second.FieldsAdded,  Is.EqualTo(0));
                            Assert.That(second.FieldsChanged, Is.EqualTo(0));
                            Assert.That(store.ListItems<WorldField>("fake-a").Select(f => f.Name), Is.EquivalentTo(new[] { "Id", "Name", "Qty" }));
                            Assert.That(store.ListWorlds().Any(w => w.Key == "fake-a"), Is.True);
                        });
    }

    [Test]
    public void ObjectsAreProjectedThenUpdatedWithoutDuplicates()
    {
        var (db, store) = NewSilo();
        var source   = new FakeSource("a", ("1", "Bolt", 5), ("2", "Nut", 7));
        var absorbed = WorldSchemaAbsorber.Absorb(db, store, "fake-a", source);
        var (type, fields) = absorbed.Types[0];
        var sync = new InboundSync(db, store);
        sync.EnsureMapping(absorbed.World, type, fields, "Part");

        var run1 = sync.Run(absorbed.World, type, source.GetObjects("Item"));
        var run2 = sync.Run(absorbed.World, type, source.GetObjects("Item"));
        var changed = new FakeSource("a", ("1", "Bolt", 9), ("2", "Nut", 7));
        var run3    = sync.Run(absorbed.World, type, changed.GetObjects("Item"));

        var objs = store.ListItems<MvObj>();
        Assert.Multiple(() =>
                        {
                            Assert.That(run1.Projected, Is.EqualTo(2));
                            Assert.That(run2.Projected + run2.Updated, Is.EqualTo(0));
                            Assert.That(run2.Unchanged, Is.EqualTo(2));
                            Assert.That(run3.Updated, Is.EqualTo(1));
                            Assert.That(objs, Has.Count.EqualTo(2));
                            Assert.That(store.ListItems<Connector>(), Has.Count.EqualTo(2));
                            Assert.That(objs.Select(o => Convert.ToInt32(o.FieldValues["Qty"])), Is.EquivalentTo(new[] { 9, 7 }));
                        });
    }

    [Test]
    public void AJoinRuleLinksAnObjectToAnExistingMetaverseObject()
    {
        var (db, store) = NewSilo();
        var a  = new FakeSource("a", ("1", "Bolt", 5));
        var b  = new FakeSource("b", ("X", "Bolt", 50));
        var sync = new InboundSync(db, store);

        var absA = WorldSchemaAbsorber.Absorb(db, store, "fake-a", a);
        sync.EnsureMapping(absA.World, absA.Types[0].Type, absA.Types[0].Fields, "Part");
        sync.Run(absA.World, absA.Types[0].Type, a.GetObjects("Item"));

        var absB = WorldSchemaAbsorber.Absorb(db, store, "fake-b", b);
        var (mappingB, _) = sync.EnsureMapping(absB.World, absB.Types[0].Type, absB.Types[0].Fields, "Part");
        mappingB.Joining = new JoinRules { Type = JoinType.FieldValueEquals, MvFieldName = "Name", WorldField = absB.Types[0].Fields.First(f => f.Name == "Name") };
        mappingB.Projection = new ProjRules { Enabled = false };
        db.AddItem(mappingB);

        var result = sync.Run(absB.World, absB.Types[0].Type, b.GetObjects("Item"));

        Assert.Multiple(() =>
                        {
                            Assert.That(result.Joined, Is.EqualTo(1));
                            Assert.That(store.ListItems<MvObj>(), Has.Count.EqualTo(1));
                            Assert.That(store.ListItems<Connector>().Select(c => c.World), Is.EquivalentTo(new[] { "fake-a", "fake-b" }));
                        });
    }

    [Test]
    public void AFilterDecidesWhichObjectsProject()
    {
        var qty = new WorldField { Name = "Qty" };
        var filter = new WorldFilter { Operation = FilterOperation.FieldValueNumericMatch, Field = qty, Value = "7" };
        Assert.Multiple(() =>
                        {
                            Assert.That(InboundSync.Passes(filter, new Dictionary<string, object?> { ["Qty"] = 7 }),  Is.True);
                            Assert.That(InboundSync.Passes(filter, new Dictionary<string, object?> { ["Qty"] = 8 }),  Is.False);
                            Assert.That(InboundSync.Passes(null,   new Dictionary<string, object?>()),                Is.True);
                        });
    }

    [Test]
    public void AFolderDescribesItsEntries()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "hi");
        var folder = Silo.Connectors.File.FileConnection.FromPath(new Silo.Connectors.File.FileConnector(), _dir);

        var objects = folder.GetObjects("FileEntry").ToList();

        Assert.Multiple(() =>
                        {
                            Assert.That(folder.GetSchema()[0].Fields.Select(f => f.Name), Does.Contain("Path"));
                            Assert.That(objects.Any(o => o.Values["Name"] as string == "a.txt" && (long)o.Values["Length"]! == 2), Is.True);
                        });
    }
}

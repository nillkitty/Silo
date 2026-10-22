using Silo.Providers.Data;
using Silo.Storage;
using Silo.Unicode;

namespace Silo.Tests.Storage;

/// <summary>
///     The Unicode Explorer keeps its collection as <c>Stored&lt;CollectedCharacter[]&gt;</c>; it has to come back as
///     it went in.
/// </summary>
[TestFixture]
public class TrayPersistenceTests
{
    [Test]
    public void TheCollectionRoundTripsWithLabelsAndRepeats()
    {
        var store = new MemoryStoredStore();
        var tray  = new Stored<CollectedCharacter[]>("unicodeexplorer:tray", StoredScope.Personal, store);

        tray.Set([
                     new CollectedCharacter(new CodePoint(0x1F7E2), "green"), new CollectedCharacter(new CodePoint(0x25B6)),
                     new CollectedCharacter(new CodePoint(0x25B6))
                 ]);

        var back = new Stored<CollectedCharacter[]>("unicodeexplorer:tray", StoredScope.Personal, store).Get()!;
        Assert.That(back.Select(c => c.CodePoint.Value), Is.EqualTo(new[] { 0x1F7E2, 0x25B6, 0x25B6 }));
        Assert.That(back[0].Label,                       Is.EqualTo("green"));
        Assert.That(back[1].Label,                       Is.Null);
    }

    [Test]
    public void TheCollectionIsKeptAsReadableJson()
    {
        var store = new MemoryStoredStore();
        new Stored<CollectedCharacter[]>("unicodeexplorer:tray", StoredScope.Personal, store).Set([
                                                                                                      new
                                                                                                          CollectedCharacter(new
                                                                                                                                 CodePoint(0x1F7E2),
                                                                                                                             "green")
                                                                                                  ]);

        string data = store.GetStored("unicodeexplorer:tray")!.Data;
        Assert.That(data, Does.Contain("U+1F7E2"));
        Assert.That(data, Does.Contain("green"));
    }

    [Test]
    public void AnEmptyCollectionIsTheSameAsNone()
    {
        var store = new MemoryStoredStore();
        var tray  = new Stored<CollectedCharacter[]>("unicodeexplorer:tray", StoredScope.Personal, store);
        tray.Set([new CollectedCharacter(new CodePoint('a'))]);
        Assert.That(tray.Delete(), Is.True);
        Assert.That(tray.Exists,   Is.False);
    }
}
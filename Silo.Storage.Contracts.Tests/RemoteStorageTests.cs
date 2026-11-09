using Telefrag.Storage;

namespace Silo.Storage.Contracts.Tests;

/// <summary>Hands bytes straight to a <see cref="StorageServer" />, as a transport would after authenticating the caller.</summary>
internal sealed class LoopbackTransport(StorageServer server) : IWireTransport
{
    public Task<byte[]> CallAsync(byte[] request, CancellationToken ct) => server.HandleAsync(request, ct);

    public IAsyncEnumerable<byte[]> ChangesAsync(CancellationToken ct) => server.ChangesAsync(ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

[TestClass]
public class RemoteStorageTests
{
    private static async Task<(RemoteStorage Client, InMemoryStorage Server)> ConnectAsync()
    {
        InMemoryStorage server = new("Test Silo");
        RemoteStorage client = await RemoteStorage.ConnectAsync(new LoopbackTransport(new StorageServer(server)));
        return (client, server);
    }

    [TestMethod]
    public async Task Hello_TellsTheClientWhoTheSiloIs()
    {
        var (client, _) = await ConnectAsync();
        SiloInfo info = await client.GetInfoAsync();
        Assert.AreEqual("Test Silo", info.Name);
        Assert.AreEqual(SiloWire.SchemaId, info.SchemaId);
        CollectionAssert.Contains(info.Capabilities, "tables");
    }

    [TestMethod]
    public async Task Stored_RoundTripsThroughTheWire()
    {
        var (client, server) = await ConnectAsync();

        Assert.IsNull(await client.Stored.GetAsync("a/b"));

        StoredItem set = await client.Stored.SetAsync("a/b", "System.String", "json", "\"hi\"");
        Assert.AreEqual("a/b", set.Key);
        Assert.AreEqual(StoredScope.Personal, set.Scope);

        StoredItem? got = await client.Stored.GetAsync("A/B");
        Assert.AreEqual("\"hi\"", got?.Data, "keys find each other in any case");
        Assert.AreEqual("\"hi\"", (await server.Stored.GetAsync("a/b"))?.Data, "the server holds it");

        await client.Stored.SetAsync("a/c", "System.String", "json", "\"x\"");
        await client.Stored.SetAsync("z", "System.String", "json", "\"y\"");
        IReadOnlyList<StoredItem> listed = await client.Stored.ListAsync("a/");
        CollectionAssert.AreEqual(new[] { "a/b", "a/c" }, listed.Select(i => i.Key).ToArray());

        Assert.AreEqual(0, (await client.Stored.ListAsync("a/", StoredScope.Shared)).Count, "scopes are apart");
        Assert.AreEqual(0, (await client.Stored.ListAsync("a/", StoredScope.Personal, StoredTier.Session)).Count, "tiers are apart");

        Assert.IsTrue(await client.Stored.DeleteAsync("a/b"));
        Assert.IsFalse(await client.Stored.DeleteAsync("a/b"));
        Assert.IsNull(await client.Stored.GetAsync("a/b"));
    }

    [TestMethod]
    public async Task EmptyLists_AreEmptyNotNull()
    {
        var (client, _) = await ConnectAsync();
        Assert.AreEqual(0, (await client.Stored.ListAsync()).Count);
        Assert.AreEqual(0, (await client.Tables.ListAsync()).Count);
    }

    [TestMethod]
    public async Task Stored_UnchangedSetKeepsTheModifiedTime()
    {
        var (client, _) = await ConnectAsync();
        StoredItem first = await client.Stored.SetAsync("k", "t", "json", "1");
        await Task.Delay(20);
        StoredItem again = await client.Stored.SetAsync("k", "t", "json", "1");
        Assert.AreEqual(first.Modified, again.Modified);

        StoredItem changed = await client.Stored.SetAsync("k", "t", "json", "2");
        Assert.IsTrue(changed.Modified > first.Modified);
        Assert.AreEqual(first.Created, changed.Created);
    }

    [TestMethod]
    public async Task Tables_RoundTripThroughTheWire()
    {
        var (client, _) = await ConnectAsync();

        TableItem saved = await client.Tables.SaveAsync(new TableItem(0, "Sales", "Q3", "{\"rows\":[]}", default, default));
        Assert.IsTrue(saved.Id > 0);
        Assert.AreEqual("Sales", saved.Name);

        TableItem renamed = await client.Tables.SaveAsync(saved with { Name = "Sales 2" });
        Assert.AreEqual(saved.Id, renamed.Id);
        Assert.AreEqual(saved.Created, renamed.Created);

        Assert.AreEqual("Sales 2", (await client.Tables.GetAsync(saved.Id))?.Name);
        Assert.AreEqual(1, (await client.Tables.ListAsync()).Count);
        Assert.IsNull(await client.Tables.GetAsync(999));

        Assert.IsTrue(await client.Tables.DeleteAsync(saved.Id));
        Assert.IsFalse(await client.Tables.DeleteAsync(saved.Id));
    }

    [TestMethod]
    public async Task ServerErrors_ArriveAsSiloStorageExceptions()
    {
        var (client, _) = await ConnectAsync();
        SiloStorageException ex = await Assert.ThrowsExactlyAsync<SiloStorageException>(
            async () => await client.Tables.SaveAsync(new TableItem(404, "Nope", "", "", default, default)));
        Assert.AreEqual("not-found", ex.Code);
    }

    [TestMethod]
    public async Task Changes_ArePushedToTheClient()
    {
        var (client, _) = await ConnectAsync();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        List<StorageChange> seen = new();
        Task listener = Task.Run(async () =>
        {
            await foreach (StorageChange c in client.ChangesAsync(cts.Token))
            {
                seen.Add(c);
                if (seen.Count == 3) break;
            }
        });

        await Task.Delay(200);    // let it subscribe
        await client.Stored.SetAsync("k", "t", "json", "1");
        await client.Stored.SetAsync("k", "t", "json", "2");
        await client.Stored.DeleteAsync("k");
        await listener;

        CollectionAssert.AreEqual(new[] { StorageChangeKind.Added, StorageChangeKind.Changed, StorageChangeKind.Removed },
                                  seen.Select(c => c.Kind).ToArray());
        Assert.IsTrue(seen.All(c => c is { Area: "stored", Key: "k" }));
    }

    [TestMethod]
    public async Task Connecting_WithADifferentSchema_IsRefusedUpFront()
    {
        StorageServer handler = new(new InMemoryStorage());
        byte[] hello = SiloWire.Write(new HelloRequest("0000000000000000"));
        object? reply = SiloWire.ReadReply(await handler.HandleAsync(hello));
        Assert.AreEqual("schema-mismatch", (reply as SiloError)?.Code);
    }

    // ----- what a hostile or confused client can do

    [TestMethod]
    public async Task Server_RefusesMessagesThatAreNotRequests()
    {
        StorageServer handler = new(new InMemoryStorage());

        // a reply type is not something a client may send
        object? reply = SiloWire.ReadReply(await handler.HandleAsync(SiloWire.Write(new SiloError("x", "y"))));
        Assert.AreEqual("bad-request", (reply as SiloError)?.Code);

        // garbage
        reply = SiloWire.ReadReply(await handler.HandleAsync([1, 2, 3, 4, 5, 6, 7, 8]));
        Assert.AreEqual("bad-request", (reply as SiloError)?.Code);

        // truncated
        byte[] good = SiloWire.Write(new StoredGetRequest("key", StoredScope.Personal, StoredTier.Permanent));
        reply = SiloWire.ReadReply(await handler.HandleAsync(good[..^3]));
        Assert.AreEqual("bad-request", (reply as SiloError)?.Code);

        // a bare number
        reply = SiloWire.ReadReply(await handler.HandleAsync(SiloWire.Write(42)));
        Assert.AreEqual("bad-request", (reply as SiloError)?.Code);
    }

    [TestMethod]
    public void Schema_CoversEveryMessage()
    {
        Assert.AreEqual(16, SiloWire.SchemaId.Length);
        foreach (Type t in new[] { typeof(HelloRequest), typeof(StoredGetRequest), typeof(StoredListRequest), typeof(StoredSetRequest),
                                   typeof(StoredDeleteRequest), typeof(TableListRequest), typeof(TableGetRequest), typeof(TableSaveRequest),
                                   typeof(TableDeleteRequest), typeof(StoredListReply), typeof(TableListReply), typeof(SiloError),
                                   typeof(StorageChange), typeof(SiloInfo), typeof(StoredItem), typeof(TableItem) })
            Assert.IsTrue(StorageManager.GetCombinatorsForType(t).Any(), $"{t.Name} has no combinator");
    }
}

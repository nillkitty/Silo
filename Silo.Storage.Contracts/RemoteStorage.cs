using Telefrag.Storage;

namespace Silo.Storage;

/// <summary>Carries bytes between a <see cref="RemoteStorage" /> and a <see cref="StorageServer" />: gRPC, a socket, a pipe...</summary>
public interface IWireTransport : IAsyncDisposable
{
    /// <summary>Sends one request and returns its reply.</summary>
    Task<byte[]> CallAsync(byte[] request, CancellationToken ct);

    /// <summary>The changes the server pushes, as they happen.</summary>
    IAsyncEnumerable<byte[]> ChangesAsync(CancellationToken ct);
}

/// <summary>A Silo's storage that lives behind a <see cref="IWireTransport" />.</summary>
public sealed class RemoteStorage : ISiloStorage, IStoredStorage, ITableStorage
{
    private readonly IWireTransport _transport;

    private RemoteStorage(IWireTransport transport) => _transport = transport;

    /// <summary>Connects: tells the Silo which messages this client speaks, and fails if it speaks others.</summary>
    public static async Task<RemoteStorage> ConnectAsync(IWireTransport transport, CancellationToken ct = default)
    {
        RemoteStorage storage = new(transport);
        storage.Info = await storage.CallAsync<SiloInfo>(new HelloRequest(SiloWire.SchemaId), ct).ConfigureAwait(false)
                       ?? throw new SiloStorageException("failed", "The Silo did not introduce itself");
        return storage;
    }

    public SiloInfo Info { get; private set; } = null!;

    public IStoredStorage Stored => this;

    public ITableStorage Tables => this;

    public ValueTask<SiloInfo> GetInfoAsync(CancellationToken ct = default) => ValueTask.FromResult(Info);

    public async IAsyncEnumerable<StorageChange> ChangesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (byte[] data in _transport.ChangesAsync(ct).WithCancellation(ct).ConfigureAwait(false))
            yield return SiloWire.ReadChange(data);
    }

    public ValueTask DisposeAsync() => _transport.DisposeAsync();

    // ----- the calls

    private async Task<T?> CallAsync<T>(object request, CancellationToken ct) where T : class
    {
        byte[] reply = await _transport.CallAsync(SiloWire.Write(request), ct).ConfigureAwait(false);
        return Unwrap<T>(SiloWire.ReadReply(reply));
    }

    private async Task<bool> CallBoolAsync(object request, CancellationToken ct)
    {
        byte[] reply = await _transport.CallAsync(SiloWire.Write(request), ct).ConfigureAwait(false);
        return SiloWire.ReadReply(reply) switch
        {
            bool b       => b,
            SiloError e  => throw new SiloStorageException(e.Code, e.Message),
            var other    => throw new SiloStorageException("failed", $"A bool was expected but {other?.GetType().Name ?? "nothing"} came back")
        };
    }

    private static T? Unwrap<T>(object? reply) where T : class => reply switch
    {
        null         => null,
        SiloError e  => throw new SiloStorageException(e.Code, e.Message),
        T typed      => typed,
        _            => throw new SiloStorageException("failed", $"{typeof(T).Name} was expected but {reply.GetType().Name} came back")
    };

    private async Task<T> RequireAsync<T>(object request, CancellationToken ct) where T : class =>
        await CallAsync<T>(request, ct).ConfigureAwait(false) ?? throw new SiloStorageException("failed", $"The Silo returned no {typeof(T).Name}");

    // ----- stored

    async ValueTask<StoredItem?> IStoredStorage.GetAsync(string key, StoredScope scope, StoredTier tier, CancellationToken ct) =>
        await CallAsync<StoredItem>(new StoredGetRequest(key, scope, tier), ct).ConfigureAwait(false);

    async ValueTask<IReadOnlyList<StoredItem>> IStoredStorage.ListAsync(string? keyPrefix, StoredScope scope, StoredTier tier, CancellationToken ct) =>
        (await RequireAsync<StoredListReply>(new StoredListRequest(keyPrefix, scope, tier), ct).ConfigureAwait(false)).Items;

    async ValueTask<StoredItem> IStoredStorage.SetAsync(string key, string typeName, string format, string data, StoredScope scope,
                                                        StoredTier tier, CancellationToken ct) =>
        await RequireAsync<StoredItem>(new StoredSetRequest(key, typeName, format, data, scope, tier), ct).ConfigureAwait(false);

    async ValueTask<bool> IStoredStorage.DeleteAsync(string key, StoredScope scope, StoredTier tier, CancellationToken ct) =>
        await CallBoolAsync(new StoredDeleteRequest(key, scope, tier), ct).ConfigureAwait(false);

    // ----- tables

    async ValueTask<IReadOnlyList<TableItem>> ITableStorage.ListAsync(CancellationToken ct) =>
        (await RequireAsync<TableListReply>(new TableListRequest(), ct).ConfigureAwait(false)).Items;

    async ValueTask<TableItem?> ITableStorage.GetAsync(int id, CancellationToken ct) =>
        await CallAsync<TableItem>(new TableGetRequest(id), ct).ConfigureAwait(false);

    async ValueTask<TableItem> ITableStorage.SaveAsync(TableItem table, CancellationToken ct) =>
        await RequireAsync<TableItem>(new TableSaveRequest(table), ct).ConfigureAwait(false);

    async ValueTask<bool> ITableStorage.DeleteAsync(int id, CancellationToken ct) =>
        await CallBoolAsync(new TableDeleteRequest(id), ct).ConfigureAwait(false);
}

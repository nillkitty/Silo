using System.Runtime.CompilerServices;
using Telefrag.Storage;

namespace Silo.Storage;

/// <summary>
///     Answers requests from remote clients using an <see cref="ISiloStorage" />, whatever carries the bytes: a transport
///     (gRPC, a socket, a pipe) hands each request to <see cref="HandleAsync" /> and sends back what it returns, and
///     forwards <see cref="ChangesAsync" /> to clients that subscribe.  Who the caller is, and whether they may, is the
///     transport's to settle before it gets here.
/// </summary>
public sealed class StorageServer(ISiloStorage storage)
{
    public async Task<byte[]> HandleAsync(byte[] request, CancellationToken ct = default)
    {
        object? reply;
        try
        {
            reply = await DispatchAsync(SiloWire.ReadRequest(request), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or CombinatorNotFoundException or EndOfStreamException
                                      or InvalidCastException or DeserializationException)
        {
            reply = new SiloError("bad-request", ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            reply = new SiloError("not-found", ex.Message);
        }
        catch (Exception ex)
        {
            reply = new SiloError("failed", ex.Message);
        }

        return SiloWire.Write(reply);
    }

    public async IAsyncEnumerable<byte[]> ChangesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (StorageChange change in storage.ChangesAsync(ct).ConfigureAwait(false))
            yield return SiloWire.WriteChange(change);
    }

    private async Task<object?> DispatchAsync(object? request, CancellationToken ct)
    {
        switch (request)
        {
            case HelloRequest hello:
                if (hello.SchemaId != SiloWire.SchemaId)
                    return new SiloError("schema-mismatch", $"The client speaks schema {hello.SchemaId} but this Silo speaks {SiloWire.SchemaId}");
                return await storage.GetInfoAsync(ct).ConfigureAwait(false) with { SchemaId = SiloWire.SchemaId };

            case StoredGetRequest r:
                return await storage.Stored.GetAsync(r.Key, r.Scope, r.Tier, ct).ConfigureAwait(false);

            case StoredListRequest r:
                return new StoredListReply([.. await storage.Stored.ListAsync(r.KeyPrefix, r.Scope, r.Tier, ct).ConfigureAwait(false)]);

            case StoredSetRequest r:
                return await storage.Stored.SetAsync(r.Key, r.TypeName, r.Format, r.Data, r.Scope, r.Tier, ct).ConfigureAwait(false);

            case StoredDeleteRequest r:
                return await storage.Stored.DeleteAsync(r.Key, r.Scope, r.Tier, ct).ConfigureAwait(false);

            case TableListRequest:
                return new TableListReply([.. await storage.Tables.ListAsync(ct).ConfigureAwait(false)]);

            case TableGetRequest r:
                return await storage.Tables.GetAsync(r.Id, ct).ConfigureAwait(false);

            case TableSaveRequest r:
                return await storage.Tables.SaveAsync(r.Table, ct).ConfigureAwait(false);

            case TableDeleteRequest r:
                return await storage.Tables.DeleteAsync(r.Id, ct).ConfigureAwait(false);

            case null:
                return new SiloError("bad-request", "An empty request was sent");

            default:
                return new SiloError("bad-request", $"{request.GetType().Name} is not a request");
        }
    }
}

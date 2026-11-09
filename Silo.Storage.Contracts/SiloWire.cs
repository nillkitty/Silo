using System.Security.Cryptography;
using System.Text;
using Telefrag.Storage;

namespace Silo.Storage;

/// <summary>The wire format of a Silo's storage: which types travel, how they are written and read, and how clients and servers tell they agree.</summary>
public static class SiloWire
{
    /// <summary>What a server accepts from a client.</summary>
    private static readonly Type[] RequestTypes =
    [
        typeof(HelloRequest), typeof(StoredGetRequest), typeof(StoredListRequest), typeof(StoredSetRequest), typeof(StoredDeleteRequest),
        typeof(TableListRequest), typeof(TableGetRequest), typeof(TableSaveRequest), typeof(TableDeleteRequest),
        typeof(TableItem), typeof(StoredItem), typeof(StoredScope), typeof(StoredTier)
    ];

    /// <summary>What a client accepts from a server.</summary>
    private static readonly Type[] ReplyTypes =
    [
        typeof(SiloInfo), typeof(StoredItem), typeof(StoredListReply), typeof(TableItem), typeof(TableListReply), typeof(SiloError),
        typeof(StorageChange), typeof(StoredScope), typeof(StoredTier), typeof(StorageChangeKind)
    ];

    static SiloWire()
    {
        foreach (Type t in RequestTypes.Concat(ReplyTypes).Distinct())
            StorageManager.RegisterConstructors(t);
    }

    /// <summary>Limits for reading what a client sent: only requests, and nothing nested very deep or very big.</summary>
    public static StorageLimits RequestLimits { get; } = new StorageLimits { MaxDepth = 8 }.AllowOnly(RequestTypes);

    /// <summary>Limits for reading what a server sent.</summary>
    public static StorageLimits ReplyLimits { get; } = new StorageLimits { MaxDepth = 8 }.AllowOnly(ReplyTypes);

    /// <summary>
    ///     Identifies the wire messages this build understands: a hash of every constructor's declaration.  Client and server
    ///     compare them when they connect, so a mismatch is told up front and not found as an unknown constructor mid-call.
    /// </summary>
    public static string SchemaId { get; } = ComputeSchemaId();

    private static string ComputeSchemaId()
    {
        string[] declarations = RequestTypes.Concat(ReplyTypes)
                                            .Distinct()
                                            .SelectMany(StorageManager.GetCombinatorsForType)
                                            .Select(c => c.FullDeclaration)
                                            .Order(StringComparer.Ordinal)
                                            .ToArray();

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', declarations)));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    public static byte[] Write(object? message) => StorageManager.SerializeObject(message!, 256, ReplyLimits);

    public static object? ReadRequest(byte[] data) => StorageManager.DeserializeObject(data, 0, null, RequestLimits);

    public static object? ReadReply(byte[] data) => StorageManager.DeserializeObject(data, 0, null, ReplyLimits);

    public static byte[] WriteChange(StorageChange change) => Write(change);

    public static StorageChange ReadChange(byte[] data) =>
        ReadReply(data) as StorageChange ?? throw new InvalidDataException("A change was expected");
}

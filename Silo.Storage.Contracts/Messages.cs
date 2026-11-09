using Telefrag.Storage;

namespace Silo.Storage;

// What goes over the wire.  A request is a constructor like any other: the first four bytes name it, the fields follow.
// A reply is whatever the request's description says, or a SiloError.  Each request lists the reply it gets.

/// <summary>First call on a connection: the client's <see cref="SiloWire.SchemaId" />.  Reply: <see cref="SiloInfo" />.</summary>
[StorageName("silo.req.hello")]
[method: Storage]
public sealed record HelloRequest(string SchemaId);

/// <summary>Reply: <see cref="StoredItem" />, or null.</summary>
[StorageName("silo.req.stored.get")]
[method: Storage]
public sealed record StoredGetRequest(string Key, StoredScope Scope, StoredTier Tier);

/// <summary>Reply: <see cref="StoredListReply" />.</summary>
[StorageName("silo.req.stored.list")]
[method: Storage]
public sealed record StoredListRequest(string? KeyPrefix, StoredScope Scope, StoredTier Tier);

/// <summary>Reply: <see cref="StoredItem" />.</summary>
[StorageName("silo.req.stored.set")]
[method: Storage]
public sealed record StoredSetRequest(string Key, string TypeName, string Format, string Data, StoredScope Scope, StoredTier Tier);

/// <summary>Reply: a bool.</summary>
[StorageName("silo.req.stored.delete")]
[method: Storage]
public sealed record StoredDeleteRequest(string Key, StoredScope Scope, StoredTier Tier);

/// <summary>Reply: <see cref="TableListReply" />.</summary>
[StorageName("silo.req.table.list")]
[method: Storage]
public sealed record TableListRequest();

/// <summary>Reply: <see cref="TableItem" />, or null.</summary>
[StorageName("silo.req.table.get")]
[method: Storage]
public sealed record TableGetRequest(int Id);

/// <summary>Reply: <see cref="TableItem" />.</summary>
[StorageName("silo.req.table.save")]
[method: Storage]
public sealed record TableSaveRequest(TableItem Table);

/// <summary>Reply: a bool.</summary>
[StorageName("silo.req.table.delete")]
[method: Storage]
public sealed record TableDeleteRequest(int Id);

[StorageName("silo.reply.stored.list")]
[method: Storage]
public sealed record StoredListReply(List<StoredItem> Items);

[StorageName("silo.reply.table.list")]
[method: Storage]
public sealed record TableListReply(List<TableItem> Items);

/// <summary>The reply to a request that could not be served.</summary>
[StorageName("silo.reply.error")]
[method: Storage]
public sealed record SiloError(string Code, string Message);


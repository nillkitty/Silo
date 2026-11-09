using Telefrag.Storage;

namespace Silo.Storage;

/// <summary>Whose a stored item is.</summary>
[StorageName("silo.storedScope")]
public enum StoredScope
{
    /// <summary>The current person's own.</summary>
    Personal,

    /// <summary>The Silo's: everyone who opens the Silo sees it.</summary>
    Shared
}

/// <summary>Where in the Silo a stored item is kept.</summary>
[StorageName("silo.storedTier")]
public enum StoredTier
{
    /// <summary>Kept with the Silo.</summary>
    Permanent,

    /// <summary>Remembered for the session.</summary>
    Session
}

/// <summary>A key and the value written under it as text, with what is known about where it came from.</summary>
[StorageName("silo.stored")]
[method: Storage]
public sealed record StoredItem(
    string      Key,
    string      TypeName,
    string      Format,
    string      Data,
    DateTime    Created,
    DateTime    Modified,
    StoredScope Scope)
;

/// <summary>A hand-authored table: its name and the rows and columns as JSON.</summary>
[StorageName("silo.table")]
[method: Storage]
public sealed record TableItem(int Id, string Name, string Description, string DataJson, DateTime Created, DateTime Modified);

/// <summary>What changed in the Silo's storage, so a client can refresh what it shows.</summary>
public enum StorageChangeKind
{
    Added,
    Changed,
    Removed
}

/// <summary>One change: what kind, in which area ("stored", "tables"), and the key or id of the item.</summary>
[StorageName("silo.change")]
[method: Storage]
public sealed record StorageChange(StorageChangeKind Kind, string Area, string Key);

/// <summary>What a Silo says about itself when a client connects.</summary>
[StorageName("silo.info")]
[method: Storage]
public sealed record SiloInfo(string Name, string SchemaId, List<string> Capabilities);

/// <summary>Thrown by a remote storage when the Silo refused or failed a request.</summary>
public sealed class SiloStorageException(string code, string message) : Exception(message)
{
    /// <summary>A short machine-readable reason: bad-request, not-found, schema-mismatch, failed.</summary>
    public string Code { get; } = code;
}


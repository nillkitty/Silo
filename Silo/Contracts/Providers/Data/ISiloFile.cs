using System.IO;

namespace Silo.Providers.Data;

/// <summary>How a .silo file is laid out on disk.</summary>
public enum SiloStorageFormat
{
    /// <summary>
    ///     A single, bare SQLite database file. Every logical database (global/temp/universal/metaverse/worlds) is a set of
    ///     prefixed tables inside it. Cheap to open and save; the right size for most Silos.
    /// </summary>
    Simple,

    /// <summary>
    ///     A zip archive holding <c>z.db</c>, <c>x.db</c>, <c>u.db</c>, <c>y.db</c>, one <c>w-*.db</c> per World and
    ///     (optionally) an <c>assemblies/</c> folder. Needed for big Silos and for Silos that carry .NET assemblies.
    /// </summary>
    Archive
}

/// <summary>What the person (or policy) decided to do about a file that changed underneath an open Silo.</summary>
public enum SaveConflictChoice
{
    /// <summary>Don't save.</summary>
    Cancel,

    /// <summary>Replace the other version with this one.</summary>
    Overwrite,

    /// <summary>Keep the other version; save this one next to it as a "conflicted copy" and keep working on that.</summary>
    SaveCopy
}

/// <summary>What changed underneath the open Silo.</summary>
public enum SaveConflictKind
{
    /// <summary>The .silo file on disk was modified by someone else since this Silo loaded or last saved it.</summary>
    FileChangedOnDisk,

    /// <summary>The remote copy (HTTP) was modified by someone else since this Silo last synced.</summary>
    RemoteChanged
}

/// <summary>Describes a save conflict for <see cref="ISaveConflictResolver" />.</summary>
public sealed record SaveConflict(SaveConflictKind Kind, string Location, DateTime? TheirsUtc, DateTime? KnownUtc);

/// <summary>The resolver's answer; <see cref="Remember" /> is the "don't ask me again" box.</summary>
public sealed record SaveConflictAnswer(SaveConflictChoice Choice, bool Remember);

/// <summary>Asks the person how to resolve a <see cref="SaveConflict" />. Implemented by the UI layer.</summary>
public interface ISaveConflictResolver
{
    Task<SaveConflictAnswer> ResolveAsync(SaveConflict conflict);
}

/// <summary>
///     One memorialized save: what the Silo itself records in its <c>SaveLog</c> table and what is raised through
///     <see cref="ISiloFile.Saved" /> so the app can log/audit it. Identical for both storage formats, so the history of
///     a Silo reads the same however it is stored.
/// </summary>
/// <param name="SavedUtc">When the save happened. The saved file's last-write time is set to exactly this value.</param>
/// <param name="Sequence">The Silo's save counter after this save (<c>Header.Version</c>).</param>
/// <param name="Format">The storage format that was written.</param>
/// <param name="Path">Where it was written.</param>
/// <param name="Hash">SHA-256 (hex) of the bytes written. Recorded in the next <c>SaveLog</c> row as its previous hash.</param>
/// <param name="PreviousHash">SHA-256 (hex) of the file as this session last found/wrote it; null for the first save.</param>
/// <param name="SizeBytes">Size of the file written.</param>
/// <param name="User">Who saved.</param>
/// <param name="Machine">Where they saved from.</param>
/// <param name="RemoteUri">The remote location the Silo syncs with, if any.</param>
public sealed record SiloSaveEvent(
    DateTime          SavedUtc,
    int               Sequence,
    SiloStorageFormat Format,
    string            Path,
    string            Hash,
    string?           PreviousHash,
    long              SizeBytes,
    string            User,
    string            Machine,
    string?           RemoteUri);

/// <summary>
///     Implemented by a provider whose Silo lives in a file: which format, whether it must run in its own process, the
///     memorialized save events, conflict handling and the explode-to-archive conversion.
/// </summary>
public interface ISiloFile
{
    /// <summary>Asked how to proceed when the file changed on disk (or remotely) since it was loaded or last saved.</summary>
    ISaveConflictResolver? ConflictResolver { get; set; }

    /// <summary>
    ///     The path the Silo saves to (the original file; or, for a remote Silo, its local cache file). Changes after a
    ///     "Save as", or when a conflicting save was diverted to a conflicted copy.
    /// </summary>
    string FilePath { get; }

    /// <summary>The storage format currently in use.</summary>
    SiloStorageFormat Format { get; }

    /// <summary>
    ///     Whether the Silo must open in its own process (a "Solo Silo"). True for an archive that carries an
    ///     <c>assemblies/</c> folder; can also be set by the user to force it.
    /// </summary>
    bool IsSolo { get; set; }

    /// <summary>True when the last save reached the disk but the push to <see cref="Remote" /> failed.</summary>
    bool IsUnsynced { get; }

    /// <summary>The remote this Silo syncs with after each save, if it came from a URL.</summary>
    ISiloRemote? Remote { get; set; }

    /// <summary>Size in bytes of the working database(s) -- what the file will weigh once saved, roughly.</summary>
    long WorkingSizeBytes { get; }

    /// <summary>
    ///     Uploads the file as last saved to <see cref="Remote" /> again (after a failed upload). Throws
    ///     <see cref="RemoteSyncException" /> if it fails again.
    /// </summary>
    Task RetryUploadAsync();

    /// <summary>Raised after every successful save (also "Save as") with the memorialized event.</summary>
    event EventHandler<SiloSaveEvent>? Saved;

    /// <summary>
    ///     Converts a <see cref="SiloStorageFormat.Simple" /> Silo to <see cref="SiloStorageFormat.Archive" /> and saves it
    ///     (an explicit action; never done silently). Does nothing for an archive.
    /// </summary>
    Task ExplodeToArchiveAsync();
}

/// <summary>A location a Silo is synced with: pulled when opened, pushed when saved.</summary>
public interface ISiloRemote
{
    /// <summary>
    ///     True while the local file holds saved changes that were not uploaded yet. Persisted by the remote itself, so a
    ///     crash or being offline doesn't lose track of it.
    /// </summary>
    bool HasUnpushedChanges { get; set; }

    /// <summary>A short name for the kind of remote ("HTTP", "Git").</summary>
    string Kind { get; }

    /// <summary>Where the Silo lives, e.g. an https URL.</summary>
    Uri Uri { get; }

    /// <summary>
    ///     Uploads the saved file. Throws <see cref="RemoteConflictException" /> when the remote changed since the last
    ///     sync and <paramref name="force" /> is false.
    /// </summary>
    Task PushAsync(string localFile, bool force, CancellationToken ct = default);
}

/// <summary>The remote copy changed since this Silo last synced with it.</summary>
public sealed class RemoteConflictException(string message) : IOException(message);

/// <summary>The local save succeeded but uploading it to the remote did not.</summary>
public sealed class RemoteSyncException(string message, Exception inner) : IOException(message, inner);
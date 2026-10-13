using System.Runtime.CompilerServices;

namespace Silo.Contracts;

/// <summary>
///     A <see cref="IDataSource" /> that can be watched: instead of being asked again and again, it tells the caller when
///     items appear, change, move or go away -- a directory that gains files, an RSS feed with a new entry, a table that
///     gets rows. This is what a Batch Processing template listens to before it runs its actions on each new item.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="WatchAsync" /> returns a long-lived sequence of <see cref="DataChangeBatch" />es. Each call starts
///         an independent watch; the sequence ends when it is cancelled or when the source goes away, and throws if the
///         source fails. Changes arrive in batches, already coalesced (a file written in ten chunks is one
///         <see cref="DataChangeKind.Changed" />), so the consumer decides what a batch is worth.
///     </para>
///     <para>
///         Delivery is at-least-once and resumable. Each batch can carry a <see cref="DataChangeBatch.Cursor" />, an opaque
///         bookmark meaning "everything up to here has been reported". A consumer that has finished a batch stores its
///         cursor and, after a restart, watches again with <see cref="WatchStart.Cursor" />; this is the source side of a
///         delta import. A source that cannot honour the cursor (it is too old, or the source keeps no history) says so
///         with a <see cref="DataChangeKind.Reset" /> change, and the consumer re-reads everything -- exactly an
///         <c>ImportType.Delta</c> that had to become <c>ImportType.Full</c>.
///     </para>
///     <para>
///         Keys identify items the way <see cref="IListItem" /> rows or file paths do: stable across changes, unique
///         within the source, compared ordinally unless the source says otherwise. A source that is itself a table,
///         list or directory reports one change per row, entry or file; the item is whatever that source's items are.
///     </para>
/// </remarks>
public interface IObservableDataSource : IDataSource
{
    /// <summary>The kinds of change this source can report (a polling source that cannot see renames omits <see cref="DataChangeKind.Renamed" />).</summary>
    DataChangeKind ObservableChanges { get; }

    /// <summary>How the source observes itself; see <see cref="ObservationFlags" />.</summary>
    ObservationFlags Observation { get; }

    /// <summary>
    ///     Starts watching. Nothing is observed until the sequence is enumerated, and observation stops when the
    ///     enumeration is disposed or <paramref name="cancel" /> fires.
    /// </summary>
    /// <param name="options">What to report and where to start; null means "changes from now on, as the source sees fit".</param>
    /// <param name="cancel">Stops the watch.</param>
    IAsyncEnumerable<DataChangeBatch> WatchAsync(WatchOptions? options = null, CancellationToken cancel = default);
}

/// <summary>What happened to an item of an <see cref="IObservableDataSource" />. Combined, the values describe which kinds a source reports or a watcher wants.</summary>
[Flags]
public enum DataChangeKind
{
    None = 0,

    /// <summary>A new item appeared (or, in a snapshot, was already there).</summary>
    Added = 1,

    /// <summary>An existing item's content changed.</summary>
    Changed = 2,

    /// <summary>The item is gone, or no longer in scope.</summary>
    Removed = 4,

    /// <summary>The item kept its identity but its key changed (a file was renamed or moved).</summary>
    Renamed = 8,

    /// <summary>
    ///     The source cannot describe what happened since the consumer last looked: forget what is known and read
    ///     everything again. Always delivered, whatever kinds were asked for.
    /// </summary>
    Reset = 16,

    All = Added | Changed | Removed | Renamed | Reset
}

/// <summary>How an <see cref="IObservableDataSource" /> observes itself.</summary>
[Flags]
public enum ObservationFlags
{
    None = 0,

    /// <summary>The source is told of changes (file system events, a subscription). Without it the source polls, at <see cref="WatchOptions.PollInterval" />.</summary>
    Push = 1,

    /// <summary>The source honours <see cref="WatchStart.Cursor" />, so a watch can resume where an earlier one stopped.</summary>
    Resumable = 2,

    /// <summary>The source can report what is already there (<see cref="WatchStart.Snapshot" />).</summary>
    Snapshot = 4,

    /// <summary>Changes are reported in the order they happened; otherwise only the order within a batch is arbitrary.</summary>
    Ordered = 8,

    /// <summary>A <see cref="DataChangeKind.Changed" /> lists the fields that changed (see <see cref="DataChange.ChangedFields" />).</summary>
    FieldLevel = 16
}

/// <summary>Where a watch begins.</summary>
public enum WatchStart
{
    /// <summary>Only changes after the watch started. The default: "tell me about new items".</summary>
    Now,

    /// <summary>First report everything already there as <see cref="DataChangeKind.Added" /> (in batches marked <see cref="DataChangeBatch.IsSnapshot" />), then carry on live.</summary>
    Snapshot,

    /// <summary>Report what happened after <see cref="WatchOptions.Cursor" />, then carry on live.</summary>
    Cursor
}

/// <summary>What a watch should report and how. Every property is optional; a source uses its own defaults for what is not set.</summary>
public sealed record WatchOptions
{
    /// <summary>Where to begin.</summary>
    public WatchStart Start { get; init; } = WatchStart.Now;

    /// <summary>The bookmark to resume from; required with <see cref="WatchStart.Cursor" />.</summary>
    public string? Cursor { get; init; }

    /// <summary>The kinds of change to report. <see cref="DataChangeKind.Reset" /> is always reported.</summary>
    public DataChangeKind Kinds { get; init; } = DataChangeKind.All;

    /// <summary>How often a polling source (one without <see cref="ObservationFlags.Push" />) looks. Ignored by sources that are told.</summary>
    public TimeSpan? PollInterval { get; init; }

    /// <summary>How long to gather changes before reporting them, so a burst becomes one batch.</summary>
    public TimeSpan? CoalesceWindow { get; init; }

    /// <summary>The most changes in one batch; a larger burst is split.</summary>
    public int? MaxBatchSize { get; init; }
}

/// <summary>One thing that happened to one item.</summary>
/// <param name="Kind">What happened; always a single kind, never a combination.</param>
/// <param name="Key">The item's key; for <see cref="DataChangeKind.Renamed" />, the new key; empty for <see cref="DataChangeKind.Reset" />.</param>
/// <param name="Item">
///     The item as it is now, in whatever form the source's items take (a file entry, a row, a feed entry, or an
///     <c>IValue</c> when the source can say more); for <see cref="DataChangeKind.Removed" />, the last known item, or
///     null if the source no longer has it. Null when the source reports only that something happened.
/// </param>
/// <param name="PreviousKey">For <see cref="DataChangeKind.Renamed" />, the key the item had before.</param>
/// <param name="ChangedFields">For <see cref="DataChangeKind.Changed" /> from a source with <see cref="ObservationFlags.FieldLevel" />, the names of the fields that changed.</param>
/// <param name="Time">When it happened, in UTC, if the source knows (not when it was noticed).</param>
public sealed record DataChange(
    DataChangeKind                Kind,
    string                        Key,
    object?                       Item          = null,
    string?                       PreviousKey   = null,
    IReadOnlyCollection<string>?  ChangedFields = null,
    DateTime?                     Time          = null)
{
    public static DataChange Added(string key, object? item = null, DateTime? time = null)
    {
        return new DataChange(DataChangeKind.Added, key, item, Time: time);
    }

    public static DataChange Changed(string key, object? item = null, IReadOnlyCollection<string>? changedFields = null, DateTime? time = null)
    {
        return new DataChange(DataChangeKind.Changed, key, item, ChangedFields: changedFields, Time: time);
    }

    public static DataChange Removed(string key, object? lastItem = null, DateTime? time = null)
    {
        return new DataChange(DataChangeKind.Removed, key, lastItem, Time: time);
    }

    public static DataChange Renamed(string previousKey, string key, object? item = null, DateTime? time = null)
    {
        return new DataChange(DataChangeKind.Renamed, key, item, previousKey, Time: time);
    }

    /// <summary>The source cannot give a delta: see <see cref="DataChangeKind.Reset" />.</summary>
    public static DataChange Reset(DateTime? time = null)
    {
        return new DataChange(DataChangeKind.Reset, string.Empty, Time: time);
    }
}

/// <summary>
///     Changes reported together. A consumer treats a batch as a unit: it finishes it, then records
///     <see cref="Cursor" /> as the point to resume from.
/// </summary>
/// <param name="Changes">The changes, in the order the source reports them.</param>
/// <param name="Cursor">The bookmark that means "everything up to and including this batch has been reported"; null if the source cannot resume.</param>
/// <param name="IsSnapshot">True for the batches that report what was already there when a <see cref="WatchStart.Snapshot" /> watch began.</param>
public sealed record DataChangeBatch(IReadOnlyList<DataChange> Changes, string? Cursor = null, bool IsSnapshot = false)
{
    /// <summary>When the batch was put together, in UTC.</summary>
    public DateTime Time { get; init; } = DateTime.UtcNow;
}

public static class ObservableDataSourceExtensions
{
    /// <summary>Whether the source reports every kind in <paramref name="kinds" />.</summary>
    public static bool Supports(this IObservableDataSource source, DataChangeKind kinds)
    {
        return (source.ObservableChanges & kinds) == kinds;
    }

    /// <summary>Turns a sequence of batches into a sequence of the changes in them, for a consumer that does not checkpoint.</summary>
    public static async IAsyncEnumerable<DataChange> Flatten(this IAsyncEnumerable<DataChangeBatch> batches,
                                                             [EnumeratorCancellation] CancellationToken cancel = default)
    {
        await foreach (var batch in batches.WithCancellation(cancel).ConfigureAwait(false))
        foreach (var change in batch.Changes)
            yield return change;
    }
}

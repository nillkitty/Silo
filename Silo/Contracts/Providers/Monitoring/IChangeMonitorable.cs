namespace Silo.Providers.Monitoring;

/// <summary>How a model finds out that something under it changed.</summary>
[Flags]
public enum MonitorModes
{
    /// <summary>Cannot be monitored.</summary>
    None = 0,

    /// <summary>
    ///     Passive: the system tells us (a file system watcher, a registry change notification). Cheap while nothing changes,
    ///     but may miss changes (buffer overflows, network shares).
    /// </summary>
    Passive = 1,

    /// <summary>Active: we look, on an interval, and compare with what was seen last time. Works anywhere; costs a read per interval.</summary>
    Active = 2
}

/// <summary>What happened to an item.</summary>
public enum NodeChangeKind
{
    /// <summary>A new item appeared.</summary>
    Created,

    /// <summary>An item's content or settings changed.</summary>
    Changed,

    /// <summary>An item went away.</summary>
    Deleted,

    /// <summary>An item has a new name (<see cref="NodeChange.OldName" /> says the old one).</summary>
    Renamed,

    /// <summary>Too much changed to say what: look at the whole container again.</summary>
    Reset
}

/// <summary>One change found by a monitor.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Name">The item: a full file path, or a registry key's full name (a value adds a "?" and the value's name, as in its URI).</param>
/// <param name="Uri">The item's URI, when it has one.</param>
/// <param name="OldName">For a rename, the item's previous <paramref name="Name" />.</param>
public sealed record NodeChange(NodeChangeKind Kind, string Name, Uri? Uri = null, string? OldName = null);

/// <summary>
///     A connection or node model that can say when what is under it changes (so the Explorer can refresh, and
///     auto-add rules can pick up new and changed items). Implementers support
///     <see cref="MonitorModes.Passive" />, <see cref="MonitorModes.Active" />, or both.
/// </summary>
public interface IChangeMonitorable
{
    /// <summary>The ways this model can be monitored; <see cref="MonitorModes.None" /> when it cannot (it is not a container, say).</summary>
    MonitorModes SupportedModes { get; }

    /// <summary>
    ///     Starts watching. <paramref name="onChange" /> is called on a background thread for each change; marshal to the UI
    ///     thread if needed.
    /// </summary>
    /// <param name="mode">One mode (not both). When it is not supported, the call returns null; ask <see cref="SupportedModes" /> first.</param>
    /// <param name="onChange">Called with each change.</param>
    /// <param name="recursive">Also watch everything below (passive mode only; an active look reads one level).</param>
    /// <param name="interval">For <see cref="MonitorModes.Active" />, how often to look (default 5 seconds).</param>
    /// <returns>Dispose it to stop; null when the mode is not supported.</returns>
    IDisposable? StartMonitoring(MonitorModes mode, global::System.Action<NodeChange> onChange, bool recursive = false, TimeSpan? interval = null);
}

/// <summary>Helpers for implementing and using <see cref="IChangeMonitorable" />.</summary>
public static class ChangeMonitoring
{
    /// <summary>The default time between looks in active mode.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    /// <summary>The best mode the model supports (passive preferred), or <see cref="MonitorModes.None" />.</summary>
    public static MonitorModes Best(this IChangeMonitorable model)
    {
        var modes = model.SupportedModes;
        return modes.HasFlag(MonitorModes.Passive) ? MonitorModes.Passive :
               modes.HasFlag(MonitorModes.Active)  ? MonitorModes.Active : MonitorModes.None;
    }

    /// <summary>What changed between two looks. A snapshot maps each item's name to a signature (anything that differs when it changed).</summary>
    public static IEnumerable<NodeChange> Compare(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after,
                                                  Func<string, Uri?>                  uriOf)
    {
        foreach (var (name, signature) in after)
        {
            if (!before.TryGetValue(name, out string? old)) yield return new NodeChange(NodeChangeKind.Created, name, uriOf(name));
            else if (old != signature) yield return new NodeChange(NodeChangeKind.Changed, name, uriOf(name));
        }

        foreach (string name in before.Keys)
            if (!after.ContainsKey(name))
                yield return new NodeChange(NodeChangeKind.Deleted, name, uriOf(name));
    }

    /// <summary>
    ///     Looks at <paramref name="snapshot" /> on a timer and reports the differences. The first look is the baseline (nothing is reported
    ///     for it). A look that throws is skipped, and the baseline stays.
    /// </summary>
    public static IDisposable Poll(Func<IReadOnlyDictionary<string, string>> snapshot, Func<string, Uri?> uriOf, global::System.Action<NodeChange> onChange,
                                   TimeSpan?                                 interval)
    {
        return new Differ(snapshot, uriOf, onChange, interval ?? DefaultInterval);
    }

    /// <summary>
    ///     Reports the differences each time <paramref name="poke" /> is called (for passive systems that say "something changed" but not
    ///     what). Dispose it to stop.
    /// </summary>
    public static IDisposable Diff(Func<IReadOnlyDictionary<string, string>> snapshot, Func<string, Uri?> uriOf, global::System.Action<NodeChange> onChange,
                                   out global::System.Action poke)
    {
        var differ = new Differ(snapshot, uriOf, onChange, Timeout.InfiniteTimeSpan);
        poke = differ.Look;
        return differ;
    }

    private sealed class Differ : IDisposable
    {
        private readonly global::System.Action<NodeChange>                        _onChange;
        private readonly Func<IReadOnlyDictionary<string, string>> _snapshot;
        private readonly Timer                                     _timer;
        private readonly Func<string, Uri?>                        _uriOf;
        private          IReadOnlyDictionary<string, string>?      _last;
        private          bool                                      _stopped;

        public Differ(Func<IReadOnlyDictionary<string, string>> snapshot, Func<string, Uri?> uriOf, global::System.Action<NodeChange> onChange, TimeSpan interval)
        {
            _snapshot = snapshot;
            _uriOf    = uriOf;
            _onChange = onChange;
            Look(); // the baseline
            _timer = new Timer(_ => Look(), null, interval, interval);
        }

        public void Dispose()
        {
            lock (this)
            {
                _stopped = true;
            }

            _timer.Dispose();
        }

        public void Look()
        {
            lock (this) // one look at a time: a slow one makes the next wait rather than overlap
            {
                if (_stopped) return;

                IReadOnlyDictionary<string, string> now;
                try
                {
                    now = _snapshot();
                }
                catch (Exception)
                {
                    return;
                }

                var before = _last;
                _last = now;
                if (before is null) return;

                foreach (var change in Compare(before, now, _uriOf))
                {
                    try
                    {
                        _onChange(change);
                    }
                    catch (Exception)
                    {
                        // a listener's fault must not stop the monitor
                    }
                }
            }
        }
    }
}

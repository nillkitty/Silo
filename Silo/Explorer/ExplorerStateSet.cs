using System.Linq;

namespace Silo.Explorer;

/// <summary>
///     The Explorer state a Silo remembered that has not met its node yet. A node can appear long after the tree was first built -- a
///     connection that is still reconnecting, the children of a folder that is read when it is expanded -- so the remembered state is
///     kept here until the node with that key shows up, takes its state (<see cref="Take" />) and so becomes ordinary live state. What is
///     still waiting is written back with the live state (<see cref="Merge" />), so saving early never forgets a node that is not back yet.
/// </summary>
public sealed class ExplorerStateSet
{
    private readonly Dictionary<string, NodeStateRecord> _waiting = new(StringComparer.Ordinal);

    /// <summary>How many remembered nodes are waiting for their node.</summary>
    public int Count => _waiting.Count;

    public bool IsWaiting(string key) => _waiting.ContainsKey(key);

    /// <summary>Starts over with what the Silo remembered (the last entry for a repeated key wins).</summary>
    public void Load(IEnumerable<NodeStateRecord> remembered)
    {
        _waiting.Clear();
        foreach (var record in remembered)
            _waiting[record.Key] = record;
    }

    /// <summary>Forgets everything that is waiting.</summary>
    public void Clear() => _waiting.Clear();

    /// <summary>The remembered state of the node with this key, which is no longer waiting once it has been handed over; null if there is none.</summary>
    public NodeStateRecord? Take(string key)
    {
        return _waiting.Remove(key, out var record) ? record : null;
    }

    /// <summary>
    ///     Gives up on the waiting nodes whose keys do not start with one of <paramref name="keepPrefixes" /> -- the nodes that never came
    ///     back (a deleted table, a folder that is gone). The prefixes protect the nodes under a connection that has not come back yet.
    /// </summary>
    /// <returns>How many were dropped.</returns>
    public int DropUnmatched(IReadOnlyCollection<string> keepPrefixes)
    {
        var drop = _waiting.Keys.Where(k => !keepPrefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal))).ToList();
        foreach (var key in drop) _waiting.Remove(key);
        return drop.Count;
    }

    /// <summary>The live state, then every waiting record the live state does not replace.</summary>
    public IReadOnlyCollection<NodeStateRecord> Merge(IEnumerable<NodeStateRecord> live)
    {
        var result = new List<NodeStateRecord>();
        var seen   = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in live)
            if (seen.Add(record.Key))
                result.Add(record);

        result.AddRange(_waiting.Values.Where(w => !seen.Contains(w.Key)));
        return result;
    }
}

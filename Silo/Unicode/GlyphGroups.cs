namespace Silo.Unicode;

/// <summary>A stretch of neighbouring items that are in the same group (<see cref="Name" /> null: in none).</summary>
public sealed record GroupRun(string? Name, int Start, int Count);

/// <summary>
///     The rules for named groups of characters, in the collection and on a palette page. A list stays flat: each item says which group it is in,
///     and a group is shown wherever its items are (a run of neighbours with the same group gets one header). So a drag, a sort or a delete never has to
///     keep a second structure in step; the rules here only decide which group an item takes and where it goes when it is put in one.
/// </summary>
public static class GlyphGroups
{
    /// <summary>The runs of the list, in order: each is the longest stretch of neighbours with the same group.</summary>
    public static List<GroupRun> Runs(int count, Func<int, string?> groupOf)
    {
        var runs = new List<GroupRun>();
        for (int i = 0; i < count; i++)
        {
            string? name = Clean(groupOf(i));
            if (runs.Count > 0 && string.Equals(runs[^1].Name, name, StringComparison.Ordinal))
                runs[^1] = runs[^1] with { Count = runs[^1].Count + 1 };
            else
                runs.Add(new GroupRun(name, i, 1));
        }

        return runs;
    }

    /// <summary>Whether any item is in a group (when none is, no headers are shown).</summary>
    public static bool Any(int count, Func<int, string?> groupOf)
    {
        for (int i = 0; i < count; i++)
            if (Clean(groupOf(i)) is not null)
                return true;

        return false;
    }

    /// <summary>The distinct group names, in the order they first appear.</summary>
    public static List<string> Names(int count, Func<int, string?> groupOf)
    {
        var names = new List<string>();
        for (int i = 0; i < count; i++)
            if (Clean(groupOf(i)) is { } name && !names.Contains(name, StringComparer.Ordinal))
                names.Add(name);

        return names;
    }

    /// <summary>A group name as kept: trimmed, null when empty.</summary>
    public static string? Clean(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    /// <summary>
    ///     Puts the items at <paramref name="indexes" /> in <paramref name="group" /> (null: out of any group, where they are). They are moved together,
    ///     in their order, to after the last item already in the group, or to the end when the group is new. Returns the index the first of them ends up
    ///     at (the first index given when they are only taken out of a group; -1 when none was in range).
    /// </summary>
    public static int Assign<T>(List<T> list, IEnumerable<int> indexes, string? group, Func<T, string?> groupOf, Func<T, string?, T> withGroup)
    {
        group = Clean(group);
        var picked = indexes.Where(i => i >= 0 && i < list.Count).Distinct().OrderBy(i => i).ToList();
        if (picked.Count == 0) return -1;

        if (group is null)
        {
            foreach (int i in picked) list[i] = withGroup(list[i], null);
            return picked[0];
        }

        var items = picked.Select(i => withGroup(list[i], group)).ToList();
        foreach (int i in picked.OrderByDescending(i => i)) list.RemoveAt(i);

        int at = list.Count;
        for (int i = list.Count - 1; i >= 0; i--)
            if (string.Equals(Clean(groupOf(list[i])), group, StringComparison.Ordinal))
            {
                at = i + 1;
                break;
            }

        list.InsertRange(at, items);
        return at;
    }

    /// <summary>Renames a group: every item in <paramref name="from" /> is put in <paramref name="to" /> (a name of another group joins the two; null or empty takes them out of any group).</summary>
    public static void Rename<T>(List<T> list, string from, string? to, Func<T, string?> groupOf, Func<T, string?, T> withGroup)
    {
        to = Clean(to);
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(Clean(groupOf(list[i])), from, StringComparison.Ordinal))
                list[i] = withGroup(list[i], to);
    }

    /// <summary>
    ///     The order that keeps each group's items together when the list is sorted: groups stay in the order they first appear, and the items inside
    ///     each are ordered by <paramref name="key" />.
    /// </summary>
    public static List<T> SortWithin<T, TKey>(IReadOnlyList<T> list, Func<T, string?> groupOf, Func<T, TKey> key)
    {
        var first = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < list.Count; i++) first.TryAdd(Clean(groupOf(list[i])) ?? string.Empty, i);

        return list.OrderBy(t => first[Clean(groupOf(t)) ?? string.Empty]).ThenBy(key).ToList();
    }
}

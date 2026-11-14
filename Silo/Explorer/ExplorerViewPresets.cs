namespace Silo.Explorer;

/// <summary>
///     One way the Explorer can show a Silo, chosen from the drop-down in its header. A view does not read the Silo itself: it
///     arranges the nodes <see cref="ExplorerTree.Build" /> made (the grouped tree) into the roots it shows, so every view has
///     the same nodes -- the same keys, commands, details and remembered state -- only placed differently.
/// </summary>
public abstract class ExplorerViewPreset
{
    /// <summary>Identifies the view in the settings; stable across runs.</summary>
    public abstract string Id { get; }

    /// <summary>What the drop-down calls it.</summary>
    public abstract string Name { get; }

    /// <summary>What it shows, for the drop-down's tooltip.</summary>
    public abstract string Description { get; }

    /// <summary>The roots to show, made from the grouped tree (which the view may take apart: it is built afresh for each refresh).</summary>
    public abstract IReadOnlyList<ExplorerNode> Arrange(IReadOnlyList<ExplorerNode> grouped);

    public override string ToString() => Name;
}

/// <summary>The Explorer's views, in the order the drop-down lists them.</summary>
public static class ExplorerViewPresets
{
    public static ExplorerViewPreset Grouped { get; } = new GroupedView();

    public static ExplorerViewPreset Flat { get; } = new FlatView(false);

    public static ExplorerViewPreset FlatByType { get; } = new FlatView(true);

    public static IReadOnlyList<ExplorerViewPreset> All { get; } = [Grouped, Flat, FlatByType];

    /// <summary>The view with the id; <see cref="Grouped" /> for an unknown or missing one.</summary>
    public static ExplorerViewPreset Find(string? id) => All.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Grouped;
}

/// <summary>The categorized tree: the Silo with what it keeps in folders by kind, then its connections, then what tools contribute.</summary>
public sealed class GroupedView : ExplorerViewPreset
{
    public override string Id          => "grouped";
    public override string Name        => "Grouped";
    public override string Description => "Everything in the Silo, in folders by kind, then its connections";

    public override IReadOnlyList<ExplorerNode> Arrange(IReadOnlyList<ExplorerNode> grouped) => grouped;
}

/// <summary>
///     The Silo's node holds every named item it keeps in one list by name, whatever its kind: tables, diagrams, bindings,
///     enums, syntaxes, saved objects and the rest. The folders and groups that hold them are left out; an item keeps what is
///     in it (an enum still opens to its values). Only the Silo is flattened: its connections and what tools contribute stay
///     as they are in the grouped tree. Each item shows its kind in gray after its name; the list is sorted by name, or by
///     kind then name.
/// </summary>
public sealed class FlatView(bool byType) : ExplorerViewPreset
{
    /// <summary>Shorter words for kinds whose name is an identifier.</summary>
    private static readonly Dictionary<string, string> ShortKinds = new(StringComparer.OrdinalIgnoreCase) { ["DataBinding"] = "Binding" };

    /// <summary>Kinds that are items in their own right; what is below them are parts of them, not items of the Silo.</summary>
    private static readonly HashSet<string> ItemKinds = new(StringComparer.OrdinalIgnoreCase)
                                                        {
                                                            "Connection", "Table", "Diagram", "DataBinding", "Enum", "Syntax", "Document"
                                                        };

    /// <summary>Kinds that only hold or stand for other things: never items themselves.</summary>
    private static readonly HashSet<string> HolderKinds = new(StringComparer.OrdinalIgnoreCase) { "Folder", "World", "Silo", "Placeholder" };

    public override string Id          => byType ? "flat-by-type" : "flat";
    public override string Name        => byType ? "Flat by type" : "Flat by name";
    public override string Description => byType
                                              ? "Everything the Silo keeps in one list by kind, then its connections"
                                              : "Everything the Silo keeps in one list by name, then its connections";

    /// <summary>The word shown after an item's name: its kind, shortened where the kind is an identifier.</summary>
    public static string ShortKind(string kind) => ShortKinds.GetValueOrDefault(kind, kind);

    public override IReadOnlyList<ExplorerNode> Arrange(IReadOnlyList<ExplorerNode> grouped)
    {
        foreach (var silo in grouped.Where(r => string.Equals(r.Kind, "Silo", StringComparison.OrdinalIgnoreCase)))
        {
            var items = new List<ExplorerNode>();
            var seen  = new HashSet<string>(StringComparer.Ordinal);
            foreach (var child in silo.Children)
                Collect(child, items, seen);

            foreach (var item in items) item.KindNote = ShortKind(item.Kind);

            var sorted = byType
                             ? items.OrderBy(i => i.KindNote, StringComparer.CurrentCultureIgnoreCase).ThenBy(i => i.Display, StringComparer.CurrentCultureIgnoreCase)
                             : items.OrderBy(i => i.Display, StringComparer.CurrentCultureIgnoreCase).ThenBy(i => i.KindNote, StringComparer.CurrentCultureIgnoreCase);

            silo.Children.Clear();
            foreach (var item in sorted)
                silo.Children.Add(item);
            silo.LinkParents();
        }

        return grouped;
    }

    /// <summary>Adds the items at or below <paramref name="node" />.</summary>
    private static void Collect(ExplorerNode node, List<ExplorerNode> items, HashSet<string> seen)
    {
        if (string.Equals(node.Kind, "Tool", StringComparison.OrdinalIgnoreCase)) return; // a tool belongs to the app, not to the Silo
        if (string.Equals(node.Kind, "Placeholder", StringComparison.OrdinalIgnoreCase)) return;

        bool holder = HolderKinds.Contains(node.Kind);

        // an item in its own right; or one whose contents are only read when it is opened (nothing to see past it yet); or a leaf
        if (!holder && (ItemKinds.Contains(node.Kind) || node.LoadChildren is not null || node.Children.Count == 0))
        {
            if (seen.Add(node.Key)) items.Add(node);
            return;
        }

        foreach (var child in node.Children)
            Collect(child, items, seen);
    }
}

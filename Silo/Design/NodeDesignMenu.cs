using System.Windows;
using Silo.Ui.Windows;

namespace Silo.Design;

/// <summary>
///     A tree node (of the navigation flyout or the Explorer) that can be customised in author mode and shows its own
///     design menu. The node keeps no state of its own for this: label, icon size and visibility live in
///     <see cref="NodeOverrides" />, keyed by <see cref="OverrideKey" />.
/// </summary>
public interface IOverridableNode : IDesignMenuSource
{
    /// <summary>The key in <see cref="NodeOverrides" />, starting with <see cref="NodeOverrides.NavPrefix" /> or <see cref="NodeOverrides.ExplorerPrefix" />.</summary>
    string OverrideKey { get; }

    /// <summary>The node's own text, before any label override.</summary>
    string OriginalName { get; }

    /// <summary>What kind of node it is, in words.</summary>
    string KindText { get; }

    /// <summary>Where it sits: the names of its ancestors and itself.</summary>
    string PathText { get; }

    /// <summary>The icon size the node has without an override.</summary>
    double DefaultIconSize { get; }

    string? LabelOverride { get; set; }

    double? IconSizeOverride { get; set; }

    bool IsHidden { get; set; }

    /// <summary>The "open" items of the node (open, open pinned, and whatever else it offers).</summary>
    IEnumerable<DesignMenuItem> OpenMenuItems();
}

/// <summary>
///     The author-mode menu every tree node gets: what it is (type, name, key), copy its key or path, its own label and
///     icon size, how to open it, and hide it. It is shown instead of the tree's and window's menus.
/// </summary>
public static class NodeDesignMenu
{
    public static IEnumerable<DesignMenuItem> Build(IOverridableNode node)
    {
        var items = new List<DesignMenuItem>
                    {
                        DesignMenuItem.Info("Name: " + node.OriginalName),
                        DesignMenuItem.Info("Kind: " + node.KindText),
                        DesignMenuItem.Info("Key: " + node.OverrideKey),
                        DesignMenuItem.Separator(),
                        DesignMenuItem.Run("Copy key", () => CopyText(node.OverrideKey)),
                        DesignMenuItem.Run("Copy path", () => CopyText(node.PathText)),
                        DesignMenuItem.Separator(),
                        DesignMenuItem.Slider("Icon size (this node)", DesignLevel.Author, 8, 64,
                                              () => node.IconSizeOverride ?? node.DefaultIconSize,
                                              v => node.IconSizeOverride = Math.Round(v), "{0:0} px"),
                        DesignMenuItem.Run("Reset icon size", () => node.IconSizeOverride = null, enabled: node.IconSizeOverride is not null),
                        DesignMenuItem.Run("Rename label...", () => Rename(node)),
                        DesignMenuItem.Run("Reset label", () => node.LabelOverride = null, enabled: node.LabelOverride is not null)
                    };

        var open = node.OpenMenuItems().ToList();
        if (open.Count > 0)
        {
            items.Add(DesignMenuItem.Separator());
            items.AddRange(open);
        }

        string prefix = node.OverrideKey.StartsWith(NodeOverrides.ExplorerPrefix, StringComparison.Ordinal)
                            ? NodeOverrides.ExplorerPrefix
                            : NodeOverrides.NavPrefix;
        int hidden = NodeOverrides.HiddenCount(prefix);
        items.Add(DesignMenuItem.Separator());
        items.Add(DesignMenuItem.Run("Hide this node", () => node.IsHidden = true));
        items.Add(DesignMenuItem.Run($"Show hidden nodes ({hidden})", () => NodeOverrides.ShowHidden(prefix), enabled: hidden > 0));
        return items;
    }

    private static void Rename(IOverridableNode node)
    {
        string? answer = PromptDialog.Ask("Rename node", "The text shown for this node (\"Reset label\" in the menu goes back to the original):",
                                          "OK", node.LabelOverride ?? node.OriginalName);
        if (answer is null) return;

        string text = answer.Trim();
        node.LabelOverride = text == node.OriginalName ? null : text;
    }

    private static void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
            App.Status("Copied " + text);
        }
        catch (Exception ex) // the clipboard can be locked by another program for a moment
        {
            App.Status("Could not copy: " + ex.Message);
        }
    }
}

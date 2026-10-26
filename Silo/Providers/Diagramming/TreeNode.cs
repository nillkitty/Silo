using System.ComponentModel;

namespace Silo.Providers.Diagramming;

/// <summary>
///     The root of a tree of vertices grown from a tree-like source, joined by connectors and laid out as a tree. Each
///     descendant is a copy of the node itself (its style and binding templates) with its item in scope; the node's
///     <c>silo-item-label</c>, <c>silo-item-tooltip</c>, <c>silo-item-style.*</c> and <c>silo-item-attr.*</c> attributes, when
///     present, are what the copies get instead. Levels below <see cref="ExpandDepth" /> are read when the person expands
///     them (+/-), never below <see cref="MaxDepth" />.
/// </summary>
[Description("A shape which will maintain a tree of descendant tree nodes based on a compatible data source")]
public class TreeNode : DynamicNode
{
    [NodeAttribute("silo-expand-depth")]
    [DefaultValue(1)]
    [Description("How many levels are read and shown expanded at first; deeper levels load when expanded")]
    public int ExpandDepth { get; set; } = 1;

    [NodeAttribute("silo-max-depth")]
    [DefaultValue(8)]
    [Description("The deepest level ever shown")]
    public int MaxDepth { get; set; } = 8;

    [NodeAttribute("silo-max-children")]
    [DefaultValue(50)]
    [Description("The most children shown under one item; a '… N more' item says how many were left out")]
    public int MaxChildren { get; set; } = 50;

    [NodeAttribute("silo-direction")]
    [DefaultValue(TreeDirection.Down)]
    [Description("Which way the tree grows from the node")]
    public TreeDirection Direction { get; set; } = TreeDirection.Down;

    [NodeAttribute("silo-level-gap")]
    [DefaultValue(60.0)]
    [Description("Space between levels")]
    public double LevelGap { get; set; } = 60;

    [NodeAttribute("silo-edge-style")]
    [Description("The style of the connectors between items (empty: elbow connectors without arrows)")]
    public string? EdgeStyle { get; set; }

    [NodeAttribute("silo-sibling-gap")]
    [DefaultValue(20.0)]
    [Description("Space between siblings")]
    public double SiblingGap { get; set; } = 20;
}

/// <summary>Which way a <see cref="TreeNode" /> grows.</summary>
public enum TreeDirection
{
    Down,
    Right
}

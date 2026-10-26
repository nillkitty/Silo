using System.ComponentModel;

namespace Silo.Providers.Diagramming;

/// <summary>
///     The settings every data-bound diagram node has. A node is an ordinary draw.io cell whose custom attributes (Edit
///     Data) carry these settings; each property names its attribute with <see cref="NodeAttributeAttribute" />. The kind
///     itself is the <c>silo-node</c> attribute (<see cref="DiagramNodeSchema.KindAttribute" />).
/// </summary>
/// <remarks>
///     Inside the node, binding templates (<c>silo-label</c>, <c>silo-tooltip</c>, <c>silo-style.*</c>, <c>silo-attr.*</c>)
///     can use scoped tokens besides the global <c>{scheme:path}</c> ones: <c>{.Path}</c> reads the object in scope (on the
///     node: what its URI resolved to; on a child: the child's item), <c>{item:Path}</c> / <c>{row:Column}</c> the current
///     item or row, and <c>{node:Path}</c> the owning node's object. A path is dotted (<c>Owner.Name</c>), may index
///     (<c>Items[0]</c>), and is a JSON path on JSON data. A bare <c>{.}</c> / <c>{item}</c> is the object's display text.
/// </remarks>
public abstract class DynamicNode
{
    [NodeAttribute("silo-uri")]
    [Description("The URI of the source the shape's data comes from (file://, a connection's URI, or a value scheme such as silo: or bind:)")]
    public Uri? Uri { get; set; }

    [NodeAttribute("silo-path")]
    [Description("Optional path into the resolved object (dotted property path, or a JSON path for JSON data) to bind to instead of the object itself")]
    public string? Path { get; set; }

    [NodeAttribute("silo-refresh")]
    [DefaultValue(NodeRefresh.OnLoad)]
    [Description("When the node reads its source again: when the diagram loads, only on request, or live (watching or polling the source)")]
    public NodeRefresh Refresh { get; set; } = NodeRefresh.OnLoad;

    [NodeAttribute("silo-poll")]
    [DefaultValue(30)]
    [Description("For live nodes whose source cannot report changes: seconds between reads")]
    public int PollSeconds { get; set; } = 30;
}

/// <summary>When a data-bound node reads its source again.</summary>
public enum NodeRefresh
{
    /// <summary>When the diagram is loaded, and on Refresh.</summary>
    OnLoad,

    /// <summary>Only on Refresh (the diagram shows the values it was saved with until then).</summary>
    Manual,

    /// <summary>Whenever the source reports a change, or every <see cref="DynamicNode.PollSeconds" /> when it cannot.</summary>
    Live
}

/// <summary>Names the cell attribute a node setting is stored in.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class NodeAttributeAttribute(string name) : Attribute
{
    /// <summary>The attribute name, e.g. <c>silo-uri</c>.</summary>
    public string Name { get; } = name;
}

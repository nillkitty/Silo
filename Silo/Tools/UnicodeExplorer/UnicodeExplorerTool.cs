using Telefrag.DI;

namespace Silo.Tools.UnicodeExplorer;

/// <summary>
///     The Unicode Explorer: a tool for finding, inspecting, collecting and keeping characters. It opens as a pane in the dock area, which can be dragged
///     out into a floating window of its own (and, while floating, kept above other windows).
/// </summary>
[Singleton(typeof(ITool))]
public sealed class UnicodeExplorerTool : ToolBase
{
    public UnicodeExplorerTool() : base("unicodeexplorer")
    {
        DisplayName = "Unicode Explorer";
        GroupName   = "Text";
    }

    /// <inheritdoc />
    protected override bool UsesPane => true;

    /// <inheritdoc />
    protected override UIElement OnBuildContent() => new UnicodeExplorerView();
}

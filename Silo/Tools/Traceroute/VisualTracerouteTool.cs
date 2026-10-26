using Telefrag.DI;

namespace Silo.Tools.Traceroute;

/// <summary>
///     Visual Traceroute: traces the route to a host and draws it live in an embedded draw.io diagram -- a circle for this
///     computer, then one shape per hop with its number, address, host name, round-trip times and loss, filled in as the probes
///     and the name lookups come back. Opens as a pane in the dock area (it can be floated).
/// </summary>
/// <remarks>See <see cref="TracerouteView" /> (the pane), <see cref="TraceRoute" /> (the engine) and <see cref="TracerouteDiagramSource" /> (the diagram).</remarks>
[Singleton(typeof(ITool))]
public sealed class VisualTracerouteTool : ToolBase
{
    public VisualTracerouteTool() : base("visualtraceroute")
    {
        DisplayName = "Visual Traceroute";
        GroupName   = "Network";
    }

    /// <inheritdoc />
    protected override bool UsesPane => true;

    /// <inheritdoc />
    protected override double PaneHeight => 480;

    /// <inheritdoc />
    protected override UIElement OnBuildContent()
    {
        return new TracerouteView();
    }

    /// <summary>Opens the pane and traces the route to <paramref name="destination" />.</summary>
    public void Trace(string destination)
    {
        Show();
        if (BuildPaneContent() is TracerouteView view) view.Start(destination);
    }
}

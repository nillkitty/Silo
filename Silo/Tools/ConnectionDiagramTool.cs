using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Silo.Connectors;
using Silo.Model;
using Telefrag.DI;

namespace Silo.Tools;

/// <summary>
///     Was a live <c>RadDiagram</c> (Telerik) with a shape for every open Silo
///     (<see cref="OpenSilo.OpenSilos"/>) and a shape for each of that Silo's active
///     connections (<see cref="OpenSilo.Connections"/>), with the connecting edge labeled
///     with the connection's connector name (<see cref="IConnector.DisplayName"/>).
/// </summary>
/// <remarks>
///     Stubbed out as part of removing the Telerik dependency (RadDiagram had no built-in
///     WPF equivalent, and a hand-rolled Canvas-based box-and-line replacement was
///     deliberately deferred to a later pass rather than rushed through here alongside
///     everything else). <see cref="ITool.IsVisible"/> is set to <see langword="false"/>
///     so it no longer appears in the Tools menu; the content below is a harmless
///     placeholder kept so re-enabling this tool later is just a visibility flip plus a
///     real <see cref="OnBuildContent"/> implementation (a <see cref="Canvas"/> with a
///     <see cref="Border"/> per silo/connection and a <see cref="Line"/> per edge would be
///     a reasonable, dependency-free starting point, mirroring the box/edge layout this
///     used to compute by hand below <c>Rebuild()</c> before this stub replaced it).
///
///     Registered as a singleton: there is only ever one instance of this tool, so its
///     window (and content) is reused every time it's toggled open again rather than being
///     rebuilt from scratch.
/// </remarks>
[Singleton(typeof(ITool))]
public class ConnectionDiagramTool : ToolBase
{
    public ConnectionDiagramTool() : base("connectiondiagram")
    {
        DisplayName = "Connection Diagram";
        GroupName   = "Diagrams";
        IsVisible   = false;
    }

    protected override UIElement OnBuildContent()
    {
        return new TextBlock
               {
                   Text                = "The connection diagram is temporarily unavailable - " +
                                         "it used to be drawn with a Telerik control that's since " +
                                         "been removed, and hasn't been rebuilt on plain WPF yet.",
                   TextWrapping        = TextWrapping.Wrap,
                   Margin              = new Thickness(16),
                   FontStyle           = FontStyles.Italic,
                   Foreground          = Brushes.Gray,
                   HorizontalAlignment = HorizontalAlignment.Center,
                   VerticalAlignment   = VerticalAlignment.Center
               };
    }
}

using Silo.Drawio.Extensions;
using Telefrag.DI;

namespace Silo.Providers.Diagramming;

/// <summary>The Status node: only the node's own bindings, evaluated with what its URI resolved to in scope.</summary>
[Singleton(typeof(IPaletteProvider))]
public sealed class StatusNodeBinder : DiagramNodeBinder
{
    public override string Kind         => "status";
    public override Type   SettingsType => typeof(StatusNode);

    protected override DrawioPaletteEntry CreatePaletteEntry()
    {
        const string style = "rounded=1;whiteSpace=wrap;html=1;fillColor=#dae8fc;strokeColor=#6c8ebf;";
        string xml = PaletteTemplates.Model(
            $"<object id=\"2\" label=\"Status\" silo-label=\"{{.}}\"{DefaultAttributesXml()}>" +
            $"<mxCell style=\"{style}\" vertex=\"1\" parent=\"1\"><mxGeometry width=\"160\" height=\"60\" as=\"geometry\"/></mxCell></object>");

        return new DrawioPaletteEntry(style, 160, 60, "Status", "Status node (bound to a URI)", xml);
    }

    public override Task<BindResult> Bind(BindContext context)
    {
        var result = new BindResult();
        BindOwner(context, result);
        return Task.FromResult(result);
    }
}

/// <summary>Builds draw.io model XML for palette templates.</summary>
internal static class PaletteTemplates
{
    /// <summary>Wraps cells (ids from 2, parent 1) in an <c>mxGraphModel</c> with the usual root cells.</summary>
    public static string Model(string cells)
    {
        return "<mxGraphModel><root><mxCell id=\"0\"/><mxCell id=\"1\" parent=\"0\"/>" + cells + "</root></mxGraphModel>";
    }
}

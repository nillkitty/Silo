using System.IO;
using System.Linq;
using System.Threading;
using Silo.Drawio.Host;

namespace Silo.Drawio.Live;

/// <summary>
///     A click-through reference from a diagram cell to a Silo construct. It is carried in the cell's
///     <c>link</c> as an address on the runtime's virtual host (so draw.io treats it as an ordinary
///     link and, with suppressNewWindows, reports it to Silo as an <c>openLink</c> event instead of
///     navigating).
/// </summary>
public sealed record DiagramLink(string Kind, string Id)
{
    private const string Marker = "/open/";

    public string ToUrl()
    {
        return $"https://{DrawioRuntime.VirtualHost}{Marker}{Uri.EscapeDataString(Kind)}/{Uri.EscapeDataString(Id)}";
    }

    public static bool TryParse(string? href, out DiagramLink link)
    {
        link = null!;
        if (href is null || !Uri.TryCreate(href, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Host, DrawioRuntime.VirtualHost, StringComparison.OrdinalIgnoreCase)) return false;
        if (!uri.AbsolutePath.StartsWith(Marker, StringComparison.Ordinal)) return false;

        string[] parts = uri.AbsolutePath[Marker.Length..].Split('/', 2);
        if (parts.Length != 2 || parts[0].Length == 0) return false;

        link = new DiagramLink(Uri.UnescapeDataString(parts[0]), Uri.UnescapeDataString(parts[1]));
        return true;
    }
}

/// <summary>Something that knows how to act on a <see cref="DiagramLink" /> (select a node, open a window, ...).</summary>
public interface IDiagramLinkHandler
{
    /// <summary>Returns true if the link was handled.</summary>
    bool Handle(DiagramLink link, DrawioView source);
}

/// <summary>The handlers consulted, in order, when a cell link is activated.</summary>
public static class DiagramLinks
{
    public static List<IDiagramLinkHandler> Handlers { get; } = [new SiloModelLinkHandler()];

    public static bool Dispatch(DiagramLink link, DrawioView source)
    {
        foreach (var h in Handlers.ToList())
            if (h.Handle(link, source))
                return true;

        return false;
    }
}

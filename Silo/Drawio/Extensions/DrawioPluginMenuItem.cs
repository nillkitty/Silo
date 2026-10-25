using Silo.Drawio.Host;

namespace Silo.Drawio.Extensions;

// Built on the host side and sent to the Silo plugin (silo-plugin.js), which adds it to draw.io's menu bar.

/// <summary>An entry of the "Silo" menu the plugin adds to draw.io's menu bar.</summary>
/// <param name="Id">Reported back in <see cref="SiloPluginEvent.Menu" /> when the item is chosen.</param>
/// <param name="Label">Menu text.</param>
/// <param name="Shortcut">Optional draw.io shortcut text, e.g. <c>Ctrl+Alt+R</c>.</param>
/// <param name="Separator">When true the entry is a separator and the other fields are ignored.</param>
public sealed record DrawioPluginMenuItem(string Id, string Label, string? Shortcut = null, bool Separator = false)
{
    public static DrawioPluginMenuItem Divider => new("-", "-", null, true);
}

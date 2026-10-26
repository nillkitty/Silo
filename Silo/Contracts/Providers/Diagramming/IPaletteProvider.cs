using Silo.Drawio.Extensions;

namespace Silo.Providers.Diagramming;

/// <summary>
///     Adds shapes to the draw.io shape sidebar. Every registered provider is asked when a diagram pane's plugin is ready;
///     palettes from different providers that share a <see cref="DrawioPluginPalette.Id" /> are merged into one sidebar
///     section, in provider order, so each provider can contribute its own entry to a shared section.
/// </summary>
[RequireRegistration]
public interface IPaletteProvider
{
    /// <summary>The palettes (or palette fragments) this provider contributes.</summary>
    IEnumerable<DrawioPluginPalette> GetPalettes();
}

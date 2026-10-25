namespace Silo.Drawio.Extensions;

// Built on the host side and sent to the Silo plugin (silo-plugin.js), which adds it to draw.io's shape sidebar.

/// <summary>A shape in a plugin-supplied sidebar palette.</summary>
public sealed record DrawioPaletteEntry(string Style, double Width, double Height, string? Value = null, string? Title = null);

/// <summary>A sidebar section ("palette") the plugin adds next to draw.io's own shape libraries.</summary>
public sealed record DrawioPluginPalette(string Id, string Title, IReadOnlyList<DrawioPaletteEntry> Entries, bool Expanded = true);

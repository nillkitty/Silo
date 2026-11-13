using System.Windows.Media;

namespace Silo.Toolbar;

/// <summary>Finds the icon geometries (Resources/NavIcons.xaml and the other icon dictionaries) the toolbar draws with.</summary>
public static class ToolbarIcons
{
    /// <summary>The geometry resource with this key, or null (no such icon, or no key).</summary>
    public static Geometry? Find(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        return Application.Current?.TryFindResource(key) as Geometry ?? Silo.Design.IconCatalog.Find(key)?.Geometry;
    }
}

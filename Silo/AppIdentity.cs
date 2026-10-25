using System.Windows.Media;
using Microsoft.Win32;
using Telefrag.DI;

namespace Silo;

[Singleton(typeof(IAppIdentity))]
[Singleton(typeof(AppIdentity))]
public class AppIdentity : IAppIdentity
{
    public static string             AppName  => Branding.AppName ?? "Silo";
    public static BrandedAppIdentity Branding => field ??= BrandedAppIdentity.Get();

    /// <summary>
    ///     Gets the app's own icon, used when the branding does not set one
    /// </summary>
    public IconRef? DefaultAppIcon { get; }

    /// <summary>
    ///     Gets the user's accent color in Windows (Settings, Personalization, Colors), or null when Windows does not say
    /// </summary>
    public Brush? UserAccentColor { get; }

    /// <summary>
    ///     Gets the accent color to use throughout the app
    /// </summary>
    public Brush AccentColor => Branding.AccentColor ?? UserAccentColor ?? Brushes.LimeGreen;

    /// <summary>
    ///     Gets the current app icon
    /// </summary>
    public IconRef? AppIcon => Branding.AppIcon ?? DefaultAppIcon;


    /// <summary>
    ///     Gets an image to use for the background of the main app window (behind all panes)
    /// </summary>
    public IconRef? BackgroundImage => Branding.BackgroundImage;

    /// <summary>
    ///     Sets an image to use as a Silo-specific about box
    /// </summary>
    public object? CustomAboutBox => Branding.CustomAboutBox;

    /// <summary>
    ///     Gets an image or image source to use as a custom Splash screen,
    ///     or a Window or Control set as a branded custom splash screen.
    /// </summary>
    public object? SplashScreen => Branding.SplashScreen;

    string IAppIdentity.ApplicationName => AppName;

    /// <summary>
    ///     Gets an instance to the identity for this app.
    /// </summary>
    /// <returns></returns>
    public static BrandedAppIdentity Get()
    {
        return App.Require<BrandedAppIdentity>();
    }

    public AppIdentity()
    {
        // Silo has no logo of its own yet (no .ico/.png in the project, and no Silo geometry in the icon dictionaries):
        // until there is one, the database icon stands in. Point this at the real one (a key, or a pack:// URI) when it exists.
        DefaultAppIcon = IconRef.FromKey("Database");

        UserAccentColor = ReadWindowsAccent();
    }

    /// <summary>
    ///     The accent color the user picked in Windows: DWM's <c>AccentColor</c> (stored as 0xAABBGGRR), else its
    ///     <c>ColorizationColor</c> (0xAARRGGBB); null when neither can be read. Read once, at startup.
    /// </summary>
    private static Brush? ReadWindowsAccent()
    {
        try
        {
            using var dwm = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            Color c;
            if (dwm?.GetValue("AccentColor") is int abgr) c = Color.FromRgb((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16));
            else if (dwm?.GetValue("ColorizationColor") is int argb) c = Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
            else return null;

            var brush = new SolidColorBrush(c); // opaque: the stored alpha is for window glass, not for an accent
            brush.Freeze();
            return brush;
        }
        catch
        {
            return null; // no registry access: no user accent
        }
    }
}

public interface IAppIdentity
{
    Brush    AccentColor     { get; }
    IconRef? AppIcon         { get; }
    string   ApplicationName { get; }
    IconRef? BackgroundImage { get; }
    object? CustomAboutBox  { get; }
    object? SplashScreen    { get; }
}

[Singleton(typeof(BrandedAppIdentity))]
public class BrandedAppIdentity
{
    /// <summary>
    ///     Sets an accent color to use throughout the app; null (the default) leaves it to the user's Windows accent
    /// </summary>
    public Brush? AccentColor { get; set; }

    /// <summary>
    ///     Sets a custom App Icon: an icon key or a picture's address
    /// </summary>
    public IconRef? AppIcon { get; set; }

    /// <summary>
    ///     Sets a name to use when referring to the application
    /// </summary>
    public string? AppName { get; set; }

    /// <summary>
    ///     Sets an image to use for the background of the main app window (behind all panes)
    /// </summary>
    public IconRef? BackgroundImage { get; set; }

    /// <summary>
    ///     Sets an image to use as a Silo-specific about box
    /// </summary>
    public object? CustomAboutBox { get; set; }

    /// <summary>
    ///     Sets an image or image source to use as a custom Splash screen,
    ///     or your own custom Control or Window to show.
    /// </summary>
    public object? SplashScreen { get; set; }

    /// <summary>
    ///     Gets an instance to the branded identity for this app.
    /// </summary>
    /// <returns></returns>
    public static BrandedAppIdentity Get()
    {
        return App.Require<BrandedAppIdentity>();
    }
}
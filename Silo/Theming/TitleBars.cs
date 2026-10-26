using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;

namespace Silo.Theming;

/// <summary>
///     What every window's frame looks like, whichever window it is: the title bar follows the Light/Dark theme (Windows draws
///     it light whatever the app does, unless it is told), it is frosted glass where Windows can do that (Windows 11 22H2 and
///     later: the Acrylic backdrop, which Windows draws behind the frame only, so the title bar and the borders are blurred glass
///     and the client area stays the window's own), and a window with no icon of its own gets Silo's (the title bar's and the
///     taskbar's icon otherwise come from whatever the executable carries, which Windows keeps in a cache of its own).
/// </summary>
/// <remarks>
///     Turned on for all windows by <c>Theme.Styles.xaml</c> (its <c>Window</c> style sets <see cref="EnabledProperty" />, and
///     <c>App._themeDerivedWindows</c> makes that style the default of <see cref="Window" />), and, for a window with a style
///     of its own, by <see cref="Install" />, which also follows theme changes. Every call into Windows is best effort: an
///     older Windows just keeps the stock frame.
/// </remarks>
public static class TitleBars
{
    // dwmapi.h, DWMWINDOWATTRIBUTE
    private const int UseImmersiveDarkModeBefore20H1 = 19;
    private const int UseImmersiveDarkMode           = 20;
    private const int SystemBackdropType             = 38;

    // DWM_SYSTEMBACKDROP_TYPE
    private const int BackdropNone      = 1;
    private const int BackdropTransient = 3; // Acrylic

    private static bool          _installed;
    private static bool          _frosted = true;
    private static ImageSource?  _icon;
    private static bool          _iconLoaded;

    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(TitleBars), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    /// <summary>Frosted glass for the title bars (on by default); off leaves the title bars solid, dark or light with the theme.</summary>
    public static bool Frosted
    {
        get => _frosted;
        set
        {
            if (_frosted == value) return;
            _frosted = value;
            RefreshAll();
        }
    }

    /// <summary>Follows theme changes and covers windows whose own style leaves out <see cref="EnabledProperty" />. Call once at startup.</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
        AppTheme.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppTheme.IsEffectivelyDark)) RefreshAll();
        };
    }

    /// <summary>Applies the frame to every open window again (the theme changed, or <see cref="Frosted" /> did).</summary>
    public static void RefreshAll()
    {
        if (Application.Current is not { } app) return;
        foreach (Window window in app.Windows) Apply(window);
    }

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window || e.NewValue is not true) return;

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            Apply(window);
            return;
        }

        // the style is applied while the window is built: the frame has to wait for its handle (before it is shown, so there is no flash)
        window.SourceInitialized += (_, _) => Apply(window);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window && ReferenceEquals(e.OriginalSource, window)) Apply(window);
    }

    private static void Apply(Window window)
    {
        try
        {
            SetIcon(window);

            nint handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;

            // a window that draws its own frame (palettes, popups) has no title bar to follow
            if (window.WindowStyle == WindowStyle.None || window.AllowsTransparency) return;

            SetDark(handle, AppTheme.Instance.IsEffectivelyDark);

            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)) return;

            int backdrop = _frosted ? BackdropTransient : BackdropNone;
            DwmSetWindowAttribute(handle, SystemBackdropType, ref backdrop, sizeof(int));

            // The backdrop belongs to the frame (the title bar and the borders). The frame is deliberately NOT extended into the
            // client area (DwmExtendFrameIntoClientArea): that is what lets the glass show through the whole window.
            if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target)
                target.BackgroundColor = ClientColor();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or COMException)
        {
            Log.Debug(ex, "Could not style the frame of {Window}", window.GetType().Name);
        }
    }

    private static Color ClientColor() =>
        Application.Current?.TryFindResource("Theme.Background") is SolidColorBrush brush
            ? Color.FromRgb(brush.Color.R, brush.Color.G, brush.Color.B)
            : SystemColors.WindowColor;

    private static void SetDark(nint handle, bool dark)
    {
        int value = dark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref value, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, UseImmersiveDarkModeBefore20H1, ref value, sizeof(int)); // Windows 10 before 2004
    }

    private static void SetIcon(Window window)
    {
        if (window.Icon is not null) return;

        if (!_iconLoaded)
        {
            _iconLoaded = true;
            foreach (string address in new[] { "pack://application:,,,/Silo;component/Assets/Silo.ico", "pack://application:,,,/Assets/Silo.ico" })
            {
                try
                {
                    var frame = BitmapFrame.Create(new Uri(address, UriKind.Absolute), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    frame.Freeze();
                    _icon = frame;
                    break;
                }
                catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException or InvalidOperationException)
                {
                    Log.Debug(ex, "The window icon could not be loaded from {Address}", address);
                }
            }
        }

        if (_icon is not null) window.Icon = _icon;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}

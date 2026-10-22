using System.IO;
using System.Text.Json;
using Serilog;

namespace Silo.Ui.Windows;

/// <summary>
///     Where the main window was when it was last closed: its place, its size, and whether it was maximized. Kept in the
///     person's profile (<c>%APPDATA%\Silo\mainwindow.json</c>), apart from any Silo, so it is put back even when no Silo
///     is open, and without a Silo having to be saved first (see <see cref="WindowPlacements" />, which keeps the arrangement
///     of the panes with the Silo).
/// </summary>
public static class MainWindowState
{
    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Silo", "mainwindow.json");

    /// <summary>Whether a place was kept (the window has been closed once since this was added).</summary>
    public static bool HasSaved => File.Exists(FilePath);

    /// <summary>
    ///     A window's frame. The place and size are those it has when it is not maximized (what it goes back to when the person
    ///     restores it).
    /// </summary>
    /// <param name="Left">The left edge; null when it is not known.</param>
    /// <param name="Top">The top edge; null when it is not known.</param>
    /// <param name="Width">The width of the normal window.</param>
    /// <param name="Height">The height of the normal window.</param>
    /// <param name="Maximized">Whether the window was maximized.</param>
    public sealed record Frame(double? Left, double? Top, double Width, double Height, bool Maximized);

    /// <summary>
    ///     The frame made to fit the screens: smaller if it is bigger than the screens, and without a place when that would
    ///     leave too little of the title bar on a screen to grab (a monitor that is gone). Null when the frame is not usable.
    /// </summary>
    public static Frame? Fit(Frame? frame, Rect screen)
    {
        if (frame is null || double.IsNaN(frame.Width) || double.IsNaN(frame.Height) || frame.Width < 200 || frame.Height < 120) return null;

        double width  = Math.Min(frame.Width,  screen.Width);
        double height = Math.Min(frame.Height, screen.Height);

        bool placed = frame is { Left: { } left, Top: { } top } && !double.IsNaN(left) && !double.IsNaN(top) &&
                      WindowPlacements.IsReachable(new Rect(left, top, width, 40), screen);

        return new Frame(placed ? frame.Left : null, placed ? frame.Top : null, width, height, frame.Maximized);
    }

    /// <summary>The frame as it was kept; null when none was kept or it cannot be read.</summary>
    public static Frame? Read(string? file = null)
    {
        try
        {
            file ??= FilePath;
            return File.Exists(file) ? JsonSerializer.Deserialize<Frame>(File.ReadAllText(file)) : null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Reading where the main window was failed");
            return null;
        }
    }

    /// <summary>Keeps the frame. A failure is logged, never thrown: the window not being remembered is not worth a message.</summary>
    public static void Write(Frame frame, string? file = null)
    {
        try
        {
            file ??= FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(frame));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Keeping where the main window is failed");
        }
    }

    /// <summary>The window's frame now (its restore place and size while it is maximized or minimized); null before it was ever shown.</summary>
    public static Frame? Capture(Window window)
    {
        var bounds = window.WindowState == WindowState.Normal
                         ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)
                         : window.RestoreBounds;
        if (bounds.IsEmpty || double.IsNaN(bounds.X) || double.IsNaN(bounds.Y) || bounds.Width < 1 || bounds.Height < 1) return null;

        return new Frame(bounds.X, bounds.Y, bounds.Width, bounds.Height, window.WindowState == WindowState.Maximized);
    }

    /// <summary>Keeps where the window is now.</summary>
    public static void Save(Window window)
    {
        if (Capture(window) is { } frame) Write(frame);
    }

    /// <summary>Puts the window where it was left, before it is shown: its size and place (when still on a screen), and maximized.</summary>
    public static void Apply(Window window)
    {
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth,
                              SystemParameters.VirtualScreenHeight);
        if (Fit(Read(), screen) is not { } frame) return;

        window.Width  = frame.Width;
        window.Height = frame.Height;
        if (frame is { Left: { } left, Top: { } top })
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left                  = left;
            window.Top                   = top;
        }

        if (frame.Maximized) window.WindowState = WindowState.Maximized;
    }
}

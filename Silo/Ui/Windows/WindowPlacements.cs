using System.Runtime.CompilerServices;
using Serilog;
using Silo.DbModel.Global;
using Silo.Model;
using Silo.Providers.Data;

namespace Silo.Ui.Windows;

/// <summary>
///     A window that remembers more than its place and size: how its parts are arranged (the main window's dock
///     panes).
/// </summary>
public interface IWindowLayout
{
    /// <summary>The arrangement as text, or null when it cannot be captured right now.</summary>
    string? CaptureLayout();

    /// <summary>Puts the arrangement back.</summary>
    void RestoreLayout(string layout);
}

/// <summary>
///     Keeps the position, size and (for the main window) pane arrangement of windows in the Silo they were used with
///     (<see cref="IWindowStore" />, the <c>Window</c> table), and puts them back when the window is next opened.
/// </summary>
/// <remarks>
///     <para>
///         Each Silo has a tracker of the windows opened while it was active. Its <see cref="OpenSilo.SaveHooks" /> entry
///         writes the current place of every live window just before the Silo is saved or closed; a window that is closed
///         earlier is written when it closes. Nothing here marks the Silo as changed, so rearranging windows alone does
///         not
///         cause a save prompt -- the arrangement is kept by the next save that happens for another reason.
///     </para>
///     <para>
///         A saved place that would leave the window off-screen (a monitor that is gone) is ignored for the position; the
///         size
///         is still used.
///     </para>
/// </remarks>
public static class WindowPlacements
{
    public const string Main   = "Main";
    public const string Table  = "Table";
    public const string Tool   = "Tool";
    public const string Dialog = "Dialog";

    private static readonly ConditionalWeakTable<OpenSilo, Tracker> Trackers = new();
    private static readonly ConditionalWeakTable<Window, object>    Hooked   = new();

    /// <summary>
    ///     Starts remembering a window for <paramref name="silo" />, and puts back the place it was left in (once per window
    ///     and
    ///     Silo: showing the window again does not undo where it was moved to meanwhile).
    /// </summary>
    /// <param name="type">
    ///     What kind of window (<see cref="Main" />, <see cref="Table" />, <see cref="Tool" />,
    ///     <see cref="Dialog" />).
    /// </param>
    /// <param name="uri">Which one of that kind, e.g. <c>silo:table/5</c>.</param>
    /// <param name="layout">For a window with an arrangement of its own to keep: the part that captures and restores it.</param>
    /// <param name="applySaved">False to only start remembering (the window is already where the person wants it).</param>
    /// <param name="saveOnClose">
    ///     Write the place when the window closes (not for the main window: its place is written with
    ///     the Silo).
    /// </param>
    /// <param name="applyFrame">
    ///     False to put back only the arrangement (<paramref name="layout" />), not the place, size and state: the main
    ///     window's frame is kept apart from any Silo (see <see cref="MainWindowState" />).
    /// </param>
    public static void Track(Window window, OpenSilo? silo, string type, string uri, IWindowLayout? layout = null, bool applySaved = true,
                             bool   saveOnClose = true, bool applyFrame = true)
    {
        if (silo?.Data is not IWindowStore store) return;

        var tracker = Trackers.GetValue(silo, NewTracker);
        if (tracker.Entries.Any(e => e.Type == type && e.Uri == uri && e.Window.TryGetTarget(out var w) && ReferenceEquals(w, window)))
            return;

        tracker.Entries.RemoveAll(e => !e.Window.TryGetTarget(out _));
        tracker.Entries.Add(new Entry(window, type, uri, layout));

        if (applySaved)
            try
            {
                if (store.GetWindow(type, uri) is { } saved)
                {
                    if (applyFrame) Apply(window, saved);
                    if (layout is not null && saved.Layout is { Length: > 0 } arrangement) layout.RestoreLayout(arrangement);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not restore the place of window {uri}", uri);
            }

        if (saveOnClose && !Hooked.TryGetValue(window, out _))
        {
            Hooked.Add(window, new object());
            window.Closing += (_, _) => WriteWhereTracked(window);
        }
    }

    private static Tracker NewTracker(OpenSilo silo)
    {
        var tracker = new Tracker(silo);
        silo.SaveHooks.Add(tracker.Flush);
        return tracker;
    }

    /// <summary>A window is closing: writes its place to every open Silo that tracks it.</summary>
    private static void WriteWhereTracked(Window window)
    {
        foreach (var silo in OpenSilo.OpenSilos.ToList())
        {
            if (!Trackers.TryGetValue(silo, out var tracker)) continue;

            var mine = tracker.Entries.Where(e => e.Window.TryGetTarget(out var w) && ReferenceEquals(w, window)).ToList();
            if (mine.Count > 0) WriteAll(silo, mine);
        }
    }

    private static void WriteAll(OpenSilo silo, IReadOnlyCollection<Entry> entries)
    {
        if (silo.Data is not IWindowStore store) return;

        try
        {
            var records = new List<WindowRecord>();
            foreach (var entry in entries)
                if (entry.Window.TryGetTarget(out var window) && Capture(window, entry) is { } record)
                    records.Add(record);

            store.SaveWindows(records);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not remember the place of the windows");
        }
    }

    /// <summary>Where a window is now (its restore place when it is maximized or minimized), or null if it never was shown.</summary>
    private static WindowRecord? Capture(Window window, Entry entry)
    {
        if (!window.IsLoaded && window.ActualWidth <= 0) return null;

        var bounds = window.WindowState == WindowState.Normal
                         ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)
                         : window.RestoreBounds;
        if (bounds.IsEmpty || double.IsNaN(bounds.X) || double.IsNaN(bounds.Y) || bounds.Width < 1 || bounds.Height < 1) return null;

        var state = window.WindowState == WindowState.Maximized ? WindowPlacementState.Maximized : WindowPlacementState.Normal;
        return new WindowRecord(entry.Type, entry.Uri, (int)Math.Round(bounds.X), (int)Math.Round(bounds.Y), (int)Math.Round(bounds.Width),
                                (int)Math.Round(bounds.Height), state, entry.Layout?.CaptureLayout());
    }

    /// <summary>Puts the window back as it was left: size, position when it is still on a screen, maximized.</summary>
    private static void Apply(Window window, WindowRecord saved)
    {
        if (window.WindowState != WindowState.Normal) window.WindowState = WindowState.Normal;

        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth,
                              SystemParameters.VirtualScreenHeight);

        // a window that sizes itself to its content keeps doing that
        if (window.SizeToContent == SizeToContent.Manual && saved.Width >= 100 && saved.Height >= 60)
        {
            window.Width  = Math.Max(window.MinWidth,  Math.Min(saved.Width,  screen.Width));
            window.Height = Math.Max(window.MinHeight, Math.Min(saved.Height, screen.Height));
        }

        double width = double.IsNaN(window.Width) ? saved.Width : window.Width;
        if (IsReachable(new Rect(saved.X, saved.Y, width, 40), screen))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left                  = saved.X;
            window.Top                   = saved.Y;
        }

        if (saved.State == WindowPlacementState.Maximized) window.WindowState = WindowState.Maximized;
    }

    /// <summary>True when enough of the title bar strip is on a screen to grab the window with the mouse.</summary>
    internal static bool IsReachable(Rect titleBar, Rect screen)
    {
        var visible = Rect.Intersect(titleBar, screen);
        return !visible.IsEmpty && visible.Width >= 80 && visible.Height >= 20;
    }

    private sealed class Entry(Window window, string type, string uri, IWindowLayout? layout)
    {
        public IWindowLayout?        Layout { get; } = layout;
        public string                Type   { get; } = type;
        public string                Uri    { get; } = uri;
        public WeakReference<Window> Window { get; } = new(window);
    }

    private sealed class Tracker(OpenSilo silo)
    {
        public List<Entry> Entries { get; } = [];
        public OpenSilo    Silo    { get; } = silo;

        /// <summary>The save hook: writes where every live window is now.</summary>
        public Task Flush()
        {
            WriteAll(Silo, Entries.Where(e => e.Window.TryGetTarget(out _)).ToList());
            return Task.CompletedTask;
        }
    }
}
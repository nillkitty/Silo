using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Silo.Tasks;

/// <summary>
///     The toast that says a task the person asked about has ended: a small window in the corner of the screen that does not take the keyboard
///     focus, stays a few seconds (longer for a failure) and goes away when clicked. Toasts that are up at the same time stack upwards.
/// </summary>
public static class TaskToast
{
    private static readonly List<Window> Open = [];

    /// <summary>The words a toast says about a task: its name and how it ended, then its count, size and time when it has them.</summary>
    public static (string Title, string Detail) Describe(ITrackedTask task)
    {
        string title = task.State switch
                       {
                           TaskState.Succeeded => task.Name + " completed",
                           TaskState.Failed    => task.Name + " failed",
                           TaskState.Cancelled => task.Name + " cancelled",
                           _                   => task.Name
                       };

        var parts = new List<string>();
        if (task.State == TaskState.Failed && !string.IsNullOrWhiteSpace(task.Message)) parts.Add(task.Message!);
        if (task.ResultCount is { } n) parts.Add($"{n:N0} result{(n == 1 ? "" : "s")}");
        if (task.ResultBytes is { } b) parts.Add($"{b:N0} bytes");
        parts.Add(Elapsed(task.Duration));
        return (title, string.Join(" · ", parts));
    }

    public static string Elapsed(TimeSpan span)
    {
        if (span.TotalSeconds < 1) return $"{span.TotalMilliseconds:0} ms";
        if (span.TotalMinutes < 1) return $"{span.TotalSeconds:0.#} s";
        return span.TotalHours < 1 ? $"{(int)span.TotalMinutes}:{span.Seconds:00}" : $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}";
    }

    public static void Show(ITrackedTask task)
    {
        if (Application.Current?.Dispatcher is not { } dispatcher) return;

        dispatcher.BeginInvoke(new Action(() => ShowNow(task)), DispatcherPriority.Normal);
    }

    private static void ShowNow(ITrackedTask task)
    {
        var (title, detail) = Describe(task);
        bool failed         = task.State == TaskState.Failed;

        var accent = new SolidColorBrush(task.State switch
                                         {
                                             TaskState.Succeeded => Color.FromRgb(0x4C, 0xAF, 0x50),
                                             TaskState.Failed    => Color.FromRgb(0xE5, 0x39, 0x35),
                                             _                   => Color.FromRgb(0xFF, 0xB3, 0x00)
                                         });
        var text = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(0, 2, 0, 0) });

        var card = new Border
                   {
                       Child = new DockPanel { Children = { new Border { Width = 5, Background = accent, Tag = "stripe" }, text } },
                       BorderThickness = new Thickness(1)
                   };
        DockPanel.SetDock(((DockPanel)card.Child).Children[0], Dock.Left);
        card.SetResourceReference(Border.BackgroundProperty, "Theme.Background");
        card.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
        card.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text");

        var window = new Window
                     {
                         WindowStyle         = WindowStyle.None,
                         ResizeMode          = ResizeMode.NoResize,
                         ShowInTaskbar       = false,
                         ShowActivated       = false,
                         Topmost             = true,
                         Width               = 340,
                         SizeToContent       = SizeToContent.Height,
                         Content             = card,
                         AllowsTransparency  = false,
                         Focusable           = false
                     };
        window.MouseLeftButtonUp += (_, _) => window.Close();
        window.Closed += (_, _) =>
                         {
                             Open.Remove(window);
                             Restack();
                         };

        Open.Add(window);
        window.Show();
        Restack();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(failed ? 12 : 6) };
        timer.Tick += (_, _) =>
                      {
                          timer.Stop();
                          if (window.IsVisible) window.Close();
                      };
        timer.Start();
    }

    /// <summary>Puts the toasts in the bottom-right corner of the work area, the newest at the bottom.</summary>
    private static void Restack()
    {
        var area = SystemParameters.WorkArea;
        double y = area.Bottom - 12;
        for (int i = Open.Count - 1; i >= 0; i--)
        {
            var w = Open[i];
            w.UpdateLayout();
            double h = w.ActualHeight > 0 ? w.ActualHeight : 64;
            y      -= h;
            w.Left =  area.Right - w.Width - 12;
            w.Top  =  y;
            y      -= 8;
        }
    }
}

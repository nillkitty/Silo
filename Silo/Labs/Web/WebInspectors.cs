using System.Collections;
using System.Windows.Controls;
using System.Windows.Media;
using Serilog;

namespace Silo.Labs;

/// <summary>Small helpers for the code-built views of the Web Lab's inspectors.</summary>
internal static class WebUi
{
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");

    public static T Muted<T>(this T element) where T : FrameworkElement
    {
        element.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        return element;
    }

    public static ListBox ThemedList(IEnumerable items)
    {
        var list = new ListBox
                   {
                       ItemsSource                = items, BorderThickness = new Thickness(0),
                       HorizontalContentAlignment = HorizontalAlignment.Stretch
                   };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        list.SetResourceReference(Control.BackgroundProperty, "Theme.Background");
        list.SetResourceReference(Control.ForegroundProperty, "Theme.Text");
        return list;
    }

    /// <summary>A button with an icon and its text (the icon that fits the text unless one is given).</summary>
    public static Button Tool(string text, Action click, string? tip = null, string? icon = null)
    {
        var button = LabUi.Tool(text, icon, click, tip);
        button.Margin = new Thickness(0, 0, 4, 0);
        return button;
    }

    public static TextBox Output(double height = 0)
    {
        var box = new TextBox
                  {
                      IsReadOnly                    = true,
                      FontFamily                    = Mono,
                      TextWrapping                  = TextWrapping.NoWrap,
                      VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                      HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                      BorderThickness               = new Thickness(0),
                      Padding                       = new Thickness(6)
                  };
        if (height > 0) box.Height = height;
        box.SetResourceReference(Control.BackgroundProperty, "Theme.Background");
        box.SetResourceReference(Control.ForegroundProperty, "Theme.Text");
        return box;
    }

    public static string FormatSize(double bytes)
    {
        if (bytes < 1024) return $"{bytes:0} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024:0.#} KB";
        return $"{bytes / 1024 / 1024:0.#} MB";
    }
}

/// <summary>
///     The base of the Web Lab's inspectors: each is a view of what the page is doing, read through the DevTools protocol
///     once the page's browser is up (<see cref="Attach" />).
/// </summary>
internal abstract class WebInspector : LabInspector
{
    /// <summary>The DevTools protocol of the page; null until the browser is up.</summary>
    protected DevTools? Dev { get; private set; }

    protected WebLabSession Session { get; }

    internal void Attach(DevTools dev)
    {
        Dev = dev;
        OnAttached();
    }

    protected virtual void OnAttached()
    {
    }

    /// <summary>The page is about to go somewhere else.</summary>
    internal virtual void OnNavigating(Uri uri)
    {
    }

    /// <summary>The page finished loading.</summary>
    internal virtual void OnNavigated(Uri? uri)
    {
    }

    /// <summary>Runs work the inspector does not wait for; a failure is logged, never thrown at the user.</summary>
    protected static async void RunSafe(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "A Web Lab inspector's work failed");
        }
    }

    protected WebInspector(WebLabSession session)
    {
        Session = session;
    }
}

using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Silo.Design;

namespace Silo.Commanding;

/// <summary>
///     Hold Ctrl+Shift and hover over anything: a pink tip lists what its right-click menu is built from -- the target,
///     each
///     object asked for commands, the model, and every type checked for registered providers (see
///     <see cref="ContextCommands.Explain" />). Releasing either key hides it.
/// </summary>
public static class ContextMenuInspector
{
    private static Popup?            _popup;
    private static TextBlock?        _text;
    private static DependencyObject? _shownFor;
    private static bool              _started;

    private static bool Armed =>
        (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift);

    /// <summary>Starts watching every window. Call once at startup.</summary>
    public static void Start()
    {
        if (_started) return;
        _started = true;

        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseMoveEvent, new MouseEventHandler((_, _) => Update()), true);
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyDownEvent,   new KeyEventHandler((_,   _) => Update()), true);
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyUpEvent,     new KeyEventHandler((_,   _) => Update()), true);
        EventManager.RegisterClassHandler(typeof(Window), UIElement.MouseLeaveEvent,       new MouseEventHandler((_, _) => Hide()),   true);
    }

    private static void Update()
    {
        if (!Armed || Mouse.DirectlyOver is not DependencyObject over)
        {
            Hide();
            return;
        }

        // the tip itself is under the mouse when it opens there: keep it
        if (_popup?.Child is { } child       && IsInside(over, child)) return;
        if (ReferenceEquals(over, _shownFor) && _popup?.IsOpen == true) return;

        string text;
        try
        {
            text = string.Join(Environment.NewLine, ContextCommands.Explain(over));
        }
        catch (Exception ex)
        {
            text = "Could not work out the context menu: " + ex.Message;
        }

        Show(over, text);
    }

    private static void Hide()
    {
        _shownFor = null;
        if (_popup is not null) _popup.IsOpen = false;
    }

    private static bool IsInside(DependencyObject? d, DependencyObject container)
    {
        for (; d is not null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (ReferenceEquals(d, container))
                return true;
        return false;
    }

    private static void Show(DependencyObject over, string text)
    {
        if (_popup is null)
        {
            _text = new TextBlock { FontFamily = new FontFamily("Segoe UI Regular"), FontSize = 12, Foreground = DesignMenu.PinkText };
            _popup = new Popup
                     {
                         AllowsTransparency = true,
                         Placement          = PlacementMode.Mouse,
                         HorizontalOffset   = 16,
                         VerticalOffset     = 16,
                         IsHitTestVisible   = false,
                         Child = new Border
                                 {
                                     Background       = DesignMenu.PinkFill,
                                     BorderBrush      = DesignMenu.PinkBorder,
                                     BorderThickness  = new Thickness(1),
                                     CornerRadius     = new CornerRadius(3),
                                     Padding          = new Thickness(8, 6, 8, 6),
                                     Child            = _text,
                                     IsHitTestVisible = false
                                 }
                     };
        }

        _shownFor     = over;
        _text!.Text   = text;
        _popup.IsOpen = false; // reopening places it at the mouse again
        _popup.IsOpen = true;
    }
}
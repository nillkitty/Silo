using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Silo.Design;

/// <summary>
///     One entry in a "design menu" (see <see cref="DesignMenu"/>): either a plain clickable
///     action (set <see cref="Command"/>) or a piece of live, interactive content - a slider, a
///     dial, a checkbox, whatever - that's shown inline and does <em>not</em> close the menu
///     when interacted with (set <see cref="Content"/>; see <see cref="Slider"/> for the
///     ready-made case this app needed first).
/// </summary>
public sealed class DesignMenuItem
{
    /// <summary>The lowest <see cref="DesignLevel"/> this item is visible at. Defaults to <see cref="DesignLevel.Author"/> - the "someone doing light customization, not necessarily a developer" tier.</summary>
    public DesignLevel MinLevel { get; init; } = DesignLevel.Author;

    /// <summary>Label shown for a command item, or as the caption above a content item (skip by leaving it null if <see cref="Content"/> already carries its own label).</summary>
    public string? Header { get; init; }

    /// <summary>Set for a plain clickable row. Mutually exclusive with <see cref="Content"/> in practice (a content item ignores this).</summary>
    public ICommand? Command { get; init; }

    public object? CommandParameter { get; init; }

    /// <summary>Set for a rich, interactive row (a slider, etc.) hosted inline in the menu instead of a plain clickable label.</summary>
    public UIElement? Content { get; init; }

    /// <summary>A thin divider, gated the same as any other item. <see cref="DesignMenu"/> also drops one automatically between items contributed by different elements, so this is only for grouping within a single element's own list.</summary>
    public bool IsSeparator { get; init; }

    public static DesignMenuItem Separator(DesignLevel minLevel = DesignLevel.Author) => new() { IsSeparator = true, MinLevel = minLevel };

    /// <summary>
    ///     A ready-made labeled-slider <see cref="DesignMenuItem"/>: drag it, the value calls
    ///     back live via <paramref name="setValue"/> (no data-binding plumbing required), and
    ///     the menu stays open the whole time (see <see cref="DesignMenu.BuildContextMenu"/>).
    ///     This is the general-purpose building block behind the app icon's runtime-adjustable
    ///     size - any future "change a number live" design setting is this same call with a
    ///     different getter/setter pair.
    /// </summary>
    public static DesignMenuItem Slider(
        string header, DesignLevel minLevel, double min, double max,
        Func<double> getValue, Action<double> setValue, string format = "{0:0}")
    {
        var slider = new Slider
                     {
                         Minimum           = min,
                         Maximum           = max,
                         Value             = getValue(),
                         Width             = 160,
                         VerticalAlignment = VerticalAlignment.Center,
                         Margin            = new Thickness(0, 0, 8, 0)
                     };

        var valueLabel = new TextBlock
                          {
                              Text              = string.Format(format, slider.Value),
                              MinWidth          = 34,
                              VerticalAlignment = VerticalAlignment.Center
                          };

        slider.ValueChanged += (_, e) =>
                                {
                                    setValue(e.NewValue);
                                    valueLabel.Text = string.Format(format, e.NewValue);
                                };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(slider);
        row.Children.Add(valueLabel);

        return new DesignMenuItem { Header = header, MinLevel = minLevel, Content = row };
    }
}

/// <summary>
///     The mechanism: gives <em>any</em> <see cref="DependencyObject"/> - a control, a window,
///     eventually anything - a list of <see cref="DesignMenuItem" />s (an attached property, so
///     nothing needs to derive from a special base type to opt in) and a single app-wide
///     Ctrl+right-click hook that finds them and pops a pink context menu - the "pink note
///     block" the person asked for, distinct on sight from every other menu in the app so it
///     reads as the secret authoring layer HyperCard's user levels were going for.
/// </summary>
/// <remarks>
///     <para>
///     <b>Attaching items.</b> Call <see cref="EnsureItems"/> on whatever element the menu
///     should be reachable from and add to the collection it returns:
///     <code>
///     DesignMenu.EnsureItems(NavTree).Add(DesignMenuItem.Slider(
///         "Icon Size", DesignLevel.Author, 12, 64,
///         () => AppDesignSettings.Instance.IconSize,
///         v => AppDesignSettings.Instance.IconSize = v));
///     </code>
///     Items aren't limited to the exact element that was clicked: a Ctrl+right-click walks up
///     the visual (falling back to logical) tree from whatever was actually hit and merges in
///     every ancestor's items, closest first, with a separator between each contributing
///     element's group. Attaching a broadly-useful item to a window or a root panel makes it
///     reachable from anywhere inside that subtree, the same way a single item on the
///     navigation tree is reachable from any of its nodes.
///     </para>
///     <para>
///     <b>Reachability.</b> Nothing happens unless Ctrl is held <em>and</em>
///     <see cref="AppDesignLevel.Instance"/> is <see cref="DesignLevel.Author"/> or higher - in
///     plain <see cref="DesignLevel.User"/> mode the hook does nothing at all, so an ordinary
///     right-click (Ctrl or not) never changes behavior for someone who hasn't opted into a
///     design level. Above that gate, each item is further filtered by its own
///     <see cref="DesignMenuItem.MinLevel"/>, so an Author sees fewer items than a Developer,
///     who sees fewer than someone in <see cref="DesignLevel.Debug"/>.
///     </para>
///     <para>
///     <b>Why a class handler.</b> <see cref="Register"/> hooks
///     <see cref="UIElement.PreviewMouseRightButtonDownEvent"/> once, app-wide, via
///     <see cref="EventManager.RegisterClassHandler(System.Type,System.Windows.RoutedEvent,System.Delegate)"/>
///     on <see cref="UIElement"/> itself - not on any specific control - so it fires for every
///     control in the app, template-internal parts included, with no per-control wiring. Preview
///     (tunneling) events reach the outermost element in a window first with
///     <see cref="MouseButtonEventArgs.OriginalSource"/> already resolved to the real hit
///     target, so the handler does its work and marks the event handled right there; class
///     handlers skip already-handled events by default, so every deeper tunneling stage for
///     that same click is skipped automatically. An ordinary right-click (no Ctrl, or nothing
///     qualifies) returns immediately without setting <c>Handled</c>, so it costs one cheap
///     modifier check per visual-tree level and never interferes with normal context menus.
///     </para>
/// </remarks>
public static class DesignMenu
{
    public static readonly DependencyProperty ItemsProperty =
        DependencyProperty.RegisterAttached("Items", typeof(ObservableCollection<DesignMenuItem>), typeof(DesignMenu));

    private static readonly SolidColorBrush PinkFill      = Freeze(0xFF, 0xD3, 0xEC);
    private static readonly SolidColorBrush PinkFillHover = Freeze(0xFF, 0x8F, 0xD1);
    private static readonly SolidColorBrush PinkBorder    = Freeze(0xE0, 0x3F, 0x94);
    private static readonly SolidColorBrush PinkText      = Freeze(0x5A, 0x0A, 0x38);

    private static bool _registered;

    private static SolidColorBrush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>Gets the design-menu items attached to <paramref name="obj"/>, or null if none were ever attached (see <see cref="EnsureItems"/> to attach).</summary>
    public static ObservableCollection<DesignMenuItem>? GetItems(DependencyObject obj) =>
        (ObservableCollection<DesignMenuItem>?)obj.GetValue(ItemsProperty);

    public static void SetItems(DependencyObject obj, ObservableCollection<DesignMenuItem>? value) =>
        obj.SetValue(ItemsProperty, value);

    /// <summary>Gets (creating if necessary) the design-menu items attached to <paramref name="obj"/> - the entry point for attaching items, so a caller never has to pre-create the collection itself.</summary>
    public static ObservableCollection<DesignMenuItem> EnsureItems(DependencyObject obj)
    {
        if (GetItems(obj) is { } existing) return existing;

        var created = new ObservableCollection<DesignMenuItem>();
        SetItems(obj, created);
        return created;
    }

    /// <summary>Hooks the app-wide Ctrl+right-click handler. Call once, early in startup (see <see cref="App.OnStartup"/>) - idempotent, so an accidental second call is harmless.</summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;

        EventManager.RegisterClassHandler(typeof(UIElement), UIElement.PreviewMouseRightButtonDownEvent,
                                          new MouseButtonEventHandler(OnPreviewRightButtonDown));
    }

    private static void OnPreviewRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        if (!AppDesignLevel.Instance.IsAtLeast(DesignLevel.Author)) return;

        var groups = CollectGroups(e.OriginalSource as DependencyObject);
        if (groups.Count == 0) return;

        var menu = BuildContextMenu(groups);
        if (menu.Items.Count == 0) return;

        menu.PlacementTarget = e.OriginalSource as UIElement ?? sender as UIElement;
        menu.Placement       = PlacementMode.MousePoint;
        menu.IsOpen          = true;
        e.Handled            = true;
    }

    /// <summary>Walks up from <paramref name="start"/> collecting each ancestor's level-qualifying items, closest element first.</summary>
    private static List<List<DesignMenuItem>> CollectGroups(DependencyObject? start)
    {
        var groups = new List<List<DesignMenuItem>>();
        var level  = AppDesignLevel.Instance.Level;
        var node   = start;
        var seen   = new HashSet<DependencyObject>();

        while (node != null && seen.Add(node))
        {
            if (GetItems(node) is { Count: > 0 } items)
            {
                var qualifying = items.Where(i => i.MinLevel <= level).ToList();
                if (qualifying.Count > 0) groups.Add(qualifying);
            }

            node = GetParent(node);
        }

        return groups;
    }

    /// <summary>Visual-tree parent, falling back to the logical tree for elements (like some template-internal or logical-only nodes) that don't have one.</summary>
    private static DependencyObject? GetParent(DependencyObject node) =>
        (node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : null)
        ?? LogicalTreeHelper.GetParent(node);

    private static ContextMenu BuildContextMenu(List<List<DesignMenuItem>> groups)
    {
        var menu = new ContextMenu
                   {
                       Background      = PinkFill,
                       BorderBrush     = PinkBorder,
                       BorderThickness = new Thickness(2),
                       Padding         = new Thickness(3)
                   };

        for (int g = 0; g < groups.Count; g++)
        {
            if (g > 0) menu.Items.Add(BuildSeparator());

            foreach (var item in groups[g])
                if (item.IsSeparator)
                    menu.Items.Add(BuildSeparator());
                else
                    menu.Items.Add(BuildMenuItem(item));
        }

        return menu;
    }

    private static MenuItem BuildMenuItem(DesignMenuItem item)
    {
        var mi = new MenuItem
                 {
                     Foreground = PinkText,
                     Template   = PinkMenuItemTemplate
                 };

        if (item.Content != null)
        {
            if (string.IsNullOrEmpty(item.Header))
            {
                mi.Header = item.Content;
            }
            else
            {
                var labeled = new StackPanel();
                labeled.Children.Add(new TextBlock { Text = item.Header, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 3) });
                labeled.Children.Add(item.Content);
                mi.Header = labeled;
            }

            // Purely a content host - no Command bound, so there's nothing for a stray click
            // inside it to invoke. Dragging the slider itself doesn't close the menu because
            // Slider's Thumb captures and marks its own mouse-down handled before that routed
            // event ever reaches this MenuItem's own (unhandled-only) click detection.
            mi.Focusable = false;
        }
        else
        {
            mi.Header           = item.Header;
            mi.Command          = item.Command;
            mi.CommandParameter = item.CommandParameter;
        }

        return mi;
    }

    private static Separator BuildSeparator() => new() { Background = PinkBorder, Margin = new Thickness(4, 2, 4, 2) };

    /// <summary>
    ///     A deliberately simple pink chrome - a rounded border that darkens on hover - rather
    ///     than the default theme's MenuItem template. Flat/single-row by design: design-menu
    ///     items don't need icons, check glyphs, shortcut-text columns, or submenus, so
    ///     replicating those parts of the stock template would only be visual noise here.
    /// </summary>
    private static readonly ControlTemplate PinkMenuItemTemplate = BuildPinkMenuItemTemplate();

    private static ControlTemplate BuildPinkMenuItemTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bg";
        border.SetValue(Border.BackgroundProperty, PinkFill);
        border.SetValue(Border.BorderBrushProperty, PinkBorder);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 1, 2, 1));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        presenter.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 6, 10, 6));
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(MenuItem)) { VisualTree = border };

        var hover = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, PinkFillHover) { TargetName = "Bg" });
        template.Triggers.Add(hover);

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.5));
        template.Triggers.Add(disabled);

        return template;
    }
}

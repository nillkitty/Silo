using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Silo.Design;

namespace Silo.Commanding;

/// <summary>
///     Hold Ctrl+Shift and hover over anything: a pink tip says what is under the mouse (a header with its icon, its name and its
///     type, and the element and model behind it) and lists what its right-click menu is built from -- the target, each object asked
///     for commands, the model, and every type checked for registered providers (see <see cref="ContextCommands.ExplainParts" />).
///     Releasing either key hides it.
/// </summary>
/// <remarks>Throughout the tip, type names are in italics and names in bold.</remarks>
public static class ContextMenuInspector
{
    private const double HeaderIconSize = 36;

    private static readonly FontFamily Font = new("Segoe UI");

    private static readonly SolidColorBrush MutedText = Freeze(Color.FromArgb(0xB0, 0x5A, 0x0A, 0x38));
    private static readonly SolidColorBrush GoodText  = Freeze(Color.FromRgb(0x1B, 0x7A, 0x3A));

    private static Popup?            _popup;
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

        UIElement content;
        try
        {
            content = Build(over);
        }
        catch (Exception ex)
        {
            content = new TextBlock { Text = "Could not work out the context menu: " + ex.Message, FontFamily = Font, Foreground = DesignMenu.PinkText };
        }

        Show(over, content);
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

    // ---- what the tip shows --------------------------------------------------------------------------------------

    private static UIElement Build(DependencyObject over)
    {
        var panel = new StackPanel { MaxWidth = 640 };
        panel.Children.Add(BuildHeader(over));
        panel.Children.Add(new Border { Height = 1, Background = DesignMenu.PinkBorder, Margin = new Thickness(0, 8, 0, 6) });
        panel.Children.Add(BuildBody(over));
        return panel;
    }

    /// <summary>
    ///     The header: what is pointed at -- the first thing behind the element that is not itself an element (an Explorer node, a
    ///     navigation item, a model), else the element -- with its icon, its name in bold and its type in italics; below, the element it
    ///     is shown by and the model it stands for, when those are other objects.
    /// </summary>
    private static UIElement BuildHeader(DependencyObject over)
    {
        var    host    = ContextCommands.HostOf(over);
        object target  = host is null ? over : ContextCommands.TargetFrom(host, over);
        var    chain   = ContextCommands.Chain(target);
        object subject = chain.FirstOrDefault(c => c is not Visual and not FrameworkContentElement) ?? target;
        object? model  = chain.LastOrDefault() is { } last && !ReferenceEquals(last, subject) && last is not Visual ? last : null;

        // the subject's own icon, else the model's own, else one picked from the subject's type
        object iconSource = TypeIcons.OwnIcon(subject) is null && model is not null && TypeIcons.OwnIcon(model) is not null ? model : subject;
        var icon = TypeIcons.IconFor(iconSource, HeaderIconSize, DesignMenu.PinkText);
        if (icon is FrameworkElement fe)
        {
            fe.Margin            = new Thickness(0, 2, 10, 0);
            fe.VerticalAlignment = VerticalAlignment.Top;
        }

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        // Name  Type · kind
        var title = new TextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = Font, Foreground = DesignMenu.PinkText };
        string? name = TypeIcons.NameOf(subject);
        if (name is not null)
        {
            title.Inlines.Add(new Run(name) { FontWeight = FontWeights.Bold, FontSize = 19 });
            title.Inlines.Add(new Run("  "));
        }

        title.Inlines.Add(new Run(ContextCommands.DisplayTypeName(subject.GetType()))
                          {
                              FontStyle = FontStyles.Italic, FontSize = name is null ? 19 : 15, FontWeight = name is null ? FontWeights.SemiBold : FontWeights.Normal
                          });
        if (TypeIcons.KindOf(subject) is { } kind) title.Inlines.Add(new Run("  · " + kind) { FontSize = 13, Foreground = MutedText });
        text.Children.Add(title);

        // shown by: the element under the mouse, when it is not the subject
        if (!ReferenceEquals(subject, target))
            text.Children.Add(SubLine("shown by ", target, host is not null && !ReferenceEquals(host, target) ? host : null));

        // stands for: the model, when it is another object
        if (model is not null) text.Children.Add(SubLine("model ", model, null));

        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(text);
        return row;
    }

    /// <summary>A small line under the title: a label, an object (its type in italics, its name in bold) and, optionally, where it sits.</summary>
    private static TextBlock SubLine(string label, object what, object? within)
    {
        var line = new TextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = Font, FontSize = 12.5, Foreground = DesignMenu.PinkText, Margin = new Thickness(0, 2, 0, 0) };
        line.Inlines.Add(new Run(label) { Foreground = MutedText });
        AddObject(line, what);
        if (within is not null)
        {
            line.Inlines.Add(new Run(" in ") { Foreground = MutedText });
            AddObject(line, within);
        }

        return line;
    }

    private static void AddObject(TextBlock line, object o)
    {
        line.Inlines.Add(new Run(ContextCommands.DisplayTypeName(o.GetType())) { FontStyle = FontStyles.Italic });
        if (TypeIcons.NameOf(o) is { } name) line.Inlines.Add(new Run(" " + name) { FontWeight = FontWeights.Bold });
    }

    /// <summary>The explanation of the right-click menu, each piece styled by what it is.</summary>
    private static UIElement BuildBody(DependencyObject over)
    {
        var body = new TextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = Font, FontSize = 12.5, Foreground = DesignMenu.PinkText, LineHeight = 18 };
        var lines = ContextCommands.ExplainParts(over);
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0) body.Inlines.Add(new LineBreak());
            foreach (var part in lines[i]) body.Inlines.Add(Styled(part));
        }

        return body;
    }

    private static Run Styled(ExplainPart part)
    {
        var run = new Run(part.Text);
        switch (part.Style)
        {
            case ExplainStyle.Type:
                run.FontStyle = FontStyles.Italic;
                break;
            case ExplainStyle.Name:
                run.FontWeight = FontWeights.Bold;
                break;
            case ExplainStyle.Label:
                run.FontWeight = FontWeights.SemiBold;
                break;
            case ExplainStyle.Heading:
                run.FontWeight = FontWeights.Bold;
                run.FontSize   = 13.5;
                break;
            case ExplainStyle.Muted:
                run.Foreground = MutedText;
                break;
            case ExplainStyle.Good:
                run.Foreground = GoodText;
                run.FontWeight = FontWeights.Bold;
                break;
        }

        return run;
    }

    // ---- the popup -----------------------------------------------------------------------------------------------

    private static void Show(DependencyObject over, UIElement content)
    {
        _popup ??= new Popup
                   {
                       AllowsTransparency = true,
                       Placement          = PlacementMode.Mouse,
                       HorizontalOffset   = 16,
                       VerticalOffset     = 16,
                       IsHitTestVisible   = false
                   };

        _popup.Child = new Border
                       {
                           Background       = DesignMenu.PinkFill,
                           BorderBrush      = DesignMenu.PinkBorder,
                           BorderThickness  = new Thickness(1.5),
                           CornerRadius     = new CornerRadius(5),
                           Padding          = new Thickness(12, 10, 12, 10),
                           Child            = content,
                           IsHitTestVisible = false
                       };

        _shownFor     = over;
        _popup.IsOpen = false; // reopening places it at the mouse again
        _popup.IsOpen = true;
    }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

using System.Windows.Controls;
using System.Windows.Media;

namespace Silo.Diagnostics;

/// <summary>
///     Adds the model's type name to the control
/// </summary>
public record TypeDebugAdorner : DebugAdornerBase<object>
{
    protected override Control? OnGetControl(AdornContext ctx)
    {
        if (ctx.Model is null) return null;

        var type = ctx.Model.GetType();
        return ctx.Text(type.FullName ?? type.Name);
    }
}

/// <summary>
///     What an adorner is shown for: the model being adorned, the control it is shown on (when there is one) and the
///     <see cref="DebugContext" />, with the primitives an adorner builds its content from.
/// </summary>
/// <param name="d">The debug context the adornment belongs to.</param>
/// <param name="model">The object being adorned.</param>
/// <param name="control">The control the object is shown by, if any; its size limits the adornment.</param>
public class AdornContext(DebugContext d, object? model = null, Control? control = null)
{
    public Control?     Control      { get; } = control;
    public DebugContext DebugContext { get; } = d.Required();
    public object?      Model        { get; } = model;

    /// <summary>A line of text, in the style every adornment shares (<see cref="AdornStyle" />).</summary>
    public TextBox Text(string text)
    {
        var box = new TextBox { Text = text };
        AdornStyle.Apply(box);

        // never wider or taller than what it is on (when that has a size yet)
        if (Control is { ActualWidth: > 0 } c) box.MaxWidth = c.ActualWidth;
        if (Control is { ActualHeight: > 0 } h) box.MaxHeight = h.ActualHeight;
        return box;
    }

    // add other primitives for creating adornments of icons, values, binary output
}

/// <summary>How a debug, development or author adornment looks, so they all look alike.</summary>
public static class AdornStyle
{
    public static Brush Background { get; } = Brushes.Pink;

    public static Brush Foreground { get; } = Brushes.Black;

    public const double FontSize = 10;

    public static void Apply(TextBox box)
    {
        box.IsReadOnly          = true;
        box.Background          = Background;
        box.Foreground          = Foreground;
        box.FontSize            = FontSize;
        box.Margin              = new Thickness(0);
        box.Padding             = new Thickness(1);
        box.BorderThickness     = new Thickness(0);
        box.HorizontalAlignment = HorizontalAlignment.Left;
        box.VerticalAlignment   = VerticalAlignment.Top;

        // Placing it over the window is the adornment manager's job (IAdornmentManager.Init); nothing does that yet.
    }
}

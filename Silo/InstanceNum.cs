using System.Windows.Markup;

namespace Silo;

/// <summary>
///     Names a <see cref="Capture" /> after the element numbers of its enclosing <see cref="Split" />s and
///     <see cref="Lines" />. <c>{silo:InstanceNum}</c> is the innermost element number; <c>{silo:InstanceNum 'Col[{0}]'}</c>
///     is a template where <c>{n}</c> is the element number n repeaters out (0 = innermost; the outermost is the match
///     number).
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public class InstanceNum : MarkupExtension
{
    public InstanceNum() : this(0)
    {
    }

    public InstanceNum(string template)
    {
        Template = template;
    }

    public InstanceNum(int depth)
    {
        Template = Of(depth);
    }

    /// <summary>
    ///     The capture name template, e.g. <c>Col[{0}]</c>
    /// </summary>
    [Parameter]
    [ConstructorArgument("template")]
    public string Template { get; set; }

    /// <summary>
    ///     The template for the element number <paramref name="depth" /> repeaters out.
    /// </summary>
    public static string Of(int depth)
    {
        return "{" + depth + "}";
    }

    /// <summary>
    ///     Returns the template; numbers are filled in each time the capture matches.
    /// </summary>
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return Template;
    }
}

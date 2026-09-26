using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace Silo;

/// <summary>
///     Renders an icon <see cref="Geometry" /> (typically one of the <c>StreamGeometry</c>
///     resources merged in from <c>Resources/Paths.xaml</c>) at a fixed size, filled/stroked
///     with the control's own <see cref="Control.Foreground" /> - which, being the ordinary
///     inherited WPF <c>Foreground</c> property, automatically tracks whatever color its
///     container is using (a menu item, a nav item, a button...) with no manual
///     ancestor-lookup binding required, the same way text does.
/// </summary>
/// <remarks>
///     This replaces an earlier <c>AppIconExtension : StaticResourceExtension</c> markup
///     extension. That approach had three problems: (1) Visual Studio's XAML editor only
///     offers the "pick a resource key" IntelliSense dropdown for the literal
///     <c>StaticResource</c>/<c>DynamicResource</c> extensions - it's special-cased by name in
///     the tooling, not by inheritance, so a <c>StaticResourceExtension</c> subclass never gets
///     it. (2) Its color came from a hardcoded <c>Brushes.White</c> fallback plus a binding to
///     one specific ancestor type's <c>Foreground</c> - correct nowhere except by coincidence,
///     and specifically broken now that the app has a real Light/Dark theme (a white icon is
///     invisible in Light mode). (3) A <c>MarkupExtension.ProvideValue</c> override builds a
///     plain object graph once, in code, at parse time - that's opaque to XAML Hot
///     Reload/Live Visual Tree editing, which patches declaratively-authored XAML (styles,
///     templates, property values), not arbitrary C# run once during parsing. Making this a
///     real templated <see cref="Control" /> (default template in <c>App.xaml</c>) fixes all
///     three: the icon is chosen with a plain <c>{StaticResource ...}</c> (full IntelliSense),
///     its color is the ordinary inherited <c>Foreground</c> (no hardcoded fallback), and the
///     template itself is real XAML that Hot Reload can patch live.
/// </remarks>
/// <example>
///     <code>
///     &lt;l:AppIcon Icon="{StaticResource HardDrive}" /&gt;
///     </code>
/// </example>
public class AppIcon : Control
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(Geometry), typeof(AppIcon));

    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(nameof(Size), typeof(double), typeof(AppIcon), new FrameworkPropertyMetadata(16d));

    /// <summary>
    ///     Stroke width applied on top of the fill, using the same Foreground brush. Defaults
    ///     to 0 (no stroke): the icons currently in <c>Resources/Paths.xaml</c> are solid,
    ///     filled glyphs (closed-path, even-odd fill), not line-art - stroking them adds an
    ///     outline around already-filled edges, which just bloats/blurs fine detail at small
    ///     sizes. Set this explicitly per-icon if a genuinely stroke-style icon is added later.
    /// </summary>
    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(AppIcon), new FrameworkPropertyMetadata(0d));

    public Geometry? Icon
    {
        get => (Geometry?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    static AppIcon()
    {
        // Falls back to searching for an implicit Style keyed off this type (see App.xaml)
        // before ever consulting a Generic.xaml theme dictionary - we don't have/need one.
        DefaultStyleKeyProperty.OverrideMetadata(typeof(AppIcon), new FrameworkPropertyMetadata(typeof(AppIcon)));
    }
}

/// <summary>
///     One-line markup-extension form of <see cref="AppIcon" />, for places (like an
///     <c>Icon="..."</c> attribute) where element/property-element syntax would spread a
///     single nav item across several lines. <c>ProvideValue</c> just constructs an
///     <see cref="AppIcon" /> instance, so it's the exact same templated visual - same
///     inherited-Foreground coloring, same App.xaml-defined template - as writing the
///     element out by hand; this only saves the line breaks.
/// </summary>
/// <example>
///     Positional (icon only):
///     <code>
///     Icon="{l:AppIcon {StaticResource HardDrive}}"
///     </code>
///     Named (to also set Size/StrokeThickness):
///     <code>
///     Icon="{l:AppIcon Icon={StaticResource HardDrive}, Size=20}"
///     </code>
/// </example>
[MarkupExtensionReturnType(typeof(AppIcon))]
public class AppIconExtension : MarkupExtension
{
    public Geometry? Icon { get; set; }

    /// <summary>
    ///     Per-instance size override. Left <see langword="null"/> (the default) unless the
    ///     markup explicitly sets it, so the constructed <see cref="AppIcon"/> gets no local
    ///     <see cref="AppIcon.Size"/> value at all and falls through to the app-wide,
    ///     runtime-adjustable default from the implicit <c>AppIcon</c> style in
    ///     <c>App.xaml</c> (bound to <see cref="Silo.Design.AppDesignSettings.IconSize"/>).
    ///     Setting this explicitly still wins, same as any local value beats a Style setter.
    /// </summary>
    public double? Size { get; set; }

    public double StrokeThickness { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var icon = new AppIcon
                   {
                       Icon            = Icon,
                       StrokeThickness = StrokeThickness
                   };

        if (Size.HasValue) icon.Size = Size.Value;

        return icon;
    }

    public AppIconExtension()
    {
    }

    public AppIconExtension(Geometry? icon)
    {
        Icon = icon;
    }
}
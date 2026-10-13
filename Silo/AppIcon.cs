using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace Silo;

/// <summary>
///     Lets <see cref="AppIcon.Icon" />/<see cref="AppIconExtension.Icon" /> be set from a
///     plain string key instead of a full <c>{StaticResource ...}</c> - e.g.
///     <c>Icon="folder17"</c> instead of <c>Icon="{StaticResource Folder17}"</c>. Tries, in
///     order: the key exactly as given; the key with just its first letter capitalized
///     (covers the common case of typing an all-lowercase key against
///     <c>Resources/Paths.xaml</c>'s PascalCase keys, e.g. "folder17" -&gt; "Folder17"); and,
///     for a hyphen/underscore-separated key, each segment capitalized and joined (e.g.
///     "hard-drive" -&gt; "HardDrive"). Deliberately does NOT fall back to null/empty on a
///     miss - it throws, so a typo shows up immediately as an error instead of a silently
///     blank icon (the exact failure mode the original <c>AppIconExtension</c> had for
///     "hard-drive" vs. the real key "HardDrive" - see AppIcon's remarks above).
/// </summary>
public sealed class IconKeyConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
        sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        if (value is not string key || string.IsNullOrWhiteSpace(key))
            return base.ConvertFrom(context, culture, value)!;

        var tried = _candidates(key).ToList();
        foreach (var candidate in tried)
            if (Application.Current?.TryFindResource(candidate) is Geometry g)
                return g;

        throw new InvalidOperationException(
            $"AppIcon: no Geometry resource found for icon key '{key}' (tried: {string.Join(", ", tried)}).");
    }

    private static IEnumerable<string> _candidates(string key)
    {
        yield return key;

        yield return char.ToUpperInvariant(key[0]) + key[1..];

        if (key.Contains('-') || key.Contains('_'))
        {
            var parts = key.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries);
            yield return string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        }
    }
}

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
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(nameof(Icon), typeof(Geometry), typeof(AppIcon));

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

    static AppIcon()
    {
        // Falls back to searching for an implicit Style keyed off this type (see App.xaml)
        // before ever consulting a Generic.xaml theme dictionary - we don't have/need one.
        DefaultStyleKeyProperty.OverrideMetadata(typeof(AppIcon), new FrameworkPropertyMetadata(typeof(AppIcon)));
    }

    [TypeConverter(typeof(IconKeyConverter))]
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
///     Positional, by plain string key (shortest form - goes through
///     <see cref="IconKeyConverter" />, so "folder17" resolves to the "Folder17" resource):
///     <code>
///     Icon="{l:AppIcon folder17}"
///     </code>
///     Positional, by an explicit resource lookup:
///     <code>
///     Icon="{l:AppIcon {StaticResource HardDrive}}"
///     </code>
///     Named (to also set Size/StrokeThickness):
///     <code>
///     Icon="{l:AppIcon Icon=folder17, Size=20}"
///     </code>
/// </example>
[MarkupExtensionReturnType(typeof(AppIcon))]
public class AppIconExtension : MarkupExtension
{
    [TypeConverter(typeof(IconKeyConverter))]
    public Geometry? Icon { get; set; }

    /// <summary>
    ///     Null (the default) leaves <see cref="AppIcon.Size" /> unset, so the icon follows the live
    ///     <c>AppDesignSettings.IconSize</c> via the style in App.xaml; a value pins this icon to that size.
    /// </summary>
    public double? Size { get; set; }

    public double StrokeThickness { get; set; }

    public AppIconExtension()
    {
    }

    public AppIconExtension(Geometry? icon)
    {
        Icon = icon;
    }

    public AppIconExtension(string iconKey)
    {
        Icon = (Geometry)new IconKeyConverter().ConvertFrom(null!, null, iconKey)!;
    }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var icon = new AppIcon
                   {
                       Icon            = Icon,
                       StrokeThickness = StrokeThickness
                   };

        // Only a local value when asked for: a local Size would beat the style's live IconSize binding.
        if (Size is { } size)
            icon.Size = size;

        return icon;
    }
}

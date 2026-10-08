using System.Windows;
using System.Windows.Controls;

namespace Silo.Views;

/// <summary>
///     A plain "this page exists in the navigation structure but doesn't have a real view yet"
///     stand-in, used as the <c>ContentBuilder</c> for every nav leaf the app doesn't have a
///     real feature behind yet (Search, Universe, Monitoring, Profile, ...). Built in code
///     rather than XAML -- there's nothing here a designer surface would help with, and it keeps
///     this first pass of the nav-to-view wiring free of new XAML files to get wrong without a
///     compiler on hand to catch it.
/// </summary>
public class PlaceholderView : UserControl
{
    public PlaceholderView(string title, string? description = null)
    {
        var stack = new StackPanel
        {
            Margin              = new Thickness(24),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment   = VerticalAlignment.Top
        };

        stack.Children.Add(new TextBlock
        {
            Text       = title,
            FontSize   = 20,
            FontWeight = FontWeights.SemiBold,
            Margin     = new Thickness(0, 0, 0, 8)
        });

        stack.Children.Add(new TextBlock
        {
            Text         = description ?? "This page is part of the navigation structure, but doesn't have a real view yet.",
            Opacity      = 0.7,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth     = 480
        });

        Content = stack;
    }
}

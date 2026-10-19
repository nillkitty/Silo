using System.Linq;
using System.Windows.Controls;

namespace Silo.Explorer;

/// <summary>Builds the views shown in a document tab for the node selected in the Explorer.</summary>
public static class NodeViews
{
    /// <summary>The most children listed in a generic view.</summary>
    private const int ChildLimit = 500;

    /// <summary>
    ///     The generic view of a node: its name and kind, its details and, if it has any, what is under it. Used for every
    ///     node that has no view of its own.
    /// </summary>
    public static UIElement Build(ExplorerNode node)
    {
        var body = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
        body.Children.Add(new TextBlock { Text = node.Display, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(Muted(node.Kind));

        IReadOnlyList<(string Name, string Value)> rows = [];
        try
        {
            rows = node.Details?.Invoke().ToList() ?? [];
        }
        catch (Exception ex)
        {
            body.Children.Add(Muted("Could not read the details: " + ex.Message));
        }

        if (rows.Count > 0) body.Children.Add(DetailsGrid(rows, new Thickness(0, 12, 0, 0)));

        var children = node.Children.Where(c => !c.IsHidden).ToList();
        if (children.Count > 0)
        {
            body.Children.Add(new TextBlock { Text = "Contains", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 4) });
            foreach (var child in children.Take(ChildLimit))
                body.Children.Add(new TextBlock { Text = child.Display + "  ", Margin = new Thickness(0, 1, 0, 1)});
            if (children.Count > ChildLimit) body.Children.Add(Muted($"...and {children.Count - ChildLimit:N0} more"));
        }

        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body };
    }

    /// <summary>A two-column label / value grid (the Properties pane and the generic view use it).</summary>
    public static Grid DetailsGrid(IReadOnlyList<(string Name, string Value)> rows, Thickness margin)
    {
        var grid = new Grid { Margin = margin };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name  = Muted(rows[i].Name);
            name.Margin = new Thickness(0, 2, 12, 2);
            FrameworkElement value = rows[i].Value.Contains('\n')
                                         ? MultilineValue(rows[i].Value)
                                         : new TextBlock { Text = rows[i].Value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
            Grid.SetRow(name, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(name);
            grid.Children.Add(value);
        }

        return grid;
    }

    /// <summary>
    ///     A value of several lines (a stack trace, say): shown as read-only text in a fixed-width font that can be selected and copied.
    /// </summary>
    private static TextBox MultilineValue(string text)
    {
        var box = new TextBox
                  {
                      Text                      = text,
                      IsReadOnly                = true,
                      TextWrapping              = TextWrapping.NoWrap,
                      BorderThickness           = new Thickness(0),
                      Background                = System.Windows.Media.Brushes.Transparent,
                      FontFamily                = new System.Windows.Media.FontFamily("Consolas"),
                      FontSize                  = 12,
                      Margin                    = new Thickness(0, 2, 0, 2),
                      Padding                   = new Thickness(0),
                      MaxHeight                 = 360,
                      VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                      HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
                  };
        box.SetResourceReference(Control.ForegroundProperty, "Theme.Text");
        return box;
    }

    public static TextBlock Muted(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        return t;
    }
}

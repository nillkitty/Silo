using System.Windows.Controls;
using Silo.Converters;
using Silo.Design;

namespace Silo.Explorer;

/// <summary>The Properties pane: name, kind and details of whatever is selected in the Explorer tree.</summary>
public sealed class PropertiesView : UserControl
{
    /// <summary>The size of the icon in the heading (the tree's own is the person's "Icon Size" setting, 16 by default).</summary>
    private const double IconSize = 40;

    private readonly StackPanel _body = new() { Margin = new Thickness(12) };

    public void Show(ExplorerNode? node)
    {
        _body.Children.Clear();
        if (node is null)
        {
            _body.Children.Add(Muted("Select something in the Explorer to see its properties."));
            return;
        }

        _body.Children.Add(Heading(node));

        IReadOnlyList<(string Name, string Value)> rows = [];
        try
        {
            rows = node.Details?.Invoke().ToList() ?? [];
        }
        catch (Exception ex)
        {
            _body.Children.Add(Muted("Could not read the details: " + ex.Message));
        }

        if (rows.Count == 0) return;

        var grid = NodeViews.DetailsGrid(rows, new Thickness(0, 10, 0, 0));
        _body.Children.Add(grid);
    }

    /// <summary>The node's icon, large, beside its name and what kind of thing it is.</summary>
    private static UIElement Heading(ExplorerNode node)
    {
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock
                           {
                               Text         = node.Header, FontSize = 16, FontWeight = FontWeights.SemiBold,
                               TextWrapping = TextWrapping.Wrap
                           });
        names.Children.Add(Muted(node.Kind));

        if (node.Icon is null) return names;

        // the same icon the tree shows, at a fixed larger size, with the realm badge on its corner as in the tree
        var picture = new Grid { Margin         = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        picture.Children.Add(new AppIcon { Icon = node.Icon, Size                               = IconSize });
        if (node.Realm != Realm.None)
            picture.Children.Add(new RealmBadge
                                 {
                                     Realm               = node.Realm,
                                     WorldKey            = node.WorldKey,
                                     Size                = Math.Max(9, Math.Round(IconSize * 0.6)),
                                     HorizontalAlignment = HorizontalAlignment.Right,
                                     VerticalAlignment   = VerticalAlignment.Bottom,
                                     Margin              = new Thickness(0, 0, -4, -4)
                                 });

        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(picture, Dock.Left);
        row.Children.Add(picture);
        row.Children.Add(names);
        return row;
    }

    private static TextBlock Muted(string text)
    {
        return NodeViews.Muted(text);
    }

    public PropertiesView()
    {
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _body };
        Show(null);
    }
}
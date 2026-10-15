using System.Windows.Controls;

namespace Silo.Explorer;

/// <summary>The Properties pane: name, kind and details of whatever is selected in the Explorer tree.</summary>
public sealed class PropertiesView : UserControl
{
    private readonly StackPanel _body = new() { Margin = new Thickness(12) };

    public PropertiesView()
    {
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _body };
        Show(null);
    }

    public void Show(ExplorerNode? node)
    {
        _body.Children.Clear();
        if (node is null)
        {
            _body.Children.Add(Muted("Select something in the Explorer to see its properties."));
            return;
        }

        _body.Children.Add(new TextBlock { Text = node.Header, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        _body.Children.Add(Muted(node.Kind));

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

        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name  = Muted(rows[i].Name);
            name.Margin = new Thickness(0, 2, 12, 2);
            var value = new TextBlock { Text = rows[i].Value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
            Grid.SetRow(name, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(name);
            grid.Children.Add(value);
        }

        _body.Children.Add(grid);
    }

    private static TextBlock Muted(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        return t;
    }
}

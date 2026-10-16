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

        var grid = NodeViews.DetailsGrid(rows, new Thickness(0, 10, 0, 0));
        _body.Children.Add(grid);
    }

    private static TextBlock Muted(string text) => NodeViews.Muted(text);
}

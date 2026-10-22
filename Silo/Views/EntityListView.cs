using System.Windows.Controls;
using Silo.Providers.Data.Source;

namespace Silo.Views;

/// <summary>
///     A generic stand-in for every "ListView of X" box in the navigation structure diagram --
///     worlds, data sources, meta types/fields/indexes, connection providers/destinations/
///     credentials. One reusable control instead of nine near-identical ones: give it the
///     entity's name and, once there's a real place to read it from, column headers; until then
///     it just renders an empty, correctly-labeled <see cref="ListView" /> rather than
///     fabricated columns or data.
/// </summary>
/// <remarks>
///     None of the places this is used from <c>AppNavTree.cs</c> pass <paramref name="columnNames" />
///     yet -- wiring each one to its real data source (an open Silo's <c>World</c> table, a
///     document's <see cref="IFlatDataProvider.GetDataSources" />, the
///     Connections subsystem's provider/destination/credential lists, ...) is its own piece of
///     work, deliberately left for later rather than guessed at here.
/// </remarks>
public class EntityListView : UserControl
{
    public EntityListView(string entityName, IReadOnlyList<string>? columnNames = null)
    {
        var root = new DockPanel();

        var header = new TextBlock
                     {
                         Text       = entityName,
                         FontSize   = 18,
                         FontWeight = FontWeights.SemiBold,
                         Margin     = new Thickness(12, 12, 12, 8)
                     };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var listView = new ListView { Margin = new Thickness(12, 0, 12, 12) };
        var gridView = new GridView();
        listView.View = gridView;

        if (columnNames is { Count: > 0 })
            foreach (var column in columnNames)
                gridView.Columns.Add(new GridViewColumn { Header = column, Width = 140 });
        else
            gridView.Columns.Add(new GridViewColumn { Header = "(not yet wired to a data source)", Width = 260 });

        root.Children.Add(listView);

        Content = root;
    }
}
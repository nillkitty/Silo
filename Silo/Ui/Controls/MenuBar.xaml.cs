using System.Windows;
using System.Windows.Controls;

namespace Silo.Ui;

/// <summary>
/// Interaction logic for MenuBar.xaml
/// </summary>
public partial class MenuBar : Menu
{
    public MenuBar()
    {
        InitializeComponent();
    }

    private MainWindow? Main => Window.GetWindow(this) as MainWindow;

    /// <summary>Refreshes the pane check marks from what is actually showing (a pane can be closed with its own X).</summary>
    private void ViewMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        // nested submenus (Theme, User Level) raise this too, and it bubbles up here
        if (!ReferenceEquals(e.OriginalSource, sender) || Main is not { } main)
            return;

        ViewToolbox.IsChecked      = main.IsToolboxOpen;
        ViewSelectedItem.IsChecked = main.IsSelectedItemOpen;
    }

    private void ViewPane_Click(object sender, RoutedEventArgs e)
    {
        if (Main is not { } main || sender is not MenuItem item)
            return;

        // IsCheckable has already flipped the check mark: it now says what the person asked for
        if (ReferenceEquals(item, ViewToolbox))
            main.SetToolboxOpen(item.IsChecked);
        else if (ReferenceEquals(item, ViewSelectedItem))
            main.SetSelectedItemOpen(item.IsChecked);
    }
}
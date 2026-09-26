using System.Windows;
using Silo.Design;

namespace Silo;

/// <summary>
///     Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public App App => App.Instance!;

    public MainWindow()
    {
        InitializeComponent();

        // Ctrl+right-click the nav tree (in Author mode or above) for a pink design
        // menu with a live "Icon Size" slider - see Design/DesignMenu.cs.
        DesignMenu.EnsureItems(NavTree).Add(DesignMenuItem.Slider(
            "Icon Size", DesignLevel.Author,
            min: 12, max: 64,
            getValue: () => AppDesignSettings.Instance.IconSize,
            setValue: v => AppDesignSettings.Instance.IconSize = v,
            format: "{0:0} px"));
    }
}
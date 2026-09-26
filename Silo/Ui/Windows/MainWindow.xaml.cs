using System.Windows;

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
    }
}
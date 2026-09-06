using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Silo.Connectors;

namespace Silo.Ui.Windows;

/// <summary>
/// Interaction logic for ConectionWindow.xaml
/// </summary>
public partial class ConnectionWindow : Window
{
    public ConnectionWindow()
    {
        InitializeComponent();
    }

    public IConnection? Connection { get; set; }

    public static IConnection? Modal()
    {
        var c = new ConnectionWindow();
        if (c.ShowDialog() is true)
            return c.Connection;

        return null;
    }
}
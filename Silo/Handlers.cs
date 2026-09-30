using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using Silo.Extensions;

namespace Silo;

internal static class Handlers
{
    public static void CancelButton(object sender, RoutedEventArgs e)
    {
        if (sender is UIElement c)
        {
            Window? w = c.ParentOfType<Window>();
            if (w != null)
            {
                w.DialogResult = false;
                w.Close();
            }
        }
    }

    public static void OkButton(object sender, RoutedEventArgs e)
    {
        if (sender is UIElement c)
        {
            Window? w = c.ParentOfType<Window>();
            if (w != null)
            {
                w.DialogResult = true;
                w.Close();
            }
        }
    }
}

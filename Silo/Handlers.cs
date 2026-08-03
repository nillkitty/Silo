using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using Telerik.Windows.Controls;

namespace Silo;

internal static class Handlers
{
    public static void CancelButton(object sender, RoutedEventArgs e)
    {
        if (sender is UIElement c)
        {
            Window    w = c.ParentOfType<Window>();
            RadWindow r = c.ParentOfType<RadWindow>();
            if (w != null)
            {
                w.DialogResult = false;
                w.Close();
            }
            else if (r != null)
            {
                r.DialogResult = false;
                r.Close();
            }
        }
    }

    public static void OkButton(object sender, RoutedEventArgs e)
    {
        if (sender is UIElement c)
        {
            Window    w = c.ParentOfType<Window>();
            RadWindow r = c.ParentOfType<RadWindow>();
            if (w != null)
            {
                w.DialogResult = true;
                w.Close();
            }
            else if (r != null)
            {
                r.DialogResult = true;
                r.Close();
            }
        }
    }
}
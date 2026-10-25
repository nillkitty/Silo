using Silo.Extensions;

namespace Silo.ViewModel;

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
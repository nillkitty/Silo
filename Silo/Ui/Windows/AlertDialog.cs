using System.Windows.Controls;
using System.Windows.Threading;

namespace Silo.Ui.Windows;

/// <summary>
///     A message box whose text can be selected and copied: the message sits in a read-only text box (select and Ctrl+C, or
///     press Copy for all of it). Shown by <see cref="App.Alert(string, string?)" />.
/// </summary>
public static class AlertDialog
{
    /// <summary>Shows the message in a modal window and returns when it is closed. Call it on the UI thread.</summary>
    public static void Show(string message, string? title = null)
    {
        var window = new Window
                     {
                         Title                 = title ?? AppIdentity.AppName,
                         Width                 = 520,
                         MinWidth              = 320,
                         MaxWidth              = 760,
                         MaxHeight             = 560,
                         SizeToContent         = SizeToContent.Height,
                         ShowInTaskbar         = false,
                         WindowStartupLocation = WindowStartupLocation.CenterOwner,
                         Owner                 = Application.Current?.MainWindow is { IsVisible: true } w ? w : null
                     };
        if (window.Owner is null) window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var text = new TextBox
                   {
                       Text                          = message,
                       IsReadOnly                    = true,
                       IsReadOnlyCaretVisible        = true,
                       TextWrapping                  = TextWrapping.Wrap,
                       BorderThickness               = new Thickness(0),
                       Background                    = System.Windows.Media.Brushes.Transparent,
                       VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                       Margin                        = new Thickness(0, 0, 0, 14)
                   };

        var copy = new Button { Content = "_Copy", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0) };
        var ok   = new Button { Content = "OK", Padding = new Thickness(14, 4, 14, 4), IsDefault = true, IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) => window.DialogResult = true;
        copy.Click += (_, _) =>
                      {
                          if (!TryCopy(message)) return;

                          copy.Content = "Copied";
                          var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                          timer.Tick += (_, _) =>
                                        {
                                            timer.Stop();
                                            copy.Content = "_Copy";
                                        };
                          timer.Start();
                      };

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        row.Children.Add(copy);
        row.Children.Add(ok);

        var panel = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(row, Dock.Bottom);
        panel.Children.Add(row);
        panel.Children.Add(text);
        window.Content = panel;

        window.Loaded += (_, _) => ok.Focus();
        window.ShowDialog();
    }

    /// <summary>Puts the text on the clipboard; false if another program has the clipboard locked.</summary>
    private static bool TryCopy(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (global::System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }
}

using System.Security.Cryptography.X509Certificates;
using System.Windows.Controls;
using System.Windows.Media;

namespace Silo.Certificates;

internal enum FieldKind
{
    Text,
    Password,
    Multiline,
    Choice,
    Check
}

/// <summary>One line of a <see cref="CertificateDialogs.Form" />.</summary>
/// <param name="Id">What the value is returned under.</param>
/// <param name="Initial">The starting text; for a check box "true" or "false"; for a choice the starting choice.</param>
internal sealed record FormField(string Id, string Label, FieldKind Kind = FieldKind.Text, string Initial = "", IReadOnlyList<string>? Choices = null, string? Hint = null);

/// <summary>The small code-built windows the certificate commands use: a form, a text result, and a picker for the Windows certificate stores.</summary>
internal static class CertificateDialogs
{
    private static Window? Owner()
    {
        var app = Application.Current;
        if (app is null) return null;

        foreach (Window w in app.Windows)
            if (w.IsActive && w.IsVisible)
                return w;

        return app.MainWindow is { IsVisible: true } main ? main : null;
    }

    private static Window NewWindow(string title, double width)
    {
        var window = new Window
                     {
                         Title                 = title,
                         Width                 = width,
                         SizeToContent         = SizeToContent.Height,
                         MaxHeight             = 760,
                         ShowInTaskbar         = false,
                         ResizeMode            = ResizeMode.CanResizeWithGrip,
                         WindowStartupLocation = WindowStartupLocation.CenterOwner,
                         Owner                 = Owner()
                     };
        if (window.Owner is null) window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        return window;
    }

    private static Button PushButton(string text, bool isDefault = false, bool isCancel = false)
    {
        return new Button { Content = text, Padding = new Thickness(14, 4, 14, 4), MinWidth = 84, Margin = new Thickness(8, 0, 0, 0), IsDefault = isDefault, IsCancel = isCancel };
    }

    // ---- a form ----------------------------------------------------------------------------------------------

    /// <summary>
    ///     Asks for the fields. Returns their values by id (check boxes as "true" / "false"), or null when cancelled. <paramref name="validate" />
    ///     may refuse the values with a message, which is shown and keeps the window open.
    /// </summary>
    public static Dictionary<string, string>? Form(string title, string? intro, IReadOnlyList<FormField> fields, string okText = "OK",
                                                   Func<Dictionary<string, string>, string?>? validate = null)
    {
        var window = NewWindow(title, 520);
        var panel  = new StackPanel { Margin = new Thickness(16) };
        if (!string.IsNullOrWhiteSpace(intro)) panel.Children.Add(new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });

        var readers = new Dictionary<string, Func<string>>();
        FrameworkElement? first = null;
        foreach (var f in fields)
        {
            FrameworkElement control;
            switch (f.Kind)
            {
                case FieldKind.Check:
                {
                    var box = new CheckBox { Content = f.Label, IsChecked = f.Initial == "true", Margin = new Thickness(0, 4, 0, 4) };
                    readers[f.Id] = () => box.IsChecked == true ? "true" : "false";
                    control       = box;
                    break;
                }
                case FieldKind.Choice:
                {
                    panel.Children.Add(Label(f.Label));
                    var combo = new ComboBox { ItemsSource = f.Choices, SelectedItem = f.Initial.Length > 0 ? f.Initial : f.Choices?.FirstOrDefault(), Margin = new Thickness(0, 0, 0, 8) };
                    readers[f.Id] = () => combo.SelectedItem as string ?? "";
                    control       = combo;
                    break;
                }
                case FieldKind.Password:
                {
                    panel.Children.Add(Label(f.Label));
                    var pw = new PasswordBox { Password = f.Initial, Margin = new Thickness(0, 0, 0, 8) };
                    readers[f.Id] = () => pw.Password;
                    control       = pw;
                    break;
                }
                case FieldKind.Multiline:
                {
                    panel.Children.Add(Label(f.Label));
                    var text = new TextBox
                               {
                                   Text = f.Initial, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 150, FontFamily = new FontFamily("Consolas"),
                                   VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 8)
                               };
                    readers[f.Id] = () => text.Text;
                    control       = text;
                    break;
                }
                default:
                {
                    panel.Children.Add(Label(f.Label));
                    var text = new TextBox { Text = f.Initial, Margin = new Thickness(0, 0, 0, 8) };
                    readers[f.Id] = () => text.Text;
                    control       = text;
                    break;
                }
            }

            panel.Children.Add(control);
            if (f.Hint is not null) panel.Children.Add(new TextBlock { Text = f.Hint, Opacity = 0.65, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, -4, 0, 8), FontSize = 11 });

            first ??= control;
        }

        var error = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8), Visibility = Visibility.Collapsed };
        panel.Children.Add(error);

        Dictionary<string, string>? result = null;
        var ok     = PushButton(okText, isDefault: true);
        var cancel = PushButton("Cancel", isCancel: true);
        ok.Click += (_, _) =>
                    {
                        var values = readers.ToDictionary(r => r.Key, r => r.Value());
                        if (validate?.Invoke(values) is { } problem)
                        {
                            error.Text       = problem;
                            error.Visibility = Visibility.Visible;
                            return;
                        }

                        result              = values;
                        window.DialogResult = true;
                    };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        row.Children.Add(ok);
        row.Children.Add(cancel);
        panel.Children.Add(row);

        window.Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
        window.Loaded += (_, _) => first?.Focus();
        window.ShowDialog();
        return result;
    }

    private static TextBlock Label(string text)
    {
        return new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 3) };
    }

    // ---- a text result ---------------------------------------------------------------------------------------

    /// <summary>Shows text a person will want to copy or keep (a PEM, a signature): selectable, with Copy and any extra buttons.</summary>
    /// <param name="extra">Further buttons: the text on each and what it does with the text.</param>
    public static void Result(string title, string message, string text, params (string Label, Action<string> Run)[] extra)
    {
        var window = NewWindow(title, 640);
        var panel  = new DockPanel { Margin = new Thickness(16) };

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(row, Dock.Bottom);
        panel.Children.Add(row);

        var heading = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(heading, Dock.Top);
        panel.Children.Add(heading);

        var box = new TextBox
                  {
                      Text = text, IsReadOnly = true, IsReadOnlyCaretVisible = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Consolas"), Height = 280,
                      VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
                  };
        panel.Children.Add(box);

        var copy = PushButton("_Copy");
        copy.Click += (_, _) =>
                      {
                          if (CertificateActionsHelpers.TryCopy(text)) copy.Content = "Copied";
                      };
        row.Children.Add(copy);
        foreach (var (label, run) in extra)
        {
            var b = PushButton(label);
            b.Click += (_, _) => run(text);
            row.Children.Add(b);
        }

        var close = PushButton("Close", isDefault: true, isCancel: true);
        close.Click += (_, _) => window.DialogResult = true;
        row.Children.Add(close);

        window.Content = panel;
        window.ShowDialog();
    }

    // ---- the Windows certificate stores ----------------------------------------------------------------------

    /// <summary>Lets the person pick certificates from a Windows certificate store. Null when cancelled; otherwise the ones chosen.</summary>
    public static (IReadOnlyList<X509Certificate2> Certificates, string Store)? PickFromStore()
    {
        var window = NewWindow("Import X509 Certificates from a Certificate Store", 760);
        var panel  = new DockPanel { Margin = new Thickness(16) };

        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(top, Dock.Top);
        var location = new ComboBox { ItemsSource = new[] { "Current User", "Local Machine" }, SelectedIndex = 0, MinWidth = 130, Margin = new Thickness(0, 0, 8, 0) };
        var store    = new ComboBox { ItemsSource = CertificateImport.StoreNames, SelectedIndex = 0, MinWidth = 160 };
        top.Children.Add(new TextBlock { Text = "Store: ", VerticalAlignment = VerticalAlignment.Center });
        top.Children.Add(location);
        top.Children.Add(store);
        panel.Children.Add(top);

        var status = new TextBlock { Opacity = 0.7, Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(status, Dock.Bottom);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(row, Dock.Bottom);
        panel.Children.Add(row);
        panel.Children.Add(status);

        var list = new ListBox { SelectionMode = SelectionMode.Extended, Height = 320 };
        panel.Children.Add(list);

        IReadOnlyList<X509Certificate2> shown = [];
        void Reload()
        {
            try
            {
                shown           = CertificateImport.ReadStore(location.SelectedIndex == 0 ? StoreLocation.CurrentUser : StoreLocation.LocalMachine, (string)store.SelectedItem);
                list.ItemsSource = shown.Select(c => $"{CertificateInfo.DisplayName(c)}    issued by {c.GetNameInfo(X509NameType.SimpleName, true)}    expires {c.NotAfter:yyyy-MM-dd}    [{CertificateInfo.ThumbprintSha256(c)[..8]}]").ToList();
                status.Text      = $"{shown.Count} certificate(s). Ctrl+click or Shift+click to choose several.";
            }
            catch (Exception ex)
            {
                shown            = [];
                list.ItemsSource = null;
                status.Text      = "That store could not be read: " + ex.Message;
            }
        }

        string StoreText() => $"{location.SelectedItem} / {store.SelectedItem}";
        location.SelectionChanged += (_, _) => Reload();
        store.SelectionChanged    += (_, _) => Reload();

        IReadOnlyList<X509Certificate2>? chosen = null;
        var                              where  = "";
        var all    = PushButton("Import _All");
        var ok     = PushButton("_Import Selected", isDefault: true);
        var cancel = PushButton("Cancel", isCancel: true);
        all.Click += (_, _) =>
                     {
                         chosen              = shown;
                         where               = StoreText();
                         window.DialogResult = true;
                     };
        ok.Click += (_, _) =>
                    {
                        chosen              = list.SelectedIndexes().Select(i => shown[i]).ToList();
                        where               = StoreText();
                        window.DialogResult = chosen.Count > 0;
                    };
        row.Children.Add(all);
        row.Children.Add(ok);
        row.Children.Add(cancel);

        window.Content = panel;
        window.Loaded += (_, _) => Reload();
        return window.ShowDialog() == true && chosen is { Count: > 0 } ? (chosen, where) : null;
    }

    private static IEnumerable<int> SelectedIndexes(this ListBox list)
    {
        return list.SelectedItems.Cast<object>().Select(list.Items.IndexOf).OrderBy(i => i);
    }
}

using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Telefrag.DI;
using IoDirectory = System.IO.Directory;
using IoFile = System.IO.File;
using IoPath = System.IO.Path;

namespace Silo.Contracts.Core;

/// <summary>
///     The stock New Silo dialog: one window that picks both what the new Silo starts from (a template list, whose
///     first entry is the "Blank Silo" sentinel meaning no template) and where it is saved (a path box with a Browse
///     button that opens a Save dialog). Returns a <see cref="NewSiloResult" /> -- a null <c>Template</c> for Blank.
/// </summary>
/// <remarks>
///     The window is built per call rather than kept, so each use starts clean, is owned by whatever window is
///     main at that moment, and can be shown any number of times. Replacing an existing file is not decided here; the
///     caller asks the person before overwriting.
/// </remarks>
[Singleton(typeof(INewSiloDialog))]
public class DefaultNewSiloDialog : INewSiloDialog
{
    public const string BlankName = "Blank Silo";

    private static string? s_lastFolder;

    /// <summary>
    ///     The sentinel shown first in the list. It is only a marker: choosing it makes the result's template null, so the
    ///     empty-Silo builder path is used. Never hand this instance to a builder.
    /// </summary>
    public static SiloTemplate Blank { get; } = new()
                                                {
                                                    Name = BlankName,
                                                    Description =
                                                        "An empty Silo: just the standard structure with nothing in it. Start here to build everything yourself."
                                                };

    /// <summary>Extra templates to offer besides the ones found by <see cref="SiloTemplateCatalog" />.</summary>
    public ObservableCollection<SiloTemplate> Templates { get; set; } = [];

    public NewSiloResult ShowDialog()
    {
        var dispatcher = Application.Current.Dispatcher;
        if (!dispatcher.CheckAccess()) return dispatcher.Invoke(() => ShowDialog());

        var choices = new List<SiloTemplate> { Blank };
        choices.AddRange(SiloTemplateCatalog.Load());
        choices.AddRange(Templates);

        return new NewSiloForm(choices).Run();
    }

    private sealed class NewSiloForm
    {
        private readonly Button _browse = new()
                                          {
                                              Content = "Browse...", Padding = new Thickness(12, 3, 12, 3),
                                              Margin  = new Thickness(8,                         0, 0,  0)
                                          };

        private readonly TextBlock _contents = new()
                                               {
                                                   TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0),
                                                   Foreground   = SystemColors.GrayTextBrush
                                               };

        private readonly Button _create = new()
                                          {
                                              Content   = "Create", Padding = new Thickness(18, 4, 18, 4), IsDefault = true,
                                              IsEnabled = false
                                          };

        private readonly TextBlock _description = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0,  0) };
        private readonly ListBox   _list        = new() { MinWidth     = 210, Margin               = new Thickness(0, 0, 12, 0) };

        private readonly TextBlock _message = new()
                                              {
                                                  TextWrapping = TextWrapping.Wrap, MinHeight = 18, Margin = new Thickness(0, 6, 0, 0)
                                              };

        private readonly TextBox _path = new() { VerticalContentAlignment = VerticalAlignment.Center };
        private readonly List<SiloTemplate> _templates;
        private readonly TextBlock _title = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly Window _window;

        private bool          _pathEditedByPerson; // once true, picking another template no longer re-suggests a file name
        private NewSiloResult _result = new(true, null, null);
        private bool          _settingPath;

        private SiloTemplate? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as SiloTemplate;

        public NewSiloResult Run()
        {
            _window.ShowDialog();
            return _result;
        }

        private UIElement BuildContent()
        {
            var root = new Grid { Margin                       = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var heading = new TextBlock { Text = "Create a new Silo", FontSize = 14, Margin = new Thickness(0, 0, 0, 10) };
            Place(root, heading, 0);

            // template list | details of the selected template
            var middle = new Grid();
            middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
            middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(_list, 0);

            var details = new StackPanel();
            details.Children.Add(_title);
            details.Children.Add(_description);
            details.Children.Add(_contents);
            var scroller = new ScrollViewer
                           {
                               Content = details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                               Margin  = new Thickness(4, 0, 0, 0)
                           };
            Grid.SetColumn(scroller, 1);
            middle.Children.Add(_list);
            middle.Children.Add(scroller);
            Place(root, middle, 1);

            // Save as: [ path............ ] [Browse...]
            var where = new Grid { Margin                            = new Thickness(0, 14, 0, 0) };
            where.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            where.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            where.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock
                        {
                            Text   = "Save as:", VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(0, 0, 8, 0)
                        };
            Grid.SetColumn(label,   0);
            Grid.SetColumn(_path,   1);
            Grid.SetColumn(_browse, 2);
            where.Children.Add(label);
            where.Children.Add(_path);
            where.Children.Add(_browse);
            Place(root, where, 2);

            Place(root, _message, 3);

            var buttons = new StackPanel
                          {
                              Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                              Margin      = new Thickness(0, 12, 0, 0)
                          };
            buttons.Children.Add(_create);
            buttons.Children.Add(new Button
                                 {
                                     Content = "Cancel", Padding = new Thickness(18, 4, 18, 4),
                                     Margin  = new Thickness(8,                      0, 0,  0), IsCancel = true
                                 });
            Place(root, buttons, 4);

            return root;
        }

        private static void Place(Grid grid, UIElement element, int row)
        {
            Grid.SetRow(element, row);
            grid.Children.Add(element);
        }

        // ---- the template list ---------------------------------------------------------------------------

        private static ListBoxItem ListItem(SiloTemplate t)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 4, 2, 4) };
            row.Children.Add(IconFor(t));
            row.Children.Add(new TextBlock
                             {
                                 Text         = DisplayName(t), VerticalAlignment = VerticalAlignment.Center,
                                 TextTrimming = TextTrimming.CharacterEllipsis
                             });
            return new ListBoxItem { Content = row, Tag = t };
        }

        private static UIElement IconFor(SiloTemplate t)
        {
            // an icon key or a picture; one that cannot be found or read falls back to the glyph
            if (Silo.Design.IconResolver.ElementFor(t.Icon, 24) is { } icon)
            {
                icon.Margin            = new Thickness(0, 0, 8, 0);
                icon.VerticalAlignment = VerticalAlignment.Center;
                return icon;
            }

            return new TextBlock
                   {
                       Text              = ReferenceEquals(t, Blank) ? "" : "",
                       FontFamily        = new FontFamily("Segoe MDL2 Assets"),
                       FontSize          = 20,
                       Width             = 24,
                       Margin            = new Thickness(0, 0, 8, 0),
                       VerticalAlignment = VerticalAlignment.Center
                   };
        }

        private static string DisplayName(SiloTemplate t)
        {
            return string.IsNullOrWhiteSpace(t.Name) ? "(unnamed template)" : t.Name;
        }

        private void OnTemplateChanged()
        {
            var t = Selected;
            if (t is null) return;

            _title.Text = DisplayName(t);

            // template descriptions are often written across several indented lines in XAML
            var description = string.IsNullOrWhiteSpace(t.Description) ? "" : Regex.Replace(t.Description, @"\s+", " ").Trim();
            _description.Text       = description;
            _description.Visibility = description.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

            if (ReferenceEquals(t, Blank) || t.Items.Count == 0)
            {
                _contents.Text = ReferenceEquals(t, Blank) ? "No template: nothing is added." : "This template adds nothing yet.";
            }
            else
            {
                var parts = t.Items.GroupBy(i => i.GetType().Name).OrderBy(g => g.Key).Select(g => g.Count() + " x " + g.Key);
                _contents.Text = "Adds: " + string.Join(", ", parts);
            }

            if (!_pathEditedByPerson) SetPath(SuggestPath(t), false);
            Validate();
        }

        // ---- the destination path ------------------------------------------------------------------------

        private static string SuggestPath(SiloTemplate t)
        {
            var folder = s_lastFolder is { } last && IoDirectory.Exists(last)
                             ? last
                             : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var name = ReferenceEquals(t, Blank) ? "New Silo" : Sanitize(DisplayName(t));

            var candidate = IoPath.Combine(folder, name + ".silo");
            for (var n = 2; IoFile.Exists(candidate); n++)
                candidate = IoPath.Combine(folder, $"{name} ({n}).silo");
            return candidate;
        }

        private static string Sanitize(string name)
        {
            var bad = IoPath.GetInvalidFileNameChars();
            var s   = new string(name.Select(c => Array.IndexOf(bad, c) >= 0 ? '_' : c).ToArray()).Trim().TrimEnd('.');
            return s.Length == 0 ? "New Silo" : s;
        }

        private void SetPath(string path, bool byPerson)
        {
            _settingPath = true;
            try
            {
                _path.Text = path;
            }
            finally
            {
                _settingPath = false;
            }

            if (byPerson) _pathEditedByPerson = true;
            _path.CaretIndex = _path.Text.Length;
        }

        private void OnPathTextChanged()
        {
            if (!_settingPath) _pathEditedByPerson = true;
            Validate();
        }

        private void Browse()
        {
            string? dir = null, name = null;
            if (TryGetPath(out var current, out _))
            {
                dir  = IoPath.GetDirectoryName(current);
                name = IoPath.GetFileName(current);
            }

            var dialog = new SaveFileDialog
                         {
                             Title           = "Save the new Silo as",
                             Filter          = "Silo files (*.silo)|*.silo|All files (*.*)|*.*",
                             DefaultExt      = "silo",
                             AddExtension    = true,
                             OverwritePrompt = false, // replacing a file is confirmed after this dialog closes
                             FileName        = name ?? "New Silo.silo"
                         };
            if (dir is not null && IoDirectory.Exists(dir)) dialog.InitialDirectory = dir;

            if (dialog.ShowDialog(_window) == true) SetPath(dialog.FileName, true);
        }

        /// <summary>Reads the path box: a ".silo" extension is added when none is typed, and the folder must exist.</summary>
        private bool TryGetPath(out string full, out string? problem)
        {
            full    = "";
            problem = null;

            var text = _path.Text.Trim().Trim('"');
            if (text.Length == 0)
            {
                problem = "Choose where to save the new Silo.";
                return false;
            }

            try
            {
                if (!IoPath.HasExtension(text)) text += ".silo";
                full = IoPath.GetFullPath(text);

                var name = IoPath.GetFileName(full);
                if (name.Length == 0 || name == ".silo")
                {
                    problem = "Give the Silo a file name.";
                    return false;
                }

                if (IoDirectory.Exists(full))
                {
                    problem = "That is a folder; add a file name.";
                    return false;
                }

                var dir = IoPath.GetDirectoryName(full);
                if (dir is null || !IoDirectory.Exists(dir))
                {
                    problem = "That folder does not exist.";
                    return false;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                problem = "That is not a valid file path.";
                return false;
            }

            return true;
        }

        private void Validate()
        {
            var ok = TryGetPath(out var full, out var problem);
            _create.IsEnabled = ok && Selected is not null;

            if (!ok)
            {
                _message.Foreground = Brushes.Firebrick;
                _message.Text       = problem ?? "";
            }
            else if (IoFile.Exists(full))
            {
                _message.Foreground = SystemColors.GrayTextBrush;
                _message.Text       = "A file with that name already exists; you will be asked before it is replaced.";
            }
            else
            {
                _message.Text = "";
            }
        }

        private void Accept()
        {
            if (!TryGetPath(out var full, out _) || Selected is not { } t) return;

            s_lastFolder         = IoPath.GetDirectoryName(full);
            _result              = new NewSiloResult(false, full, ReferenceEquals(t, Blank) ? null : t);
            _window.DialogResult = true;
        }

        public NewSiloForm(List<SiloTemplate> templates)
        {
            _templates = templates;
            _window = new Window
                      {
                          Title                 = "New Silo",
                          Width                 = 740,
                          Height                = 470,
                          MinWidth              = 580,
                          MinHeight             = 380,
                          ShowInTaskbar         = false,
                          ResizeMode            = ResizeMode.CanResizeWithGrip,
                          WindowStartupLocation = WindowStartupLocation.CenterOwner,
                          Owner                 = Application.Current?.MainWindow is { IsVisible: true } w ? w : null
                      };
            if (_window.Owner is null) _window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            _window.Content = BuildContent();

            _list.SelectionChanged += (_, _) => OnTemplateChanged();
            _list.MouseDoubleClick += (_, _) =>
                                      {
                                          if (_create.IsEnabled) _create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                      };
            _path.TextChanged += (_, _) => OnPathTextChanged();
            _browse.Click     += (_, _) => Browse();
            _create.Click     += (_, _) => Accept();
            _window.Loaded    += (_, _) => _list.Focus();

            foreach (var t in _templates) _list.Items.Add(ListItem(t));
            _list.SelectedIndex = 0;
        }
    }
}
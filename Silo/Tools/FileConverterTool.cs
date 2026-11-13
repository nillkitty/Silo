using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Silo.Providers.Conversion;
using Silo.Providers.Reflection;
using Silo.Tasks;
using Telefrag.DI;

namespace Silo.Tools;

/// <summary>
///     Turns files of one format into another with the registered <see cref="IFileConverter" />s: SMD dumps into ROM images (and
///     back), one kind of archive or disc image into another, JSON, YAML and XML into each other. Drop files on it or browse for
///     them; it says what each is and offers the formats they can become. Each output is written beside its input unless a
///     folder is chosen.
/// </summary>
[Transient(typeof(ITool))]
public class FileConverterTool : ToolBase
{
    private readonly ListBox  _files     = new() { MinHeight = 120, AllowDrop = true };
    private readonly ComboBox _target    = new() { MinWidth = 260, IsEnabled = false };
    private readonly TextBox  _folder    = new() { MinWidth = 260, ToolTip = "Where the converted files go; empty puts each beside its input" };
    private readonly CheckBox _overwrite = new() { Content = "Overwrite existing files", Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBox  _log       = new() { IsReadOnly = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap, MinHeight = 120, FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New") };
    private readonly Button   _convert   = new() { Content = "_Convert", Padding = new Thickness(14, 4, 14, 4), IsEnabled = false, Margin = new Thickness(0, 0, 8, 0) };

    public FileConverterTool() : base("fileconverter")
    {
        DisplayName = "_File Converter";
    }

    /// <summary>A file in the list: what it is, and what it can become.</summary>
    private sealed record Item(string Path, string Format, IReadOnlyList<(IFileFormat Target, IFileConverter Converter)> Targets)
    {
        public override string ToString() => $"{System.IO.Path.GetFileName(Path)}  --  {Format}  ({System.IO.Path.GetDirectoryName(Path)})";
    }

    /// <summary>A format in the Convert to box, and how many of the files can be made into it.</summary>
    private sealed record Choice(IFileFormat Format, int Count, int Of)
    {
        public override string ToString() => Count == Of ? Format.DisplayName ?? Format.FileExtension : $"{Format.DisplayName} ({Count} of {Of} files)";
    }

    protected override UIElement OnBuildContent()
    {
        var root = new DockPanel { Margin = new Thickness(8) };

        root.Children.Add(Dock(new TextBlock { Text = "Drop files here, or add them with Browse. Choose what to make of them, then Convert.", Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap }, System.Windows.Controls.Dock.Top));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
        var browse  = new Button { Content = "_Browse...", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0) };
        var remove  = new Button { Content = "_Remove", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0) };
        var clear   = new Button { Content = "C_lear", Padding = new Thickness(10, 4, 10, 4) };
        buttons.Children.Add(browse);
        buttons.Children.Add(remove);
        buttons.Children.Add(clear);
        root.Children.Add(Dock(buttons, System.Windows.Controls.Dock.Top));

        var target = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        target.Children.Add(new TextBlock { Text = "Convert to:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Width = 90 });
        target.Children.Add(_target);

        var output = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var pick   = new Button { Content = "...", Width = 28, Margin = new Thickness(4, 0, 0, 0), ToolTip = "Choose the output folder" };
        System.Windows.Controls.DockPanel.SetDock(pick, System.Windows.Controls.Dock.Right);
        output.Children.Add(pick);
        output.Children.Add(new TextBlock { Text = "Output folder:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Width = 90 });
        output.Children.Add(_folder);

        var bottom = new StackPanel();
        bottom.Children.Add(target);
        bottom.Children.Add(output);
        bottom.Children.Add(_overwrite);
        var run = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
        run.Children.Add(_convert);
        bottom.Children.Add(run);
        root.Children.Add(Dock(bottom, System.Windows.Controls.Dock.Bottom));
        root.Children.Add(Dock(_log, System.Windows.Controls.Dock.Bottom));
        root.Children.Add(_files);

        browse.Click += (_, _) =>
                        {
                            var dialog = new OpenFileDialog { Title = "Choose files to convert", Filter = Providers.Format.Formats.AllFiles, Multiselect = true };
                            if (dialog.ShowDialog() == true) _ = AddAsync(dialog.FileNames);
                        };
        pick.Click += (_, _) =>
                      {
                          var dialog = new OpenFolderDialog { Title = "Output folder" };
                          if (dialog.ShowDialog() == true) _folder.Text = dialog.FolderName;
                      };
        remove.Click += (_, _) =>
                        {
                            if (_files.SelectedItem is not null) _files.Items.Remove(_files.SelectedItem);
                            UpdateTargets();
                        };
        clear.Click += (_, _) =>
                       {
                           _files.Items.Clear();
                           UpdateTargets();
                       };
        _convert.Click += async (_, _) => await ConvertAllAsync();
        _files.DragOver += (_, e) =>
                           {
                               e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
                               e.Handled = true;
                           };
        _files.Drop += (_, e) =>
                       {
                           if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped) _ = AddAsync(dropped);
                       };
        return root;
    }

    private static UIElement Dock(UIElement element, Dock side)
    {
        System.Windows.Controls.DockPanel.SetDock(element, side);
        return element;
    }

    /// <summary>Adds files (a folder's files, for a folder), working out what each is off the UI thread and saying so in the log.</summary>
    private async Task AddAsync(IEnumerable<string> paths)
    {
        var files = paths.SelectMany(p => Directory.Exists(p) ? Directory.EnumerateFiles(p) : [p])
                         .Where(p => File.Exists(p) && !_files.Items.Cast<Item>().Any(i => string.Equals(i.Path, p, StringComparison.OrdinalIgnoreCase)))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .ToList();

        foreach (string path in files)
        {
            var (item, report) = await Task.Run(() => Inspect(path));
            _files.Items.Add(item);
            Say($"{Path.GetFileName(path)}: {item.Format}" + (item.Targets.Count == 0 ? " -- nothing here can convert it" : " -- can become " + string.Join(", ", item.Targets.Select(t => t.Target.DisplayName))));
            foreach (string line in report) Say("    " + line);
        }

        UpdateTargets();
    }

    private static (Item Item, IReadOnlyList<string> Report) Inspect(string path)
    {
        try
        {
            var targets = FileConverters.TargetsFor(path);
            var report  = new List<string>();
            string? format = null;
            foreach (var converter in targets.Select(t => t.Converter).Distinct())
            {
                format ??= converter.SourceFormat(path)?.DisplayName;
                report.AddRange(converter.Inspect(path));
            }

            format ??= Providers.Format.Formats.ForFile(path).FirstOrDefault()?.DisplayName ?? "unknown format";
            return (new Item(path, format, targets), report);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return (new Item(path, "cannot be read", []), [ex.Message]);
        }
    }

    /// <summary>Fills the Convert to box with every format some file can become, the one most of them can first.</summary>
    private void UpdateTargets()
    {
        var items    = _files.Items.Cast<Item>().ToList();
        var previous = (_target.SelectedItem as Choice)?.Format.GetType();
        var choices = items.SelectMany(i => i.Targets.Select(t => t.Target))
                           .GroupBy(f => f.GetType())
                           .Select(g => new Choice(g.First(), g.Count(), items.Count))
                           .OrderByDescending(c => c.Count)
                           .ThenBy(c => c.Format.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                           .ToList();

        _target.ItemsSource  = choices;
        _target.SelectedItem = choices.FirstOrDefault(c => c.Format.GetType() == previous) ?? choices.FirstOrDefault();
        _target.IsEnabled    = choices.Count > 0;
        _convert.IsEnabled   = choices.Count > 0;
    }

    private void Say(string line)
    {
        _log.AppendText(line + Environment.NewLine);
        _log.ScrollToEnd();
    }

    private async Task ConvertAllAsync()
    {
        if (_target.SelectedItem is not Choice choice) return;

        string folder    = _folder.Text.Trim();
        bool   overwrite = _overwrite.IsChecked == true;
        var    items     = _files.Items.Cast<Item>().ToList();
        if (folder.Length > 0) Directory.CreateDirectory(folder);

        _convert.IsEnabled = false;
        int done = 0, tried = 0;
        try
        {
            await FileTasks.RunAsync($"Converting {items.Count} file(s) to {choice.Format.DisplayName}", "File Converter", async task =>
            {
                foreach (var item in items)
                {
                    task.Token.ThrowIfCancellationRequested();
                    var match = item.Targets.FirstOrDefault(t => t.Target.GetType() == choice.Format.GetType());
                    if (match.Converter is null)
                    {
                        Post($"{Path.GetFileName(item.Path)}: cannot become {choice.Format.DisplayName}; left as it is.");
                        continue;
                    }

                    tried++;
                    string output = Path.Combine(folder.Length > 0 ? folder : Path.GetDirectoryName(item.Path)!, FileConverters.OutputName(item.Path, match.Target));
                    task.Report($"{Path.GetFileName(item.Path)} ({tried} of {items.Count})", tried);
                    try
                    {
                        if (!overwrite && File.Exists(output)) throw new IOException($"{Path.GetFileName(output)} already exists (tick Overwrite to replace it).");

                        var result = await Task.Run(() => match.Converter.Convert(item.Path, match.Target, output, task.Token), task.Token);
                        Post($"{Path.GetFileName(item.Path)} -> {result.Output}");
                        foreach (string note in result.Notes) Post("    " + note);
                        done++;
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
                    {
                        Post($"{Path.GetFileName(item.Path)}: {ex.Message}");
                    }
                }

                task.SetResult(done);
            }, true);
        }
        catch (OperationCanceledException)
        {
            Say("Cancelled.");
        }
        catch (Exception ex)
        {
            Say("The conversion stopped: " + ex.Message);
        }
        finally
        {
            Say($"{done} of {items.Count} converted.");
            _convert.IsEnabled = _target.IsEnabled;
        }
    }

    /// <summary><see cref="Say" />, from whatever thread.</summary>
    private void Post(string line)
    {
        if (_log.Dispatcher.CheckAccess()) Say(line);
        else _log.Dispatcher.BeginInvoke(() => Say(line));
    }
}

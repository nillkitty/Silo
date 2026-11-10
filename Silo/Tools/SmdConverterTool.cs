using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Telefrag.DI;

namespace Silo.Tools;

/// <summary>
///     Turns Super Magic Drive dumps (<c>.smd</c>) into plain ROM images (<c>.bin</c>), which the disassembly tools need before they can split
///     a game's data. Drop files on it or browse for them; each ROM is written beside its SMD unless a folder is chosen (see
///     <see cref="SmdConverter" />).
/// </summary>
[Transient(typeof(ITool))]
public class SmdConverterTool : ToolBase
{
    private readonly ListBox   _files     = new() { MinHeight = 120, AllowDrop = true };
    private readonly TextBox   _folder    = new() { MinWidth = 260, ToolTip = "Where the .bin files go; empty puts each beside its .smd" };
    private readonly CheckBox  _overwrite = new() { Content = "Overwrite existing .bin files", Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBox   _log       = new() { IsReadOnly = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap, MinHeight = 120, FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New") };
    private readonly Button    _convert   = new() { Content = "_Convert", Padding = new Thickness(14, 4, 14, 4), IsEnabled = false, Margin = new Thickness(0, 0, 8, 0) };

    public SmdConverterTool() : base("smdconverter")
    {
        DisplayName = "_SMD to BIN Converter";
    }

    protected override UIElement OnBuildContent()
    {
        var root = new DockPanel { Margin = new Thickness(8) };

        root.Children.Add(Dock(new TextBlock { Text = "Drop .smd files here, or add them with Browse. Each is converted to a plain ROM (.bin).", Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap }, System.Windows.Controls.Dock.Top));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
        var browse  = new Button { Content = "_Browse...", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0) };
        var remove  = new Button { Content = "_Remove", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0) };
        var clear   = new Button { Content = "C_lear", Padding = new Thickness(10, 4, 10, 4) };
        buttons.Children.Add(browse);
        buttons.Children.Add(remove);
        buttons.Children.Add(clear);
        root.Children.Add(Dock(buttons, System.Windows.Controls.Dock.Top));

        var output = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var pick   = new Button { Content = "...", Width = 28, Margin = new Thickness(4, 0, 0, 0), ToolTip = "Choose the output folder" };
        System.Windows.Controls.DockPanel.SetDock(pick, System.Windows.Controls.Dock.Right);
        output.Children.Add(pick);
        output.Children.Add(new TextBlock { Text = "Output folder:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        output.Children.Add(_folder);
        var bottom = new StackPanel();
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
                            var dialog = new OpenFileDialog { Title = "Choose SMD files", Filter = "SMD files (*.smd)|*.smd|All files (*.*)|*.*", Multiselect = true };
                            if (dialog.ShowDialog() == true) Add(dialog.FileNames);
                        };
        pick.Click += (_, _) =>
                      {
                          var dialog = new OpenFolderDialog { Title = "Output folder" };
                          if (dialog.ShowDialog() == true) _folder.Text = dialog.FolderName;
                      };
        remove.Click += (_, _) =>
                        {
                            if (_files.SelectedItem is not null) _files.Items.Remove(_files.SelectedItem);
                            _convert.IsEnabled = _files.Items.Count > 0;
                        };
        clear.Click += (_, _) =>
                       {
                           _files.Items.Clear();
                           _convert.IsEnabled = false;
                       };
        _convert.Click += (_, _) => ConvertAll();
        _files.DragOver += (_, e) =>
                           {
                               e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
                               e.Handled = true;
                           };
        _files.Drop += (_, e) =>
                       {
                           if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped) Add(dropped);
                       };
        return root;
    }

    private static UIElement Dock(UIElement element, Dock side)
    {
        System.Windows.Controls.DockPanel.SetDock(element, side);
        return element;
    }

    private void Add(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            if (!File.Exists(path) || _files.Items.Contains(path)) continue;

            _files.Items.Add(path);
            Inspect(path);
        }

        _convert.IsEnabled = _files.Items.Count > 0;
    }

    /// <summary>Says what the file is and why: what was looked at in it and what that showed.</summary>
    private void Inspect(string path)
    {
        try
        {
            var analysis = SmdConverter.Analyze(File.ReadAllBytes(path));
            Say($"{Path.GetFileName(path)}: {analysis.Kind switch { SmdConverter.Kind.Smd => "an SMD: it will be converted", SmdConverter.Kind.Rom => "already a plain ROM: nothing to convert", _ => "not recognised as an SMD" }}");
            foreach (string line in analysis.Report) Say("    " + line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say($"{Path.GetFileName(path)}: cannot read it: {ex.Message}");
        }
    }

    private void Say(string line)
    {
        _log.AppendText(line + Environment.NewLine);
        _log.ScrollToEnd();
    }

    private void ConvertAll()
    {
        string folder = _folder.Text.Trim();
        if (folder.Length > 0) Directory.CreateDirectory(folder);

        int done = 0;
        foreach (string input in _files.Items.Cast<string>().ToList())
        {
            try
            {
                string? output = folder.Length > 0 ? Path.Combine(folder, Path.ChangeExtension(Path.GetFileName(input), ".bin")) : null;
                var (path, result) = SmdConverter.ConvertFile(input, output, _overwrite.IsChecked == true);
                string title = SmdConverter.TitleOf(result.Rom);
                Say($"{Path.GetFileName(input)} -> {path}  ({result.Blocks} block(s), {result.Rom.Length / 1024} KB{(title.Length > 0 ? ", " + title : string.Empty)})");
                foreach (string note in result.Notes) Say("    note: " + note);
                Say($"    {SmdConverter.Analyze(result.Rom).Report.LastOrDefault(l => l.StartsWith("Checksum")) ?? "the converted ROM has no checksum to check"}");
                done++;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Say($"{Path.GetFileName(input)}: {ex.Message}");
            }
        }

        Say($"{done} of {_files.Items.Count} converted.");
    }
}

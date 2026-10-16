using System.IO;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Silo.Connectors.File;

/// <summary>
///     The view of a file system item: what is in a folder, the text of a text file, an image. It shows "Loading..." while
///     <see cref="FilePreviews.Compute" /> reads the file system on another thread, so a slow network path does not hold the
///     window up.
/// </summary>
public sealed class FileModelView : UserControl
{
    private readonly ContentControl _body = new();

    public FileModelView(FileModel model)
    {
        var header = new StackPanel { Margin = new Thickness(16, 12, 16, 8) };
        header.Children.Add(new TextBlock { Text = model.Name, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        header.Children.Add(Muted(FileKinds.Describe(model.Info)));
        header.Children.Add(Muted(model.Path));
        if (model.LinkTarget is { Length: > 0 } target) header.Children.Add(Muted("Points to " + target));
        DockPanel.SetDock(header, Dock.Top);

        _body.Content = Muted("Loading...", new Thickness(16, 4, 16, 4));

        var root = new DockPanel();
        root.Children.Add(header);
        root.Children.Add(_body);
        Content = root;

        _ = LoadAsync(model);
    }

    private async Task LoadAsync(FileModel model)
    {
        FilePreview preview;
        try
        {
            preview = await Task.Run(() => FilePreviews.Compute(model));
        }
        catch (Exception ex)
        {
            preview = new MessagePreview(ex.Message);
        }

        _body.Content = Build(preview);
    }

    private static UIElement Build(FilePreview preview)
    {
        switch (preview)
        {
            case FolderPreview folder:
                return BuildFolder(folder);

            case TextPreview text:
            {
                var box = new TextBox
                          {
                              Text                          = text.Text,
                              IsReadOnly                    = true,
                              FontFamily                    = new FontFamily("Consolas"),
                              BorderThickness               = new Thickness(0),
                              Padding                       = new Thickness(16, 4, 16, 4),
                              HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                              VerticalScrollBarVisibility   = ScrollBarVisibility.Auto
                          };
                if (!text.Truncated) return box;

                var panel = new DockPanel();
                var note  = Muted($"Only the first {FilePreviews.TextLimit / 1024:N0} KB is shown.", new Thickness(16, 4, 16, 4));
                DockPanel.SetDock(note, Dock.Bottom);
                panel.Children.Add(note);
                panel.Children.Add(box);
                return panel;
            }

            case ImagePreview image:
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption  = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = new MemoryStream(image.Bytes);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return new ScrollViewer
                           {
                               HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                               VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                               Content = new Image { Source = bitmap, Stretch = Stretch.None, Margin = new Thickness(16, 4, 16, 4), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top }
                           };
                }
                catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException)
                {
                    return Muted("This image could not be shown: " + ex.Message, new Thickness(16, 4, 16, 4));
                }

            case MessagePreview message:
                return Muted(message.Text, new Thickness(16, 4, 16, 4));

            default:
                return Muted("No preview.", new Thickness(16, 4, 16, 4));
        }
    }

    private static UIElement BuildFolder(FolderPreview folder)
    {
        if (folder.Rows.Count == 0) return Muted("This folder is empty (or cannot be read).", new Thickness(16, 4, 16, 4));

        var grid = new GridView();
        grid.Columns.Add(Column("Name", nameof(FileRow.Name), 300));
        grid.Columns.Add(Column("Type", nameof(FileRow.Type), 160));
        grid.Columns.Add(Column("Size", nameof(FileRow.Size), 100));
        grid.Columns.Add(Column("Modified", nameof(FileRow.Modified), 150));

        var list = new ListView { View = grid, ItemsSource = folder.Rows, BorderThickness = new Thickness(0), Margin = new Thickness(8, 0, 8, 0) };
        if (!folder.Truncated) return list;

        var panel = new DockPanel();
        var note  = Muted($"Only the first {FileModel.EntryLimit:N0} items are shown.", new Thickness(16, 4, 16, 4));
        DockPanel.SetDock(note, Dock.Bottom);
        panel.Children.Add(note);
        panel.Children.Add(list);
        return panel;
    }

    private static GridViewColumn Column(string header, string property, double width)
    {
        return new GridViewColumn { Header = header, Width = width, DisplayMemberBinding = new Binding(property) };
    }

    private static TextBlock Muted(string text, Thickness? margin = null)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0, 2, 0, 0) };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        return block;
    }
}

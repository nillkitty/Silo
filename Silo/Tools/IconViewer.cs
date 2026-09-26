using System.Collections;
using System.ComponentModel;
using System.IO;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Telefrag.DI;
using Telerik.Windows.Controls;

namespace Silo.Tools;

[Transient(typeof(ITool))]
public abstract class ToolBase(string name) : ITool, ICommand, INotifyPropertyChanged
{
    private UIElement?  _ctrl;
    private RadPane?    _pane;
    private Window?     _window;
    private bool        _isEnabled = true;
    private bool        _isVisible = true;
    public  bool        ModalOnly   { get; set; }
    public  string      DisplayName { get; set; }
    public  string      GroupName   { get; set; }
    public  ImageSource Icon        { get; set; }
    public  string      Name        { get; } = name.Required();
    public  int         Priority    { get; set; }

    /// <summary>
    ///     Display text for the tool's Tools-menu entry. Declared concretely (not just
    ///     via the <see cref="ITool"/> default member) because WPF's reflection-based
    ///     data binding only sees members declared on the concrete runtime type.
    /// </summary>
    public string Header => DisplayName ?? Name;

    /// <inheritdoc cref="ITool.IsEnabled"/>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            OnPropertyChanged();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <inheritdoc cref="ITool.IsVisible"/>
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    ///     Reflects whether this tool's window is currently open.  There is exactly one
    ///     window per tool instance (tools are typically registered as singletons), so
    ///     this simply mirrors that window's visibility rather than tracking separate
    ///     state that could drift out of sync with it.
    /// </summary>
    public bool IsChecked => _window?.IsVisible ?? false;

    protected abstract UIElement OnBuildContent();

    private Window WindowWrap(UIElement ctrl)
    {
        ctrl.Required();
        if (_window is null)
        {
            _window = new Window
                      {
                          Title   = DisplayName ?? Name,
                          Content = ctrl
                      };

            // Tools are singletons and are meant to be shown/hidden repeatedly rather
            // than rebuilt, so closing the window via the titlebar (or Alt+F4, etc.)
            // hides it instead of destroying it - the same Window/content instance is
            // reused the next time the tool is toggled back open.
            _window.Closing += (_, e) =>
                                {
                                    e.Cancel = true;
                                    _window.Hide();
                                    OnPropertyChanged(nameof(IsChecked));
                                };
        }

        return _window;
    }

    private RadPane? PaneWrap(UIElement content)
    {
        return new RadPane
               {
                   Header  = DisplayName,
                   Content = content.Required()
               };
    }

    public RadPane? GetPane()
    {
        if (ModalOnly) return null;

        _pane ??= PaneWrap(OnBuildContent());
        return _pane;
    }

    /// <inheritdoc />
    public Window? Toggle()
    {
        var d = App.Instance!.Dispatcher;
        if (!d.CheckAccess())
            return d.Invoke(Toggle);

        // Executing the command while the window is open closes it, rather than
        // re-showing (or, worse, rebuilding) an already-open window.
        if (_window is { IsVisible: true })
        {
            _window.Hide();
            OnPropertyChanged(nameof(IsChecked));
            return null;
        }

        _ctrl ??= OnBuildContent();
        Window window = ModalOnly
            ? WindowWrap(_ctrl)
            : WindowWrap(_pane ??= PaneWrap(_ctrl));

        window.Show();
        window.Activate();
        OnPropertyChanged(nameof(IsChecked));
        return window;
    }

    public UIElement? GetContent()
    {
        _ctrl = OnBuildContent();
        return _ctrl;
    }

    // --- ICommand ---
    // Lets a ToolBase instance be bound directly as a Tools-menu item's Command:
    // IsEnabled drives CanExecute (with CommandManager notified on change above so the
    // menu item's enabled state stays live), and clicking the item calls Toggle() -
    // the same single entry point used for opening/closing the tool's window elsewhere.
    bool ICommand.CanExecute(object? parameter) => IsEnabled;

    void ICommand.Execute(object? parameter) => Toggle();

    event EventHandler? ICommand.CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

[Transient(typeof(ITool))]
public class IconViewer : ToolBase
{
    public IconViewer() : base("iconviewer")
    {
        DisplayName = "_Icon Viewer";
    }

    protected override UIElement OnBuildContent()
    {
        var rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var items = DiscoverIcons();

        var listBox = new ListBox
                      {
                          ItemsSource                = items,
                          Margin                     = new Thickness(8),
                          HorizontalContentAlignment = HorizontalAlignment.Center,
                          VerticalContentAlignment   = VerticalAlignment.Center
                      };

        // Attached property must be set via static setter
        ScrollViewer.SetHorizontalScrollBarVisibility(listBox, ScrollBarVisibility.Disabled);

        var panelFactory = new FrameworkElementFactory(typeof(WrapPanel));
        listBox.ItemsPanel = new ItemsPanelTemplate(panelFactory);

        var dt            = new DataTemplate(typeof(IconItem));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.SetValue(Border.BorderThicknessProperty,  new Thickness(1));
        borderFactory.SetValue(Border.BorderBrushProperty,      Brushes.LightGray);
        borderFactory.SetValue(Border.BackgroundProperty,       Brushes.White);
        borderFactory.SetValue(Border.MarginProperty,           new Thickness(2));
        borderFactory.SetValue(Border.PaddingProperty,          new Thickness(4));
        borderFactory.SetValue(FrameworkElement.WidthProperty,  48.0);
        borderFactory.SetValue(FrameworkElement.HeightProperty, 48.0);
        borderFactory.SetBinding(FrameworkElement.ToolTipProperty, new Binding("Name"));

        var contentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
        contentPresenter.SetBinding(ContentPresenter.ContentProperty, new Binding("VisualElement"));
        borderFactory.AppendChild(contentPresenter);
        dt.VisualTree        = borderFactory;
        listBox.ItemTemplate = dt;

        Grid.SetRow(listBox, 0);
        rootGrid.Children.Add(listBox);

        var bottomPanel = new Grid { Margin                            = new Thickness(8, 0, 8, 8) };
        bottomPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottomPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottomPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
                    {
                        Text              = "Resource Key / Path: ",
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin            = new Thickness(0, 0, 6, 0)
                    };
        Grid.SetColumn(label, 0);
        bottomPanel.Children.Add(label);

        var copyBox = new TextBox
                      {
                          VerticalAlignment = VerticalAlignment.Center,
                          Margin            = new Thickness(0, 0, 8, 0),
                          IsReadOnly        = true
                      };
        Grid.SetColumn(copyBox, 1);
        bottomPanel.Children.Add(copyBox);

        var copyButton = new Button
                         {
                             Content   = "Copy",
                             Width     = 75,
                             Height    = 26,
                             IsEnabled = false
                         };
        Grid.SetColumn(copyButton, 2);
        bottomPanel.Children.Add(copyButton);

        listBox.SelectionChanged += (_, _) =>
                                    {
                                        if (listBox.SelectedItem is IconItem selected)
                                        {
                                            copyBox.Text         = selected.CopyValue;
                                            copyButton.IsEnabled = true;
                                        }
                                        else
                                        {
                                            copyBox.Text         = string.Empty;
                                            copyButton.IsEnabled = false;
                                        }
                                    };

        copyButton.Click += (_, _) =>
                            {
                                if (!string.IsNullOrEmpty(copyBox.Text)) Clipboard.SetText(copyBox.Text);
                            };

        Grid.SetRow(bottomPanel, 1);
        rootGrid.Children.Add(bottomPanel);

        return rootGrid;
    }


    private static List<IconItem> DiscoverIcons()
    {
        var list = new List<IconItem>();
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                                  .Where(a => !a.IsDynamic && !a.FullName!.StartsWith("System") && !a.FullName.StartsWith("Microsoft"))
                                  .ToArray();

        if (Application.Current != null) ExtractFromDictionary(Application.Current.Resources, list);

        foreach (var asm in assemblies)
        {
            string    resName = asm.GetName().Name + ".g.resources";
            using var stream  = asm.GetManifestResourceStream(resName);
            if (stream != null)
            {
                using var reader = new ResourceReader(stream);
                foreach (DictionaryEntry entry in reader)
                {
                    string pathKey = entry.Key?.ToString() ?? string.Empty;

                    if (pathKey.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    {
                        var uri = new Uri($"pack://application:,,,/{asm.GetName().Name};component/{pathKey}", UriKind.Absolute);
                        var img = new Image
                                  {
                                      Source  = new BitmapImage(uri),
                                      Stretch = Stretch.Uniform,
                                      Width   = 24,
                                      Height  = 24
                                  };
                        list.Add(new IconItem
                                 {
                                     Name          = Path.GetFileName(pathKey),
                                     SourceType    = "PNG (Pack URI)",
                                     VisualElement = img,
                                     CopyValue     = uri.ToString()
                                 });
                    }
                    else if (pathKey.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) && entry.Value is Stream svgStream)
                    {
                        using var sr   = new StreamReader(svgStream);
                        var       geom = ExtractGeometryFromSvg(sr.ReadToEnd());
                        if (geom != null)
                            list.Add(new IconItem
                                     {
                                         Name          = Path.GetFileName(pathKey),
                                         SourceType    = "SVG Stream",
                                         VisualElement = CreatePathViewbox(geom),
                                         CopyValue     = Path.GetFileName(pathKey)
                                     });
                    }
                }
            }

            foreach (var manifestName in asm.GetManifestResourceNames())
                if (manifestName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    using var s = asm.GetManifestResourceStream(manifestName);
                    if (s != null)
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption  = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = s;
                        bmp.EndInit();
                        bmp.Freeze();

                        var img = new Image { Source = bmp, Stretch = Stretch.Uniform, Width = 24, Height = 24 };
                        list.Add(new IconItem
                                 {
                                     Name          = manifestName,
                                     SourceType    = "Embedded PNG",
                                     VisualElement = img,
                                     CopyValue     = manifestName
                                 });
                    }
                }
                else if (manifestName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    using var s = asm.GetManifestResourceStream(manifestName);
                    if (s != null)
                    {
                        using var reader = new StreamReader(s);
                        var       geom   = ExtractGeometryFromSvg(reader.ReadToEnd());
                        if (geom != null)
                            list.Add(new IconItem
                                     {
                                         Name          = manifestName,
                                         SourceType    = "Embedded SVG",
                                         VisualElement = CreatePathViewbox(geom),
                                         CopyValue     = manifestName
                                     });
                    }
                }
        }

        return list;
    }

    private static void ExtractFromDictionary(ResourceDictionary dict, List<IconItem> target)
    {
        foreach (var key in dict.Keys)
            if (dict[key] is StreamGeometry geom)
            {
                string keyStr = key.ToString() ?? "Unknown";
                if (!target.Any(x => x.Name == keyStr))
                    target.Add(new IconItem
                               {
                                   Name          = keyStr,
                                   SourceType    = "StreamGeometry",
                                   VisualElement = CreatePathViewbox(geom),
                                   CopyValue     = $"{{StaticResource {keyStr}}}"
                               });
            }

        // Avoid direct property initialization nesting; walk merged dictionaries in loop
        var mergedDictionaries = dict.MergedDictionaries;
        if (mergedDictionaries != null)
            for (int i = 0; i < mergedDictionaries.Count; i++)
                ExtractFromDictionary(mergedDictionaries[i], target);
    }

    private static Viewbox CreatePathViewbox(Geometry geometry)
    {
        var path = new System.Windows.Shapes.Path
                   {
                       Data    = geometry,
                       Fill    = Brushes.Black,
                       Stretch = Stretch.Uniform
                   };

        return new Viewbox
               {
                   Width  = 24,
                   Height = 24,
                   Child  = path
               };
    }

    private static Geometry? ExtractGeometryFromSvg(string svgContent)
    {
        var matches = Regex.Matches(svgContent, "(?i)<path[^>]*?\\sd=[\"']([^\"']+)[\"']");
        if (matches.Count == 0) return null;

        string combined = string.Join(" ", matches.Select(m => m.Groups[1].Value.Trim()));
        try
        {
            return StreamGeometry.Parse(combined);
        }
        catch
        {
            return null;
        }
    }


    private sealed class IconItem
    {
        public string  CopyValue     { get; set; } = string.Empty;
        public string  Name          { get; set; } = string.Empty;
        public string  SourceType    { get; set; } = string.Empty;
        public object? VisualElement { get; set; }
    }
}
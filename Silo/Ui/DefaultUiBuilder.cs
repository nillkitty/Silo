using System.Collections;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Silo.Commanding;
using Telefrag.DI;

namespace Silo.Ui;

[Register(typeof(IUiBuilder<>), true)]
public class DefaultUiBuilder<TModel> : IUiBuilder<TModel>
{
    public UIElement Build(TModel model)
    {
        if (model is null)
        {
            return new TextBlock
                   {
                       Text      = "(null)",
                       FontStyle = FontStyles.Italic,
                       Margin    = new Thickness(6)
                   };
        }

        // 1. If model is already a UIElement, evaluate wrapping/docking
        if (model is UIElement uiElement)
        {
            return WrapVisualElement(uiElement);
        }

        // 2. Resolve via standard idiomatic data annotations/attributes
        if (TryBuildFromAttributes(model, out var attributedElement))
        {
            return WrapVisualElement(attributedElement);
        }

        // 3. Fallback: Collections render in a DataGrid - see TryBuildFromAttributes'
        //    "grid"/"datagrid"/"radgridview" UIHint case, which is the same code path.
        if (model is IEnumerable collection and not string)
        {
            return new DataGrid
                   {
                       ItemsSource         = collection,
                       AutoGenerateColumns = true,
                       IsReadOnly          = true,
                       CanUserSortColumns  = true,
                       HorizontalAlignment = HorizontalAlignment.Stretch,
                       VerticalAlignment   = VerticalAlignment.Stretch
                   };
        }

        // 4. Fallback: arbitrary objects render in a hand-rolled reflection-based
        //    property editor (label + value-editor row per public read/write property).
        //    Replaces the Telerik RadPropertyGrid this used to build here.
        return BuildPropertyEditor(model);
    }

    private static UIElement WrapVisualElement(UIElement element)
    {
        // Used to also encapsulate a tool's pane (a Telerik RadPane, back when RadPane was
        // itself a UIElement) inside a full docking surface here. AvalonDock's equivalent,
        // LayoutAnchorable, is a layout-model object rather than a UIElement/Visual - it
        // can never actually reach this method (Build(TModel) above only calls this for a
        // `model is UIElement` match, which a LayoutAnchorable can never satisfy) - so that
        // branch was dead code once ported as-is, and the compiler correctly rejected the
        // `element is LayoutAnchorable` pattern as statically impossible. A ToolBase's pane
        // (see Tools/IconViewer.cs's ToolBase.GetPane()) is docked directly into
        // MainWindow's DockingManager instead; nothing here needs to build a standalone
        // docking surface for a lone pane.

        // Scrollable element handling
        if (IsScrollableElement(element))
        {
            // If it already handles internal scrolling or is a ScrollViewer, return as-is
            if (element is ScrollViewer || HasNativeScrollHost(element))
            {
                return element;
            }

            return new ScrollViewer
                   {
                       Content                       = element,
                       HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                       VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                       Focusable                     = false
                   };
        }

        return element;
    }

    private static bool TryBuildFromAttributes(TModel model, out UIElement element)
    {
        var modelType = model.GetType();

        // UIHintAttribute support (e.g., [UIHint("MultilineText")], [UIHint("RadGridView")])
        var uiHint = modelType.GetCustomAttribute<UIHintAttribute>();
        if (uiHint is not null)
        {
            switch (uiHint.UIHint.ToLowerInvariant())
            {
                case "multilinetext":
                case "document":
                case "code":
                    var textBox = new TextBox
                                  {
                                      Text                          = model.ToString(),
                                      AcceptsReturn                 = true,
                                      AcceptsTab                    = true,
                                      TextWrapping                  = TextWrapping.Wrap,
                                      IsReadOnly                    = true,
                                      VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                                      HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
                                  };
                    element = textBox;
                    return true;

                // "radgridview" is kept as a recognized hint string (harmless now that it
                // builds a plain DataGrid instead) so any existing [UIHint("RadGridView")]
                // attribute elsewhere in the app keeps working unchanged.
                case "grid":
                case "datagrid":
                case "radgridview":
                    if (model is IEnumerable gridItems)
                    {
                        var grid = new DataGrid
                                   {
                                       ItemsSource         = gridItems,
                                       AutoGenerateColumns = true,
                                       IsReadOnly          = true,
                                       CanUserSortColumns  = true,
                                       HorizontalAlignment = HorizontalAlignment.Stretch,
                                       VerticalAlignment   = VerticalAlignment.Stretch
                                   };
                        element = grid;
                        return true;
                    }

                    break;
            }
        }

        // DataTypeAttribute support (e.g., [DataType(DataType.MultilineText)], [DataType(DataType.Html)])
        var dataTypeAttr = modelType.GetCustomAttribute<DataTypeAttribute>();
        if (dataTypeAttr is not null)
        {
            switch (dataTypeAttr.DataType)
            {
                case DataType.MultilineText:
                case DataType.Html:
                case DataType.Text:
                    element = new TextBox
                              {
                                  Text                        = model.ToString(),
                                  TextWrapping                = TextWrapping.Wrap,
                                  AcceptsReturn               = true,
                                  IsReadOnly                  = true,
                                  VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                              };
                    return true;

                case DataType.ImageUrl:
                    var image = new Image
                                {
                                    HorizontalAlignment = HorizontalAlignment.Center,
                                    VerticalAlignment   = VerticalAlignment.Center
                                };
                    if (Uri.TryCreate(model.ToString(), UriKind.RelativeOrAbsolute, out var uri))
                    {
                        image.Source = new System.Windows.Media.Imaging.BitmapImage(uri);
                    }

                    element = image;
                    return true;
            }
        }

        element = null!;
        return false;
    }

    /// <summary>
    ///     Builds a simple label/value-editor grid for an arbitrary object's public
    ///     read/write properties via reflection, wrapped in a vertically-scrolling
    ///     <see cref="ScrollViewer"/>. Replaces the Telerik RadPropertyGrid fallback that
    ///     used to be built here - plainer (no categorization, search box, or description
    ///     panel), but dependency-free and editable: a writable property gets a live
    ///     editor (CheckBox for bool, ComboBox for enum, TextBox for strings/numbers, with
    ///     the value written back via reflection on LostFocus/selection-change); anything
    ///     else - read-only properties, and anything of a type this doesn't know how to
    ///     edit - gets a plain read-only text label instead.
    /// </summary>
    private static UIElement BuildPropertyEditor(object model)
    {
        var grid = new Grid { Margin = new Thickness(8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var props = model.GetType()
                          .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                          .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead)
                          .OrderBy(p => p.Name)
                          .ToList();

        int row = 0;
        foreach (var prop in props)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
                        {
                            Text              = prop.Name,
                            Margin            = new Thickness(0, 0, 8, 6),
                            VerticalAlignment = VerticalAlignment.Center,
                            FontWeight        = FontWeights.SemiBold
                        };
            Grid.SetRow(label, row);
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            object? value;
            try
            {
                value = prop.GetValue(model);
            }
            catch
            {
                value = null;
            }

            bool writable = prop.CanWrite && prop.GetCustomAttribute<ReadOnlyAttribute>() is not { IsReadOnly: true };

            var editor = BuildPropertyValueEditor(model, prop, value, writable);
            editor.Margin = new Thickness(0, 0, 0, 6);
            Grid.SetRow(editor, row);
            Grid.SetColumn(editor, 1);
            grid.Children.Add(editor);

            row++;
        }

        return new ScrollViewer
               {
                   Content                       = grid,
                   VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                   HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
               };
    }

    private static FrameworkElement BuildPropertyValueEditor(object model, PropertyInfo prop, object? value, bool writable)
    {
        void SetBack(object? v)
        {
            try
            {
                prop.SetValue(model, v);
            }
            catch
            {
                // best-effort - leave the model unchanged if the value can't be set
            }
        }

        if (!writable)
        {
            return new TextBlock { Text = value?.ToString() ?? "(null)", VerticalAlignment = VerticalAlignment.Center };
        }

        var pt = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;

        if (pt == typeof(bool))
        {
            var cb = new CheckBox { IsChecked = value as bool?, VerticalAlignment = VerticalAlignment.Center };
            cb.Checked   += (_, _) => SetBack(true);
            cb.Unchecked += (_, _) => SetBack(false);
            return cb;
        }

        if (pt.IsEnum)
        {
            var combo = new ComboBox
                        {
                            ItemsSource       = Enum.GetValues(pt),
                            SelectedItem      = value,
                            VerticalAlignment = VerticalAlignment.Center
                        };
            combo.SelectionChanged += (_, _) => SetBack(combo.SelectedItem);
            return combo;
        }

        if (pt == typeof(string) || IsSimpleNumeric(pt))
        {
            var tb = new TextBox
                     {
                         Text              = value?.ToString() ?? string.Empty,
                         VerticalAlignment = VerticalAlignment.Center
                     };
            tb.LostFocus += (_, _) =>
                            {
                                try
                                {
                                    object? converted = pt == typeof(string)
                                        ? tb.Text
                                        : Convert.ChangeType(tb.Text, pt, CultureInfo.InvariantCulture);
                                    SetBack(converted);
                                }
                                catch
                                {
                                    // leave the underlying value unchanged on a bad parse
                                }
                            };
            return tb;
        }

        if (value is IEnumerable en and not string)
        {
            int count = 0;
            foreach (var _ in en) count++;
            return new TextBlock
                   {
                       Text              = $"({prop.PropertyType.Name}, {count} item{(count == 1 ? "" : "s")})",
                       FontStyle         = FontStyles.Italic,
                       VerticalAlignment = VerticalAlignment.Center
                   };
        }

        return new TextBlock { Text = value?.ToString() ?? "(null)", VerticalAlignment = VerticalAlignment.Center };
    }

    private static bool IsSimpleNumeric(Type t)
    {
        return t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) ||
               t == typeof(double) || t == typeof(float) || t == typeof(decimal) ||
               t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(sbyte);
    }

    private static bool IsScrollableElement(UIElement element)
    {
        return element switch
               {
                   ScrollViewer             => true,
                   IScrollInfo              => true,
                   FlowDocumentScrollViewer => true,
                   Canvas                   => true,
                   Viewbox                  => false,
                   _                        => false
               };
    }

    private static bool HasNativeScrollHost(UIElement element)
    {
        // WPF native controls that manage their own internal VirtualizingPanel/ScrollHost
        return element switch
               {
                   DataGrid => true,
                   ListBox  => true,
                   TreeView => true,
                   _        => false
               };
    }
}

public class RegisterAttribute : Attribute
{
    public Type    ServiceType { get; set; }
    public bool    IsSingleton { get; set; }
    public string? Key         { get; set; }

    public RegisterAttribute(Type type, bool singleton = true, string? key = null)
    {
        ServiceType = type.Required();
        IsSingleton = singleton;
        Key         = key;
    }
}

using System.Collections;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Silo.Commanding;
using Telefrag.DI;
using Telerik.Windows.Controls;

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

        // 3. Fallback: Collections render in RadCollectionEditor
        //if (model is IEnumerable collection and not string)
        //{
        //    var collectionEditor = new RadCollectionEditor
        //                           {
        //                               Source              = collection,
        //                               HorizontalAlignment = HorizontalAlignment.Stretch,
        //                               VerticalAlignment   = VerticalAlignment.Stretch
        //                           };

        //    return collectionEditor;
        //}

        // 4. Fallback: Arbitrary objects render in RadPropertyGrid with rich interactions
        var propertyGrid = new RadPropertyGrid
                           {
                               Item                            = model,
                               AutoGeneratePropertyDefinitions = true,
                               //    PropertyOrderMode               = PropertyOrderMode.Categorized,
                               DescriptionPanelVisibility      = Visibility.Visible,
                               SearchBoxVisibility             = Visibility.Visible,
                               //       SortDirection                   = ListSortDirection.Ascending,
                               LabelColumnWidth                = new GridLength(180),
                               HorizontalAlignment             = HorizontalAlignment.Stretch,
                               VerticalAlignment               = VerticalAlignment.Stretch
                           };

        return propertyGrid;
    }

    private static UIElement WrapVisualElement(UIElement element)
    {
        // Encapsulate RadPane inside RadDocking structure
        if (element is RadPane pane)
        {
            var docking = new RadDocking
                          {
                              HorizontalAlignment = HorizontalAlignment.Stretch,
                              VerticalAlignment   = VerticalAlignment.Stretch
                          };

            var splitContainer = new RadSplitContainer();
            var paneGroup      = new RadPaneGroup();

            paneGroup.Items.Add(pane);
            splitContainer.Items.Add(paneGroup);
            docking.Items.Add(splitContainer);

            return docking;
        }

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

                case "grid":
                case "datagrid":
                case "radgridview":
                    if (model is IEnumerable gridItems)
                    {
                        var grid = new RadGridView
                                   {
                                       ItemsSource         = gridItems,
                                       AutoGenerateColumns = true,
                                       ShowGroupPanel      = true,
                                       IsReadOnly          = true,
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
        // Telerik and WPF native controls that manage their own internal VirtualizingPanel/ScrollHost
        return element switch
               {
                   RadGridView     => true,
                   RadTreeListView => true,
                   RadTreeView     => true,
                   //       RadCollectionEditor => true,
                   RadPropertyGrid => true,
                   ListBox         => true,
                   TreeView        => true,
                   DataGrid        => true,
                   _               => false
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
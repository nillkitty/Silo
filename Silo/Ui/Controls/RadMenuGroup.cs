using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using Telerik.Windows.Controls;

namespace Silo;

[ContentProperty(nameof(Items))]
public class RadMenuGroup : FrameworkElement, IAddChild
{
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(RadMenuGroup), new PropertyMetadata(true, OnIsActiveChanged));

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(RadMenuGroup),
                                    new PropertyMetadata(null, OnItemsSourceChanged));

    public static readonly DependencyProperty ItemContainerStyleProperty =
        DependencyProperty.Register(nameof(ItemContainerStyle), typeof(Style), typeof(RadMenuGroup),
                                    new PropertyMetadata(null, OnStructureChanged));

    public static readonly DependencyProperty ItemTemplateProperty =
        DependencyProperty.Register(nameof(ItemTemplate), typeof(DataTemplate), typeof(RadMenuGroup),
                                    new PropertyMetadata(null, OnStructureChanged));

    private readonly List<UIElement>           _injectedItems = new();
    private          bool                      _isLoadedOrInitialized;
    private          INotifyCollectionChanged? _observableSource;
    private          ItemsControl?             _parentControl;

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public Style? ItemContainerStyle
    {
        get => (Style?)GetValue(ItemContainerStyleProperty);
        set => SetValue(ItemContainerStyleProperty, value);
    }

    public DataTemplate? ItemTemplate
    {
        get => (DataTemplate?)GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    public List<object> Items { get; } = new();

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoadedOrInitialized = true;
        EnsureParent();
        RebuildItems();
    }

    protected override void OnVisualParentChanged(DependencyObject oldParent)
    {
        base.OnVisualParentChanged(oldParent);
        EnsureParent();
        if (DesignerProperties.GetIsInDesignMode(this)) RebuildItems();
    }

    private void EnsureParent()
    {
        if (_parentControl != null) return;

        var current = LogicalTreeHelper.GetParent(this);
        while (current != null)
        {
            if (current is ItemsControl ic)
            {
                _parentControl = ic;
                break;
            }

            current = LogicalTreeHelper.GetParent(current);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachCollectionChanged();
        ClearInjectedItems();
        _parentControl = null;
    }

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildItems();
    }

    private void DetachCollectionChanged()
    {
        if (_observableSource != null)
        {
            _observableSource.CollectionChanged -= OnSourceCollectionChanged;
            _observableSource                   =  null;
        }
    }

    private void ClearInjectedItems()
    {
        if (_parentControl == null) return;

        foreach (var item in _injectedItems) _parentControl.Items.Remove(item);
        _injectedItems.Clear();
    }

    private void RebuildItems()
    {
        EnsureParent();
        if (_parentControl == null) return;

        ClearInjectedItems();

        // Find current position of this placeholder inside parent's items collection
        int myIndex = _parentControl.Items.IndexOf(this);
        if (myIndex < 0) return;

        var toInject = new List<UIElement>();

        // 1. ItemsSource dynamic elements
        if (ItemsSource != null)
            foreach (var dataItem in ItemsSource)
                if (dataItem is UIElement ui)
                {
                    toInject.Add(ui);
                }
                else
                {
                    var mi = new RadMenuItem { DataContext = dataItem };

                    // A locally-set Header always wins over any Style Setter, so only
                    // fall back to the raw data item as the header when no style (and
                    // therefore no Header binding of its own) is supplied.
                    if (ItemContainerStyle != null)
                        mi.Style = ItemContainerStyle;
                    else
                        mi.Header = dataItem;

                    if (ItemTemplate != null)
                        mi.HeaderTemplate = ItemTemplate;

                    toInject.Add(mi);
                }

        // 2. Static declared children
        foreach (var item in Items)
            if (item is UIElement ui)
                toInject.Add(ui);

        // 3. Insert immediately following this placeholder
        var targetVisibility = IsActive ? Visibility.Visible : Visibility.Collapsed;
        int insertAt         = myIndex + 1;

        for (int i = 0; i < toInject.Count; i++)
        {
            var el = toInject[i];
            el.Visibility = targetVisibility;
            _parentControl.Items.Insert(insertAt + i, el);
            _injectedItems.Add(el);
        }
    }

    public RadMenuGroup()
    {
        // RadMenuGroup itself is zero-sized and collapsed so it does not render a blank space
        Visibility = Visibility.Collapsed;
        Width      = 0;
        Height     = 0;

        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // IAddChild ensures children declared in XAML are captured as they are parsed
    public void AddChild(object value)
    {
        Items.Add(value);
        if (_isLoadedOrInitialized) RebuildItems();
    }

    public void AddText(string text)
    {
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var group                                                  = (RadMenuGroup)d;
        var targetVisibility                                       = group.IsActive ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in group._injectedItems) item.Visibility = targetVisibility;
    }

    private static void OnStructureChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((RadMenuGroup)d).RebuildItems();
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var group = (RadMenuGroup)d;
        group.DetachCollectionChanged();

        if (e.NewValue is INotifyCollectionChanged ncc)
        {
            group._observableSource                   =  ncc;
            group._observableSource.CollectionChanged += group.OnSourceCollectionChanged;
        }

        group.RebuildItems();
    }
}
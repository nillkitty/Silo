using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using Telerik.Windows.Controls;

namespace Silo;

public static class MenuExtensions
{
    public static readonly DependencyProperty DynamicItemsSourceProperty =
        DependencyProperty.RegisterAttached("DynamicItemsSource", typeof(IEnumerable), typeof(MenuExtensions),
                                            new PropertyMetadata(null, OnDynamicItemsSourceChanged));

    public static readonly DependencyProperty DynamicItemStyleProperty =
        DependencyProperty.RegisterAttached("DynamicItemStyle", typeof(Style), typeof(MenuExtensions), new PropertyMetadata(null));

    public static readonly DependencyProperty GroupNameProperty =
        DependencyProperty.RegisterAttached("GroupName", typeof(string), typeof(MenuExtensions), new PropertyMetadata(null));

    public static readonly DependencyProperty IsGroupVisibleProperty =
        DependencyProperty.RegisterAttached("IsGroupVisible", typeof(bool), typeof(MenuExtensions),
                                            new PropertyMetadata(true, OnIsGroupVisibleChanged));

    private static readonly DependencyProperty DynamicItemsTrackerProperty =
        DependencyProperty.RegisterAttached("DynamicItemsTracker", typeof(DynamicItemHandler), typeof(MenuExtensions),
                                            new PropertyMetadata(null));

    private static void OnDynamicItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl menu) return;

        var tracker = (DynamicItemHandler?)menu.GetValue(DynamicItemsTrackerProperty);
        if (tracker != null) tracker.Dispose();

        if (e.NewValue is IEnumerable source)
        {
            tracker = new DynamicItemHandler(menu, source);
            menu.SetValue(DynamicItemsTrackerProperty, tracker);
        }
    }

    private static void OnIsGroupVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl parent) return;
        bool isVisible   = (bool)e.NewValue;
        var  targetGroup = GetGroupName(d);

        foreach (var item in parent.Items)
            if (item is DependencyObject depObj)
            {
                var groupName = GetGroupName(depObj);
                if (!string.IsNullOrEmpty(targetGroup) && groupName == targetGroup)
                    if (depObj is UIElement ui)
                        ui.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
            }
    }

    private sealed class DynamicItemHandler
    {
        private readonly List<RadMenuItem> _generated = new();
        private readonly ItemsControl      _menu;
        private readonly IEnumerable       _source;

        public void Dispose()
        {
            if (_source is INotifyCollectionChanged ncc) ncc.CollectionChanged -= OnCollectionChanged;
            Clear();
        }

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            Rebuild();
        }

        private void Clear()
        {
            foreach (var item in _generated) _menu.Items.Remove(item);
            _generated.Clear();
        }

        private void Rebuild()
        {
            Clear();
            var style = GetDynamicItemStyle(_menu);

            foreach (var dataItem in _source)
            {
                var mi = new RadMenuItem
                         {
                             DataContext = dataItem,
                             Header      = dataItem
                         };

                if (style != null) mi.Style = style;

                _generated.Add(mi);
                _menu.Items.Add(mi);
            }
        }

        public DynamicItemHandler(ItemsControl menu, IEnumerable source)
        {
            _menu   = menu;
            _source = source;

            if (_source is INotifyCollectionChanged ncc) ncc.CollectionChanged += OnCollectionChanged;

            Rebuild();
        }
    }

    #region DynamicItemsSource

    public static IEnumerable? GetDynamicItemsSource(DependencyObject obj)
    {
        return (IEnumerable?)obj.GetValue(DynamicItemsSourceProperty);
    }

    public static void SetDynamicItemsSource(DependencyObject obj, IEnumerable? value)
    {
        obj.SetValue(DynamicItemsSourceProperty, value);
    }

    #endregion

    #region DynamicItemStyle

    public static Style? GetDynamicItemStyle(DependencyObject obj)
    {
        return (Style?)obj.GetValue(DynamicItemStyleProperty);
    }

    public static void SetDynamicItemStyle(DependencyObject obj, Style? value)
    {
        obj.SetValue(DynamicItemStyleProperty, value);
    }

    #endregion

    #region GroupName & GroupVisibility

    public static string? GetGroupName(DependencyObject obj)
    {
        return (string?)obj.GetValue(GroupNameProperty);
    }

    public static void SetGroupName(DependencyObject obj, string? value)
    {
        obj.SetValue(GroupNameProperty, value);
    }


    public static bool GetIsGroupVisible(DependencyObject obj)
    {
        return (bool)obj.GetValue(IsGroupVisibleProperty);
    }

    public static void SetIsGroupVisible(DependencyObject obj, bool value)
    {
        obj.SetValue(IsGroupVisibleProperty, value);
    }

    #endregion
}
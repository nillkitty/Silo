using System.Windows.Markup;

namespace Silo.DbModel;

public class Item : DependencyObject
{
}

[ContentProperty(nameof(Items))]
public class ItemContainer<TChildren> : Item
{
    public List<TChildren> Items { get; set; } = [];
}
using System.Windows;

namespace Silo.Nav;

public abstract record AppView(string Name, Func<UIElement> ContentBuilder)
{
    /// <summary>
    ///     The document tab this view opens in. Null: <c>"view:" + Name</c>. Set it when two views share a name (the
    ///     Settings ▸ Silo Settings ▸ Providers page and Connections ▸ Providers, say), so each gets a tab of its own.
    /// </summary>
    public string? Key { get; init; }
}

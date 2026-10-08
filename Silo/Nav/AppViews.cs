using System.Windows;

namespace Silo.Nav;

/// <summary>
///     The one concrete <see cref="AppView" /> the app currently needs: a view that's just a
///     name plus a content factory, with no behavior of its own beyond that. Kept in its own
///     file (rather than alongside <see cref="AppView" /> itself) so that one can stay a pure
///     contract -- this is where a second kind of view (one backed by its own window instead of
///     inline content, say) would eventually join it.
/// </summary>
public sealed record SimpleAppView(string Name, Func<UIElement> ContentBuilder) : AppView(Name, ContentBuilder);

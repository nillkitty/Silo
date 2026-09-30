using System.Windows;
using System.Windows.Media;
using Telefrag.DI;
using Telefrag.Exceptions;

namespace Silo.Extensions;

public static class Extensions
{
    extension(Container c)
    {
        public TService Require<TService>(string? name = null) where TService : class
        {
            return c.Resolve<TService>(name, true) ??
                   throw new RequiredComponentMissingException($"Required component '{typeof(TService)}' is not registered.");
        }
    }

    extension(IEnumerable<string> ss)
    {
        public string Join(char c)
        {
            return string.Join(c, ss);
        }
    }

    extension(Type type)
    {
        /// <summary>
        ///     A short, human-friendly display name for a type - "List&lt;Int32&gt;" rather
        ///     than the raw reflection name ("List`1[System.Int32]" for
        ///     <see cref="Type.Name" />, or the fully-qualified "System.Collections.Generic.List`1[[System.Int32...]]"
        ///     for <see cref="Type.FullName" />).
        /// </summary>
        /// <remarks>
        ///     Previously resolved via <c>Microsoft.EntityFrameworkCore.Infrastructure.TypeExtensions.ShortDisplayName</c>,
        ///     which came in transitively through <c>Telefrag.Common</c>'s own EF Core
        ///     dependency. Removing the Telerik packages (several of which also depended on
        ///     EF Core, and evidently pinned the transitive version up) let NuGet re-resolve
        ///     that transitive dependency down to a version where the method no longer
        ///     resolves, which is what broke the 9 call sites using it
        ///     (<see cref="Silo.App.DiscoverRegistrations" />, <c>ModelControl&lt;TModel&gt;</c>,
        ///     <c>FormWindow.PromptFor</c>). Rather than depend on wherever EF Core's version
        ///     happens to land next, this is a small self-contained replacement - only used
        ///     for diagnostic logging and a couple of UI labels, so exact formatting parity
        ///     with EF Core's implementation isn't load-bearing anywhere.
        /// </remarks>
        public string ShortDisplayName()
        {
            if (!type.IsGenericType) return type.Name;

            string name         = type.Name;
            int    tick         = name.IndexOf('`');
            if (tick >= 0) name = name[..tick];

            var args = type.GetGenericArguments().Select(a => a.ShortDisplayName());
            return $"{name}<{string.Join(", ", args)}>";
        }
    }

    extension(DependencyObject start)
    {
        /// <summary>
        ///     Walks up the visual tree - falling back to the logical tree wherever the
        ///     visual tree doesn't apply (e.g. across a Popup boundary) - from
        ///     <paramref name="start" /> (inclusive) looking for the nearest ancestor of
        ///     type <typeparamref name="T" />, or <see langword="null" /> if none is found.
        ///     Replaces Telerik's <c>ParentOfType&lt;T&gt;</c> extension method (see
        ///     <see cref="Silo.Handlers" />). The same walk (Visual/Visual3D check before
        ///     calling <see cref="VisualTreeHelper.GetParent" />, else
        ///     <see cref="LogicalTreeHelper.GetParent" />, with a cycle guard) used by
        ///     <see cref="Silo.Design.DesignMenu" />'s ancestor collection.
        /// </summary>
        public T? ParentOfType<T>() where T : DependencyObject
        {
            var current = start;
            var seen    = new HashSet<DependencyObject>();
            while (current != null && seen.Add(current))
            {
                if (current is T match) return match;
                current = current is Visual
                              ? VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current)
                              : LogicalTreeHelper.GetParent(current);
            }

            return null;
        }
    }
}
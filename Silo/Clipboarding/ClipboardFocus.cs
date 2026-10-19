using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Silo.Clipboarding;

/// <summary>
///     Works out what Copy, Cut and Paste mean where the person is: from the element that has the focus up through its
///     parents, the first object that is <see cref="ICopyable" /> is what is copied, and every <see cref="IPastable" /> on the
///     way (the focused one first) is offered the paste in turn. An element, or the object it shows (its DataContext), counts;
///     a control that knows better says so with <see cref="IClipboardSurface" />.
/// </summary>
public static class ClipboardFocus
{
    public static ClipboardContext Resolve(DependencyObject? from)
    {
        IReadOnlyList<ICopyable> selection = [];
        var                      targets   = new List<IPastable>();
        var                      seen      = new HashSet<DependencyObject>();

        void AddTarget(IPastable p)
        {
            if (!targets.Any(t => ReferenceEquals(t, p))) targets.Add(p);
        }

        for (var d = from; d is not null && seen.Add(d); d = Parent(d))
        {
            foreach (var owner in Owners(d))
            {
                if (owner is IClipboardSurface surface)
                {
                    var c = surface.GetClipboardContext();
                    if (selection.Count == 0) selection = c.Selection;
                    foreach (var t in c.Targets) AddTarget(t);
                    continue;
                }

                if (selection.Count == 0 && owner is ICopyable copyable) selection = [copyable];
                if (owner is IPastable pastable) AddTarget(pastable);
            }
        }

        return new ClipboardContext(selection, targets);
    }

    private static IEnumerable<object> Owners(DependencyObject d)
    {
        yield return d;

        object? context = d switch
                          {
                              FrameworkElement fe        => fe.DataContext,
                              FrameworkContentElement fc => fc.DataContext,
                              _                          => null
                          };
        if (context is not null) yield return context;
    }

    private static DependencyObject? Parent(DependencyObject d)
    {
        return d is Visual or Visual3D ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
    }
}

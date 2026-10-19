using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Serilog;
using Silo.Toolbar;

namespace Silo.Commanding;

/// <summary>
///     Implemented by a node that stands for a model object (an Explorer node, a navigation node): the model is what the
///     node's
///     right-click menu is about, the way a data-bound element's <c>DataContext</c> is.
/// </summary>
public interface IModelHolder
{
    /// <summary>The object the node stands for, or null.</summary>
    object? Model { get; }
}

/// <summary>An <see cref="ICommandProvider" /> made from a function, for commands written next to the UI that offers them.</summary>
public sealed class CommandList(Func<IEnumerable<ProvidedCommand>> commands) : ICommandProvider
{
    /// <inheritdoc />
    public IEnumerable<ProvidedCommand> GetCommands()
    {
        return commands();
    }
}

/// <summary>
///     Builds every right-click menu in the app the same way -- for an Explorer node, a navigation node, a data-bound item
///     in a
///     list, or the blank area of a container -- from <see cref="ICommandProvider" />s and
///     <see cref="Silo.Providers.Action.IActionProvider{TNodeType}" />s. For a target, these are asked, in this order:
///     <list type="number">
///         <item>
///             the target node: its attached <see cref="CommandsProperty" /> (or, for an item container, its list's
///             <see cref="ItemCommandsProperty" />), and the node itself if it is an <see cref="ICommandProvider" />;
///         </item>
///         <item>
///             the node's data context -- an element's <c>DataContext</c>, an <see cref="IModelHolder" />'s
///             <see cref="IModelHolder.Model" /> -- and so on down while there is one (a tree item, then the Explorer node
///             it
///             shows, then that node's model); the last one found is the model (the node itself when it has no data
///             context);
///         </item>
///         <item>
///             the <see cref="Silo.Providers.Action.IActionProvider{TNodeType}" />s registered for the model's type, then
///             for each
///             of its base classes, then for its interfaces (see <see cref="ActionProviders" />).
///         </item>
///     </list>
///     Each provider is asked once. The commands are then put together by group, groups in the order they were first seen,
///     except that the node's built-in actions (<see cref="NodeCommand.ActionsGroup" />) and
///     <see cref="ProvidedCommand.LastGroup" />
///     go last.
/// </summary>
public static class ContextCommands
{
    private const int MaxChain = 6;

    /// <summary>Commands offered for this element itself (an item, or a container for its blank area).</summary>
    public static readonly DependencyProperty CommandsProperty =
        DependencyProperty.RegisterAttached("Commands", typeof(ICommandProvider), typeof(ContextCommands));

    /// <summary>Set on a list or tree: commands offered for each of its item containers (they usually act on the selection).</summary>
    public static readonly DependencyProperty ItemCommandsProperty =
        DependencyProperty.RegisterAttached("ItemCommands", typeof(ICommandProvider), typeof(ContextCommands));

    /// <summary>
    ///     Marks an element whose right-click menu <see cref="Attach" /> builds (what the Ctrl+Shift inspector looks
    ///     for).
    /// </summary>
    private static readonly DependencyProperty IsHostProperty =
        DependencyProperty.RegisterAttached("IsHost", typeof(bool), typeof(ContextCommands));

    /// <summary>
    ///     The target and its data contexts, outermost first: an element, its <c>DataContext</c>, that object's model, and so
    ///     on. The last is the model the type-registered providers are asked about.
    /// </summary>
    public static IReadOnlyList<object> Chain(object? target)
    {
        var chain = new List<object>();
        for (var current = target; current is not null && chain.Count < MaxChain;)
        {
            chain.Add(current);
            object? next = current switch
                           {
                               IModelHolder h            => h.Model,
                               FrameworkElement fe       => fe.DataContext,
                               FrameworkContentElement f => f.DataContext,
                               _                         => null
                           };
            if (next is null || chain.Any(c => ReferenceEquals(c, next))) break;
            current = next;
        }

        return chain;
    }

    /// <summary>The model a target stands for: its innermost data context, or the target itself when it has none.</summary>
    public static object? ModelOf(object? target)
    {
        return Chain(target).LastOrDefault();
    }

    /// <summary>The commands for <paramref name="target" /> (see the class remarks); empty for null.</summary>
    public static IReadOnlyList<NodeCommand> For(object? target)
    {
        var result = new List<NodeCommand>();
        var chain  = Chain(target);
        var asked  = new HashSet<object>(ReferenceEqualityComparer.Instance);

        void Ask(ICommandProvider? provider)
        {
            if (provider is null || !asked.Add(provider)) return;
            try
            {
                foreach (var c in provider.GetCommands()) result.Add(NodeCommand.From(c));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Getting the commands of {provider} failed", provider.GetType().Name);
            }
        }

        foreach (var o in chain)
        {
            if (o is DependencyObject d)
            {
                Ask(GetCommands(d));
                // an item container: the commands its list (or tree, however deeply nested) offers for every item
                for (var owner = ItemsControl.ItemsControlFromItemContainer(d);
                     owner is not null;
                     owner = ItemsControl.ItemsControlFromItemContainer(owner))
                    if (GetItemCommands(owner) is { } itemCommands)
                    {
                        Ask(itemCommands);
                        break;
                    }
            }

            Ask(o as ICommandProvider);
        }

        try
        {
            foreach (var c in ActionProviders.CommandsFor(chain.LastOrDefault())) result.Add(NodeCommand.From(c));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Getting the registered commands of {model} failed", chain.LastOrDefault()?.GetType().Name);
        }

        return Order(result);
    }

    /// <summary>One block per group, in the order the groups first appear; built-in actions, then the last group, at the end.</summary>
    public static IReadOnlyList<NodeCommand> Order(IEnumerable<NodeCommand> commands)
    {
        return commands.GroupBy(c => c.Group)
                       .OrderBy(g => g.Key == ProvidedCommand.LastGroup ? 2 : g.Key == NodeCommand.ActionsGroup ? 1 : 0)
                       .SelectMany(g => g)
                       .ToList();
    }

    /// <summary>
    ///     Gives <paramref name="host" /> (a tree, a list, any container) a right-click menu built by <see cref="For" />: for
    ///     the
    ///     item under the mouse (which is selected first), or for the host itself when the blank area was clicked. No menu
    ///     appears when nothing is offered.
    /// </summary>
    /// <param name="host">The container.</param>
    /// <param name="extras">Entries to add after the commands (Cut / Copy / Paste, say), given the target; may be null.</param>
    public static void Attach(FrameworkElement host, Func<object, IEnumerable<Control>>? extras = null)
    {
        host.ContextMenu ??= new ContextMenu();
        host.SetValue(IsHostProperty, true);

        host.PreviewMouseRightButtonDown += (_, e) =>
                                            {
                                                switch (TargetFrom(host, e.OriginalSource))
                                                {
                                                    case TreeViewItem tvi:
                                                        tvi.IsSelected = true;
                                                        tvi.Focus();
                                                        break;
                                                    case ListBoxItem lbi:
                                                        lbi.IsSelected = true;
                                                        lbi.Focus();
                                                        break;
                                                }
                                            };

        host.ContextMenuOpening += (_, e) =>
                                   {
                                       var menu = host.ContextMenu!;
                                       menu.Items.Clear();
                                       object target = TargetFrom(host, e.OriginalSource);

                                       try
                                       {
                                           foreach (var entry in CommandMenu.Items(For(target))) menu.Items.Add(entry);
                                       }
                                       catch (Exception ex)
                                       {
                                           Log.Warning(ex, "Building a context menu failed");
                                       }

                                       var more = extras?.Invoke(target).ToList() ?? [];
                                       if (more.Count > 0)
                                       {
                                           if (menu.Items.Count > 0) menu.Items.Add(new Separator());
                                           foreach (var item in more) menu.Items.Add(item);
                                       }

                                       if (menu.Items.Count == 0) e.Handled = true; // nothing to offer: no menu at all
                                   };
    }

    /// <summary>
    ///     What a right-click inside <paramref name="host" /> is about: the nearest item container (or element with commands
    ///     of
    ///     its own) above the clicked element, else the nearest element showing a different data context than the host, else
    ///     the host (its blank area).
    /// </summary>
    public static FrameworkElement TargetFrom(FrameworkElement host, object? source)
    {
        FrameworkElement? different = null;
        for (var d = source as DependencyObject; d is not null && !ReferenceEquals(d, host); d = Parent(d))
        {
            if (d is not FrameworkElement fe) continue;
            if (GetCommands(fe) is not null || fe is ICommandProvider ||
                ItemsControl.ItemsControlFromItemContainer(fe) is not null) return fe;
            if (different is null && !ReferenceEquals(fe.DataContext, host.DataContext)) different = fe;
        }

        return different ?? host;
    }

    private static string Describe(object o)
    {
        string name = o is FrameworkElement { Name.Length: > 0 } fe ? " \"" + fe.Name + "\"" : "";
        return TypeName(o.GetType()) + name;
    }

    private static string TypeName(Type t)
    {
        if (!t.IsGenericType) return t.Name;
        string bare = t.Name[..t.Name.IndexOf('`')];
        return bare + "<" + string.Join(", ", t.GetGenericArguments().Select(TypeName)) + ">";
    }

    public static ICommandProvider? GetCommands(DependencyObject d)
    {
        return (ICommandProvider?)d.GetValue(CommandsProperty);
    }

    public static void SetCommands(DependencyObject d, ICommandProvider? value)
    {
        d.SetValue(CommandsProperty, value);
    }

    /// <summary>
    ///     What the right-click menu of <paramref name="element" /> is built from, as lines of text: the target, every object
    ///     asked for commands (and which way it offers them), the model, and every type checked for registered providers (with
    ///     the providers found). For the Ctrl+Shift inspector (<see cref="ContextMenuInspector" />).
    /// </summary>
    public static IReadOnlyList<string> Explain(DependencyObject element)
    {
        var    lines  = new List<string>();
        var    host   = HostOf(element);
        object target = host is null ? element : TargetFrom(host, element);

        lines.Add("This:  " + Describe(target) + (host is null                  ? "   (no shared menu here; showing what one would use)" :
                                                  ReferenceEquals(target, host) ? "   (blank area)" : ""));
        if (host is not null && !ReferenceEquals(host, target)) lines.Add("  In:  " + Describe(host));

        lines.Add("");
        lines.Add("Command Discovery Order:");
        var chain = Chain(target);
        for (int i = 0; i < chain.Count; i++)
        {
            var o    = chain[i];
            var ways = new List<string>();
            if (o is DependencyObject d)
            {
                if (GetCommands(d) is { } attached) ways.Add("attached Commands (" + attached.GetType().Name + ")");
                for (var owner = ItemsControl.ItemsControlFromItemContainer(d);
                     owner is not null;
                     owner = ItemsControl.ItemsControlFromItemContainer(owner))
                    if (GetItemCommands(owner) is { } itemCommands)
                    {
                        ways.Add("ItemCommands of " + owner.GetType().Name + " (" + itemCommands.GetType().Name + ")");
                        break;
                    }
            }

            if (o is ICommandProvider) ways.Add("ICommandProvider");
            string role = i == 0 ? "target" : chain[i - 1] is IModelHolder ? "Model" : "DataContext";
            lines.Add($"  {i + 1}. {Describe(o)}  [{role}]  " + (ways.Count > 0 ? "\u2713 " + string.Join(", ", ways) : "\u2013 none"));
        }

        var model = chain.LastOrDefault();
        // lines.Add("");
        lines.Insert(2, "Model: " + (model is null ? "(none)" : model.GetType().FullName));
        if (model is null) return lines;
        //lines.Add("");

        lines.Add("Registered Provider Order:");
        foreach (var (type, providers) in ActionProviders.Lookup(model.GetType()))
            lines.Add("  " + TypeName(type) + "  " + (providers.Count > 0
                                                          ? "\u2713 " + string.Join(", ", providers.Select(p => p.GetType().Name))
                                                          : "\u2013"));

        return lines;
    }

    /// <summary>The nearest element at or above <paramref name="d" /> whose menu <see cref="Attach" /> builds, or null.</summary>
    public static FrameworkElement? HostOf(DependencyObject? d)
    {
        for (; d is not null; d = Parent(d))
            if (d is FrameworkElement fe && (bool)fe.GetValue(IsHostProperty))
                return fe;
        return null;
    }

    public static ICommandProvider? GetItemCommands(DependencyObject d)
    {
        return (ICommandProvider?)d.GetValue(ItemCommandsProperty);
    }

    public static void SetItemCommands(DependencyObject d, ICommandProvider? value)
    {
        d.SetValue(ItemCommandsProperty, value);
    }

    private static DependencyObject? Parent(DependencyObject d)
    {
        return d is Visual or Visual3D ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
    }
}
using System.Collections.ObjectModel;
using Serilog;
using Silo.DbModel.Global;

namespace Silo.ViewModel;

public record SiloNode
{
    private Task? _loading;

    /// <summary>
    ///     Optional: produces the node's children the first time they are wanted (when the node is expanded), so a tree such
    ///     as
    ///     a file system is only read as far as the person goes. Null for a node whose <see cref="Children" /> are already
    ///     all there. The children it returns are added to <see cref="Children" /> by <see cref="LoadChildrenAsync" />.
    /// </summary>
    public Func<CancellationToken, Task<IReadOnlyList<SiloNode>>>? ChildLoader { get; init; }

    public ObservableCollection<SiloNode> Children { get; }

    /// <summary>
    ///     True once <see cref="ChildLoader" /> has run (or there is none); a failed load counts, see
    ///     <see cref="LoadError" />.
    /// </summary>
    public bool ChildrenLoaded { get; private set; }

    /// <summary>
    ///     The model behind the node, and exactly the object the node was made with: the Explorer looks on it for the item's
    ///     details, view and menu (<c>INodeDetails</c>, <c>INodeViewProvider</c>, <c>ICommandProvider</c>, and the
    ///     <c>IActionProvider&lt;T&gt;</c>s of its type). For a node made for an <see cref="INodeProvider" /> that is the provider
    ///     itself, not the provider's <see cref="INodeProvider.Data" /> (see <see cref="Data" />).
    /// </summary>
    public object DataContext { get; protected init; }

    /// <summary>
    ///     The raw thing the node stands for: the <see cref="INodeProvider.Data" /> of its provider (a <c>FileSystemInfo</c>,
    ///     say), or the <see cref="DataContext" /> itself when that is not a provider.
    /// </summary>
    public object? Data => DataContext is INodeProvider provider ? provider.Data : DataContext;

    public int?   Expand      { get; protected set; }
    public Uri?   Icon        { get; protected init; }

    public bool IsExpanded
    {
        get => Expand > 0;
        set
        {
            if (value)
                Expand = 1;
            else
                Expand = 0;
        }
    }

    public bool IsSelected { get; set; }

    public bool IsVisible => Expand > 0;

    /// <summary>Why the last <see cref="ChildLoader" /> run failed (an unreadable folder, say), else null.</summary>
    public string? LoadError { get; private set; }

    public object? Parent   { get; protected set; }
    public string? Text     { get; protected init; }
    public string? TypeName { get; protected init; }
    public Uri     Uri      { get; protected init; }

    /// <summary>
    ///     Runs the <see cref="ChildLoader" /> once and adds what it returns to <see cref="Children" />. Calling it again
    ///     while it is running waits for that run; afterwards it does nothing. Call it from the UI thread (the children are
    ///     added on the calling context). A failure is kept in <see cref="LoadError" /> and shown as a single child saying
    ///     what went wrong, rather than thrown.
    /// </summary>
    public Task LoadChildrenAsync(CancellationToken cancel = default)
    {
        if (ChildLoader is null || ChildrenLoaded) return Task.CompletedTask;

        return _loading ??= LoadCoreAsync(cancel);
    }

    /// <summary>
    ///     Forgets the children read so far and reads them again with the <see cref="ChildLoader" /> (Refresh Contents). Does
    ///     nothing for a node whose children are not read on demand. Call it from the UI thread.
    /// </summary>
    public Task ReloadChildrenAsync(CancellationToken cancel = default)
    {
        if (ChildLoader is null) return Task.CompletedTask;

        Children.Clear();
        ChildrenLoaded = false;
        LoadError      = null;
        return LoadChildrenAsync(cancel);
    }

    private async Task LoadCoreAsync(CancellationToken cancel)
    {
        try
        {
            var loaded = await ChildLoader!(cancel);
            foreach (var child in loaded)
                Children.Add(child);
            ChildrenLoaded = true;
        }
        catch (OperationCanceledException)
        {
            // cancelled: leave it unloaded so a later expansion tries again
        }
        catch (Exception ex)
        {
            LoadError      = ex.Message;
            ChildrenLoaded = true;
            Log.Warning(ex, "Loading the children of {Node} failed", Text);
            Children.Add(new SiloNode(ex.Message, ex.Message, null, null, null));
        }
        finally
        {
            _loading = null;
        }
    }

    public bool Remove()
    {
        if (IsVisible && Parent is SiloNode { } p) return p.Children.Remove(this);

        return false;
    }


    public SiloNode(Node src) : this(src.Required(), src.Name, src.Uri, src.IconUri, src.Expanded)
    {
    }

    // There is deliberately no constructor that takes an INodeProvider. With one, `new SiloNode(provider)` and
    // `new SiloNode((object)provider)` would pick different overloads for the same object, and a plain constructor taking the
    // provider's Data would hide the provider from the Explorer. Every constructor makes DataContext the object it is given;
    // read a provider's raw data through Data.

    /// <summary>A node whose <see cref="DataContext" /> is <paramref name="dataContext" />, whatever its type (a provider included).</summary>
    public SiloNode(object dataContext, int? expand = null) : this(dataContext, null, null, null, expand)
    {
    }

    public SiloNode(object dataContext, string? text, Uri? uri, Uri? icon, int? expand)
    {
        DataContext = dataContext;
        Text        = text;
        Uri         = uri;
        Icon        = icon;
        Expand      = expand;
        Children    = [];
        Children.CollectionChanged += (sender, args) =>
                                      {
                                          if (args is null)
                                              return;

                                          if (args.NewItems != null)
                                              foreach (var x in args.NewItems)
                                                  if (x is SiloNode n)
                                                      n.Parent = this;

                                          // a removed child no longer points back (unless it already moved to another parent)
                                          if (args.OldItems != null)
                                              foreach (var x in args.OldItems)
                                                  if (x is SiloNode n && ReferenceEquals(n.Parent, this))
                                                      n.Parent = null;
                                      };
    }
}
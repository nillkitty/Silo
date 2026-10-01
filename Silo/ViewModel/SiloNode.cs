using System.Collections.ObjectModel;
using Silo.DbModel.Global;

namespace Silo.ViewModel;

public record SiloNode
{
    public ObservableCollection<SiloNode> Children { get; }

    public object DataContext { get; protected init; }
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

    public bool    IsVisible => Expand > 0;
    public object? Parent    { get; protected set; }
    public string? Text      { get; protected init; }
    public Uri     Uri       { get; protected init; }

    public bool Remove()
    {
        if (IsVisible && Parent is SiloNode { } p) return p.Children.Remove(this);

        return false;
    }


    public SiloNode(Node src) : this(src.Required(), src.Name, src.Uri, src.IconUri, src.Expanded)
    {
    }

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
                                          if (args?.NewItems is null)
                                              return;

                                          foreach (var x in args.NewItems)
                                              if (x is SiloNode n)
                                                  n.Parent = this;
                                          if (args.OldItems != null)
                                              foreach (var x in args.OldItems)
                                                  if (x is SiloNode n)
                                                      n.Parent = null;
                                      };
    }
}
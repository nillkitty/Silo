using System.DirectoryServices;
using Silo.ViewModel;

namespace Silo.Connectors.Ldap;

public record LdapNode : SiloNode
{
    public LdapModel Model { get; init; }

    public LdapNode(LdapModel model, int? expand = null) : base(model, expand)
    {
        Model    = model;
        Text     = model.Label;
        Icon     = model.Icon;
        TypeName = model.TypeName;
        Uri      = model.Uri;

        // a container reads what is in it when it is expanded
        if (model.HasChildren) ChildLoader = model.LoadChildNodesAsync;
    }
}

public static class LdapExtensions
{
    extension(DirectoryEntry e)
    {
        public TValue? GetValue<TValue>(string propName, TValue? defaultValue = default)
        {
            // a property the entry does not have is an empty collection (not null): indexing it would throw
            if (e.Properties[propName] is { Count: > 0 } pp)
            {
                if (pp.Value is TValue tv) return tv;
                if (pp[0] is TValue tvv) return tvv;
            }

            return defaultValue;
        }
    }
}
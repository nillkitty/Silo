using System.DirectoryServices;
using Silo.ViewModel;

namespace Silo.Connectors.Ldap;

public record LdapNode : SiloNode
{
    public LdapModel Model { get; init; }

    public LdapNode(LdapModel model) : base(model)
    {
        Model    = model;
        Text     = model.Name;
        Icon     = model.Icon;
        TypeName = model.TypeName;
        Uri      = model.Uri;
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
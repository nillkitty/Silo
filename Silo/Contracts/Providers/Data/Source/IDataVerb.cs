using Silo.Contracts.ConnectorServices;
using Silo.Contracts.Providers.Data.Shape;

namespace Silo.Contracts.Providers.Data.Source;

/// <summary>
///     Defines a data verb which can be used on a <see cref="IDataProvider" />
///     to ultimately arrive at a <see cref="IDataSource" />.
/// </summary>
/// <remarks>
///     Executing the verb <see cref="IDataProvider" /> if more arguments are required
///     (parametric/open) or a <see cref="IDataSource" /> if all arguments are
///     satisfied (resolved).
/// </remarks>
/// <param name="Type"></param>
/// <param name="VerbKey"></param>
/// <param name="DisplayName"></param>
public abstract record DataVerb(
    Uri           SourceUri, // current Uri for this node
    IDataProvider Owner,     // up to the owning provider
    IType         Type,      // the type of data at this node
    string        VerbKey,   // unique key for this verb (under that owner)
    string        DisplayName) : IDisplayName
{
    protected abstract IDataNode OnExecute(params object[] arguments);

    public virtual IDataNode Execute(params object[] arguments)
    {
        return OnExecute(arguments);
    }
}

public interface IDataNode;
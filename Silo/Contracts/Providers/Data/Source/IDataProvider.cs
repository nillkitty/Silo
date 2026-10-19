using Silo.Contracts.ConnectorServices;

namespace Silo.Contracts.Providers.Data.Source;

/// <summary>
///     A object which provides access to one or more <see cref="IDataSource" />
///     items, either through navigating a tree, or supplying a request type
///     to a verb.
/// </summary>
public interface IDataProvider : IDisplayName, IDataNode
{
    /// <summary>
    ///     Interrogate the provider to see what verbs it supports.
    /// </summary>
    IEnumerable<DataVerb> GetSupportedVerbs();
}

public interface IHierarchicalDataProvider : IDataProvider
{
    string                                 ProviderName { get; }
    IEnumerable<IHierarchicalDataProvider> GetChildren();
    IEnumerable<IDataSource>               GetDataSources();
}

public interface IFlatDataProvider : IDataProvider
{
    IEnumerable<IDataSource> GetDataSources();
}
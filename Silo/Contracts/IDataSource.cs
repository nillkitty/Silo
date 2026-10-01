using Silo.Contracts.ConnectorServices;

namespace Silo.Contracts;

/// <summary>
///     A source of data at the level of which would be used
///     in the "FROM" clause of a "SELECT" statement;
///     e.g. <c>SELECT * FROM [DataSource]</c> should be a valid
///     action on implementing types.
/// </summary>
public interface IDataSource
{
    /// <summary>
    ///     e.g. the table name, 'CN=name' for LdapNodes,
    ///     etc.
    /// </summary>
    string CanonicalName { get; }

    /// <summary>
    ///     The local namespace path which navigates to the IDataProvider
    ///     providing this source.
    /// </summary>

    string LocalNamespace { get; }

    /// <summary>
    ///     e.g. 'dbo' for a SqlTable, or
    ///     'OU=SALES,DC=contoso,DC=com' for an LdapContainer
    /// </summary>
    string NativeNamespace { get; }

    /// <summary>
    ///     Gets a reference to the parent data provider.
    /// </summary>
    IDataProvider? Provider { get; }
}

/// <summary>
///     A object which provides access to one or more <see cref="IDataSource" />
///     items, either through navigating a tree, or supplying a request type
///     to a verb.
/// </summary>
public interface IDataProvider : IDisplayName
{
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
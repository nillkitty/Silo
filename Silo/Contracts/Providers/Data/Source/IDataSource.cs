namespace Silo.Contracts.Providers.Data.Source;

/// <summary>
///     A source of data at the level of which would be used
///     in the "FROM" clause of a "SELECT" statement;
///     e.g. <c>SELECT * FROM [DataSource]</c> should be a valid
///     action on implementing types.
/// </summary>
public interface IDataSource : IDataNode
{
    /// <summary>
    ///     e.g. the table name, 'CN=name' for LdapNodes,
    ///     etc.
    /// </summary>
    string CanonicalName { get; }

    /// <summary>
    ///     The URI that can be used to uniquely locate this data source
    ///     again (if available)
    /// </summary>
    Uri DataSourceUri { get; }

    /// <summary>
    ///     An optional suggestion to use as the mnemonic/alias in a query
    /// </summary>
    string? DefaultAlias { get; }

    /// <summary>
    ///     The local namespace path which navigates to the IDataProvider
    ///     providing this source.
    /// </summary>

    string? LocalNamespace { get; }

    /// <summary>
    ///     e.g. 'dbo' for a SqlTable, or
    ///     'OU=SALES,DC=contoso,DC=com' for an LdapContainer
    /// </summary>
    string? NativeNamespace { get; }

    /// <summary>
    ///     Gets a reference to the parent data provider.
    /// </summary>
    IDataProvider? Provider { get; }
}
namespace Silo.Connectors.Sql;

/// <summary>What a <see cref="SqlItem" /> is.</summary>
public enum SqlItemKind
{
    /// <summary>
    ///     Using SQL server browser to obtain multiple SQL server instances on a target host
    ///     results in this n-1 layer container to contain all instances on the host.
    /// </summary>
    Host,

    /// <summary>
    ///     A SQL server instance
    /// </summary>
    Server,

    /// <summary>
    ///     A database on a SQL server instance
    /// </summary>
    Database,

    /// <summary>
    ///     A container for other constructs
    /// </summary>
    Folder,

    /// <summary>
    ///     A container representing a SQL 'schema'
    /// </summary>
    Namespace,

    /// <summary>
    ///     A static SQL table
    /// </summary>
    Table,

    /// <summary>
    ///     A column in a table, view, or other columnar object
    /// </summary>
    Column,

    /// <summary>
    ///     A dynamic or computed table through which CRUD operations are viable.
    /// </summary>
    View,

    /// <summary>
    ///     A table-valued function or stored procedure which requires one or more
    ///     arguments
    /// </summary>
    ParametricObject,

    /// <summary>
    ///     A table-valued function or stored procedure which takes no arguments.
    /// </summary>
    NonParametricObject,

    /// <summary>
    ///     A scalar function
    /// </summary>
    ScalarFunction,

    /// <summary>
    ///     A synonym (server-side alias)
    /// </summary>
    Synonym,

    /// <summary>
    ///     A sequence
    /// </summary>
    Sequence,

    /// <summary>
    ///     The definition for a "schema"
    /// </summary>
    Schema,

    /// <summary>
    ///     The definition for a database principal
    /// </summary>
    User,

    /// <summary>
    ///     The definition for a database role
    /// </summary>
    Role,

    /// <summary>
    ///     The definition for a database role member
    /// </summary>
    RoleMember
}

/// <summary>The text of SQL Server's names, types and URIs. Pure, so it can be tested on its own.</summary>
public static class SqlNames
{
    /// <summary>A name as a quoted identifier (<c>[My]]Table]</c>), safe to put into a statement.</summary>
    public static string Quote(string name)
    {
        return "[" + name.Replace("]", "]]") + "]";
    }

    /// <summary>The <c>[database].[schema].[name]</c> form of an object's name.</summary>
    public static string FullName(string database, string schema, string name)
    {
        return $"{Quote(database)}.{Quote(schema)}.{Quote(name)}";
    }

    /// <summary>
    ///     The URI without the user and password a connection may have been made with: nothing that is shown or
    ///     remembered may carry them.
    /// </summary>
    public static Uri SafeUri(Uri uri)
    {
        if (uri.UserInfo.Length == 0) return uri;

        return new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri;
    }

    /// <summary>
    ///     The URI of an item: the server's (<c>sqlserver://host:port/</c>), then the database, then (for a table or view) its
    ///     schema and
    ///     name, then (for a column) the column. A "Tables" / "Views" heading is <c>~tables</c> / <c>~views</c> under its
    ///     database.
    /// </summary>
    public static Uri ItemUri(Uri server, params string[] segments)
    {
        var    safe      = SafeUri(server);
        string authority = safe.Host + (safe.Port > 0 ? ":" + safe.Port : string.Empty);
        string path      = string.Join("/", segments.Select(Uri.EscapeDataString));

        return new Uri($"{safe.Scheme}://{authority}/{path}", UriKind.Absolute);
    }

    /// <summary>
    ///     A column's type as it is written in a table definition: <c>nvarchar(50)</c>, <c>decimal(18,2)</c>,
    ///     <c>varchar(max)</c>, <c>int</c>.
    /// </summary>
    /// <param name="typeName">The type's name (sys.types.name).</param>
    /// <param name="maxLength">The size in bytes (sys.columns.max_length); -1 for "max".</param>
    /// <param name="precision">sys.columns.precision.</param>
    /// <param name="scale">sys.columns.scale.</param>
    public static string TypeText(string typeName, int maxLength, int precision, int scale)
    {
        switch (typeName.ToLowerInvariant())
        {
            case "char" or "varchar" or "binary" or "varbinary":
                return $"{typeName}({(maxLength < 0 ? "max" : maxLength.ToString())})";
            case "nchar" or "nvarchar": // two bytes to a character
                return $"{typeName}({(maxLength < 0 ? "max" : (maxLength / 2).ToString())})";
            case "decimal" or "numeric":
                return $"{typeName}({precision},{scale})";
            case "datetime2" or "datetimeoffset" or "time":
                return scale == 7 ? typeName : $"{typeName}({scale})";
            default:
                return typeName;
        }
    }
}
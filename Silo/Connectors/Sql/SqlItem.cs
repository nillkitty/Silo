using Silo.ViewModel;

namespace Silo.Connectors.Sql;

public class SqlDatabaseItem(SqlConnection connection, string name, Uri uri)
    : SqlItem(connection, SqlItemKind.Database, name.Required(), uri.Required(), name);

public class SqlNamespaceItem(SqlDatabaseItem db, string name)
    : SqlItem(db.Connection, SqlItemKind.Namespace, name.Required(), db.Uri.Append("Schema", name), name);

public class SqlTableItem(SqlItem parent, long object_id, string name, Uri uri)
    : SqlItem(parent.Connection, SqlItemKind.Table, name.Required(), uri.Required(), parent.Database);

public class SqlViewItem(SqlItem parent, long object_id, string name, Uri uri)
    : SqlItem(parent.Connection, SqlItemKind.View, name.Required(), uri.Required(), parent.Database);

public class SqlRoleItem(SqlDatabaseItem db, string name)
    : SqlItem(db.Connection, SqlItemKind.Role, name.Required(), db.Uri.Append("Role", name), name);

public class SqlUserItem(SqlDatabaseItem db, string name)
    : SqlItem(db.Connection, SqlItemKind.User, name.Required(), db.Uri.Append("User", name), name);

public class SqlSchemaItem(SqlDatabaseItem db, string name)
    : SqlItem(db.Connection, SqlItemKind.Schema, name.Required(), db.Uri.Append("Schema", name), name);

public class SqlRoleMemberItem(SqlRoleItem r, string name)
    : SqlItem(r.Connection, SqlItemKind.RoleMember, name.Required(), r.Uri.Append("Role", r.Name, "Member", name), name);

public class SqlSynonymItem(SqlDatabaseItem db, string schema, string name)
    : SqlItem(db.Connection, SqlItemKind.Synonym, name.Required(), db.Uri.Append("Synonym", schema, name), name);

public class SqlSequenceItem(SqlDatabaseItem db, string schema, string name)
    : SqlItem(db.Connection, SqlItemKind.Sequence, name.Required(), db.Uri.Append("Sequence", schema, name), name);

public class SqlScalarFuncItem(SqlDatabaseItem db, string schema, string name)
    : SqlItem(db.Connection, SqlItemKind.ScalarFunction, name.Required(), db.Uri.Append("Function", schema, name), name);

public class SqlParamProc(SqlDatabaseItem db, string schema, string name)
    : SqlItem(db.Connection, SqlItemKind.ParametricObject, name.Required(), db.Uri.Append("Proc", schema, name), name);

public class SqlParamFunc(SqlDatabaseItem db, string schema, string name)
    : SqlItem(db.Connection, SqlItemKind.ParametricObject, name.Required(), db.Uri.Append("TVF", schema, name), name);

public class SqlNonParamProc(SqlDatabaseItem db, string schema, string name)
    : SqlItem(db.Connection, SqlItemKind.NonParametricObject, name.Required(), db.Uri.Append("Proc", schema, name), name);

public class SqlNonParamFunc(SqlDatabaseItem db, string schema, string name)
    : SqlItem(db.Connection, SqlItemKind.NonParametricObject, name.Required(), db.Uri.Append("TVF", schema, name), name);

/// <summary>
///     One thing in a SQL Server as the Explorer shows it -- the server, a database, a "Tables" / "Views" heading, a table
///     or view, a
///     column. It is the <see cref="SiloNode.DataContext" /> of its node, so it supplies the Properties pane's details and
///     (for the server)
///     the node's commands.
/// </summary>
public class SqlItem : INodeDetails, ICommandProvider, INodeProvider
{
    private readonly List<(string Name, string Value)> _facts = [];

    public SqlConnection Connection { get; }

    /// <summary>The database the item is in (or is); null for the server.</summary>
    public string? Database { get; }

    /// <summary>Whether the item can be opened: false for a database that is offline or that this login cannot enter.</summary>
    public bool IsAccessible { get; init; } = true;

    public SqlItemKind Kind { get; }

    /// <summary>The text of the node.</summary>
    public string Label { get; init; }

    public SiloNode? Node { get; protected set; }

    public string? Schema  { get; }
    public string  Version => GetServerVersion();

    /// <summary>
    ///     The key of the icon (see <c>Resources/NavIcons.xaml</c>) as a relative URI, which is how the Explorer reads
    ///     one.
    /// </summary>
    public Uri? Icon => new(IconKeyOf(Kind, IsAccessible), UriKind.Relative);

    public string Name { get; }

    public string TypeName
    {
        get
        {
            return Kind switch
                   {
                       SqlItemKind.Server   => "SQL Server",
                       SqlItemKind.Database => "Database",
                       SqlItemKind.Folder   => "Folder",
                       SqlItemKind.Table    => "Table",
                       SqlItemKind.View     => "View",
                       _                    => "Column"
                   };
        }
    }

    public Uri     Uri  { get; }
    public object? Data => this;

    private string GetServerVersion()
    {
        throw new NotImplementedException();
    }

    public static string IconKeyOf(SqlItemKind kind, bool accessible)
    {
        if (!accessible) return "FsFileMissing";

        return kind switch
               {
                   SqlItemKind.Server   => "Server",
                   SqlItemKind.Database => "Database5",
                   SqlItemKind.Folder   => "Folder17",
                   SqlItemKind.Table    => "TbTable",
                   SqlItemKind.View     => "Layers",
                   _                    => "Record"
               };
    }

    /// <summary>Adds a row to the details of the item (what the query that found it also said).</summary>
    public SqlItem Fact(string name, string value)
    {
        _facts.Add((name, value));
        return this;
    }

    /// <summary>
    ///     The node for the item. A server and a database read what is inside them when they are expanded; a table or
    ///     view reads its columns.
    /// </summary>
    public SiloNode CreateNode(int? expand)
    {
        Func<CancellationToken, Task<IReadOnlyList<SiloNode>>>? loader = null;
        if (Kind == SqlItemKind.Server)
            loader = Connection.LoadDatabaseNodesAsync;
        else if (Kind == SqlItemKind.Database && IsAccessible)
            loader = token => Connection.LoadDatabaseContentsAsync(this, token);
        else if (Kind is SqlItemKind.Table or SqlItemKind.View)
            loader = token => Connection.LoadColumnNodesAsync(this, token);

        return new SiloNode(this, Label, Uri, Icon, expand) { ChildLoader = loader };
    }

    public SqlItem(SqlConnection connection, SqlItemKind kind, string name, Uri uri, string? database = null, string? schema = null)
    {
        Connection = connection;
        Kind       = kind;
        Name       = name;
        Label      = name;
        Uri        = uri;
        Database   = database;
        Schema     = schema;
    }

    /// <summary>The server's commands. The others have none of their own.</summary>
    public IEnumerable<ProvidedCommand> GetCommands()
    {
        if (Kind != SqlItemKind.Server) yield break;

        yield return ProvidedCommand.From("sql.connect", "_Connect", () => Connection.ConnectFromUi(), "Plug",
                                          () => Connection.ConnectionState is not true, "Connects to the server", "Connection");
        yield return ProvidedCommand.From("sql.disconnect", "_Disconnect", () => Connection.DisconnectFromUi(), "Power",
                                          () => Connection.State is not Disconnected, "Closes the connection to the server", "Connection");
    }

    public IEnumerable<(string Name, string Value)> GetDetails()
    {
        yield return ("Type", TypeName);
        yield return ("URI", Uri.OriginalString);

        if (Kind == SqlItemKind.Server)
        {
            foreach (var row in Connection.GetServerDetails()) yield return row;
            yield break;
        }

        if (Database is not null && Kind != SqlItemKind.Database) yield return ("Database", Database);
        if (Schema is not null) yield return ("Schema", Schema);
        if (!IsAccessible) yield return ("State", "This database cannot be opened");

        foreach (var fact in _facts) yield return fact;
    }

    public SiloNode? GetNode()
    {
        Node ??= new SqlNode(this);
        return Node;
    }
}

public record SqlNode : SiloNode
{
    public SqlItem Model { get; }
    public SqlItem model { get; init; }

    public void Deconstruct(out SqlItem model)
    {
        model = this.model;
    }

    public SqlNode(SqlItem Model) : base(Model)
    {
        model      = Model;
        this.Model = Model.Required();
        switch (model.Kind)
        {
            case SqlItemKind.Host:
                Text     = $"Server - {Model.Uri.Host}";
                Icon     = null; // claude: server icon
                TypeName = "SQL Server Host";
                break;
            case SqlItemKind.Server:
                Text     = $"SQL Server {Model.Version} - {Model.Uri.Host}";
                Icon     = null; // claude: database server icon
                TypeName = "SQL Server Instance";
                break;
            case SqlItemKind.Database:
                Text     = Model.Name;
                Icon     = null; // claude: database icon
                TypeName = "Database";
                break;
            case SqlItemKind.Folder:
                Text     = Model.Name;
                Icon     = null; // claude: folder icon
                TypeName = "Folder";
                break;
            case SqlItemKind.Namespace:
                Text     = Model.Name;
                Icon     = null; // claude: schema/namespace icon
                TypeName = "Namespce";
                break;
            case SqlItemKind.Table:
                Text     = Model.Name;
                Icon     = null; // claude: table icon
                TypeName = "Database Table";
                break;

            case SqlItemKind.Column:
                Text     = Model.Name;
                Icon     = null; // claude: column icon
                TypeName = "Database Column";
                break;

            case SqlItemKind.View:
                Text     = Model.Name;
                Icon     = null; // claude: db view icon
                TypeName = "Database View";
                break;

            case SqlItemKind.ParametricObject:
                Text     = Model.Name;
                Icon     = null; // claude: method with parameters icon
                TypeName = Model.TypeName;
                break;

            case SqlItemKind.NonParametricObject:
                Text     = Model.Name;
                Icon     = null; // claude: table valued function/view icon
                TypeName = Model.TypeName;
                break;

            case SqlItemKind.ScalarFunction:
                Text     = Model.Name;
                Icon     = null; // claude: function icon
                TypeName = "Scalar Function";
                break;

            case SqlItemKind.Synonym:
                Text     = Model.Name;
                Icon     = null; // claude: synonym icon
                TypeName = "Synonym";
                break;

            case SqlItemKind.Sequence:
                Text     = Model.Name;
                Icon     = null; // claude: sequence icon
                TypeName = "Sequence";
                break;

            case SqlItemKind.Schema:
                Text     = Model.Name;
                Icon     = null; // claude: schema icon
                TypeName = "Database Schema";
                break;

            case SqlItemKind.User:
                Text     = Model.Name;
                Icon     = null; // claude: user icon
                TypeName = "Database Principal";
                break;

            case SqlItemKind.Role:
                Text     = Model.Name;
                Icon     = null; // claude: role icon
                TypeName = "Database Role";
                break;

            case SqlItemKind.RoleMember:
                Text     = Model.Name;
                Icon     = null; // claude: role member icon
                TypeName = "Database Role Member";
                break;

            default:
                throw new ArgumentOutOfRangeException();
        }
    }
}
using Silo.Contracts.ConnectorServices;
using Db = Microsoft.Data.SqlClient;

namespace Silo.Connectors.Sql;

/// <summary>How a connection signs in.</summary>
public enum SqlAuthMethod
{
    /// <summary>The Windows account Silo runs as (integrated security).</summary>
    Windows,

    /// <summary>A SQL login: a user name and password, from a credential or the URI.</summary>
    Credential
}

/// <summary>
///     One way of signing in to the server, tried in turn by <see cref="SqlSettings.Attempts" />. Holds a connection
///     string, which can
///     hold a password: it is never written out by <see cref="ToString" />.
/// </summary>
public sealed class SqlAttempt
{
    public string ConnectionString { get; }

    /// <summary>What it is, for messages: "Windows authentication", "SQL login 'sa'".</summary>
    public string Description { get; }

    /// <summary>How it signs in.</summary>
    public SqlAuthMethod Method { get; }

    public override string ToString()
    {
        return Description;
    }

    public SqlAttempt(string connectionString, string description, SqlAuthMethod method = SqlAuthMethod.Credential)
    {
        ConnectionString = connectionString;
        Description      = description;
        Method           = method;
    }
}

/// <summary>
///     What a SQL Server connection asks of the server, worked out from its URI and the connector's authentication flags.
///     No network, so it
///     can be tested on its own.
/// </summary>
/// <remarks>
///     The URI is <c>sqlserver://[user[:password]@]host[:port][/database]</c> (<c>mssql</c> and <c>sql</c> work as well),
///     with these optional
///     query parameters: <c>instance</c> (a named instance: <c>host\instance</c>), <c>encrypt</c> and <c>trust</c>
///     (true/false: encrypt the
///     link; trust the server certificate without checking it), <c>timeout</c> (seconds to wait to connect),
///     <c>readonly</c> (true: say
///     the work is read only, for a server that routes it to a replica) and <c>app</c> (the application name the server
///     sees).
/// </remarks>
public static class SqlSettings
{
    /// <summary>
    ///     What a connector that says nothing about Windows authentication gets: it is used when there is no credential
    ///     or the credential fails.
    /// </summary>
    public const WinAuthFlags DefaultWinAuth = WinAuthFlags.WinAuthWhenNoCreds | WinAuthFlags.WinAuthIfCredsFail;

    // "Login failed", "login from an untrusted domain", "not associated with a trusted connection", account disabled, password expired / must change
    private static readonly HashSet<int> AuthFailures = [18450, 18451, 18452, 18456, 18470, 18486, 18487, 18488];

    /// <summary>
    ///     Whether the error number of a <c>SqlException</c> means the server refused who we are (so another way of
    ///     signing in may work) rather than that it could not be reached.
    /// </summary>
    public static bool IsAuthFailure(int sqlErrorNumber)
    {
        return AuthFailures.Contains(sqlErrorNumber);
    }

    /// <summary>The server as the connection string spells it: <c>host</c>, <c>host,port</c> or <c>host\instance</c>.</summary>
    public static string DataSource(Uri uri)
    {
        string host = uri.Host.Length == 0 ? "." : uri.Host;

        if (Query(uri).TryGetValue("instance", out string? instance) && instance.Length > 0)
            return host + "\\" + instance;

        return uri.Port > 0 ? $"{host},{uri.Port}" : host;
    }

    /// <summary>The server and port to show while connecting.</summary>
    public static string HostPort(Uri uri)
    {
        return DataSource(uri);
    }

    /// <summary>The database the URI names (its first path segment); null when it names none.</summary>
    public static string? Database(Uri uri)
    {
        var first = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is null ? null : Uri.UnescapeDataString(first);
    }

    /// <summary>The user and password written into the URI (<c>user:password@host</c>), if any.</summary>
    public static (string User, string? Password)? CredentialFromUri(Uri uri)
    {
        if (uri.UserInfo.Length == 0) return null;

        int     colon    = uri.UserInfo.IndexOf(':');
        string  user     = Uri.UnescapeDataString(colon < 0 ? uri.UserInfo : uri.UserInfo[..colon]);
        string? password = colon < 0 ? null : Uri.UnescapeDataString(uri.UserInfo[(colon + 1)..]);
        return user.Length == 0 ? null : (user, password);
    }

    /// <summary>The query parameters of the URI, keys in lower case.</summary>
    public static Dictionary<string, string> Query(Uri uri)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int    eq  = part.IndexOf('=');
            string key = Uri.UnescapeDataString(eq < 0 ? part : part[..eq]).ToLowerInvariant();
            result[key] = eq < 0 ? string.Empty : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
        }

        return result;
    }

    /// <summary>The connection settings that do not depend on how the person signs in.</summary>
    public static Db.SqlConnectionStringBuilder Common(Uri uri)
    {
        var query = Query(uri);
        var builder = new Db.SqlConnectionStringBuilder
                      {
                          DataSource = DataSource(uri),
                          ApplicationName =
                              query.GetValueOrDefault("app") is { Length: > 0 } app ? app : "Silo"
                      };

        if (Database(uri) is { } database) builder.InitialCatalog = database;

        if (Flag(query, "encrypt") is { } encrypt) builder.Encrypt            = encrypt;
        if (Flag(query, "trust") is { } trust) builder.TrustServerCertificate = trust;
        if (Flag(query, "readonly") is true) builder.ApplicationIntent        = Db.ApplicationIntent.ReadOnly;
        if (query.TryGetValue("timeout", out string? timeout) && int.TryParse(timeout, out int seconds) && seconds >= 0)
            builder.ConnectTimeout = seconds;

        return builder;
    }

    private static bool? Flag(Dictionary<string, string> query, string key)
    {
        return query.TryGetValue(key, out string? text) && bool.TryParse(text, out bool value) ? value : null;
    }

    /// <summary>
    ///     The ways to sign in, in the order to try them. With the connector's flags (<see cref="ConnectorAuthFlags" />,
    ///     <see cref="WinAuthFlags" />) and whether a credential is at hand:
    ///     <list type="bullet">
    ///         <item><see cref="WinAuthFlags.WinAuthThenCreds" />: Windows first, then the credential if there is one.</item>
    ///         <item>
    ///             Otherwise with a credential: the credential, then Windows if
    ///             <see cref="WinAuthFlags.WinAuthIfCredsFail" />.
    ///         </item>
    ///         <item>Otherwise (no credential): Windows if <see cref="WinAuthFlags.WinAuthWhenNoCreds" />.</item>
    ///     </list>
    ///     <see cref="ConnectorAuthFlags.RequireCredential" /> without a credential is an error. Asking for a new credential
    ///     (<see cref="ConnectorAuthFlags.PromptOnChallenge" />, <see cref="WinAuthFlags.WinAuthPromptOnFail" />) is for the
    ///     caller's UI: here a
    ///     refusal is an exception.
    /// </summary>
    /// <exception cref="InvalidOperationException">There is no way to sign in with these flags.</exception>
    public static IReadOnlyList<SqlAuthMethod> AuthOrder(bool hasCredential, ConnectorAuthFlags auth, WinAuthFlags win)
    {
        if (auth.HasFlag(ConnectorAuthFlags.RequireCredential) && !hasCredential)
            throw new InvalidOperationException("This connection needs a user name and password, and none was given.");

        var order = new List<SqlAuthMethod>();
        if (win.HasFlag(WinAuthFlags.WinAuthThenCreds))
        {
            order.Add(SqlAuthMethod.Windows);
            if (hasCredential) order.Add(SqlAuthMethod.Credential);
        }
        else if (hasCredential)
        {
            order.Add(SqlAuthMethod.Credential);
            if (win.HasFlag(WinAuthFlags.WinAuthIfCredsFail)) order.Add(SqlAuthMethod.Windows);
        }
        else if (win.HasFlag(WinAuthFlags.WinAuthWhenNoCreds))
        {
            order.Add(SqlAuthMethod.Windows);
        }

        if (order.Count == 0)
            throw new
                InvalidOperationException("There is no way to sign in: no credential was given, and Windows authentication is not allowed for this connector.");

        return order;
    }

    /// <summary>
    ///     The connection strings to try, with a description of each, in the order <see cref="AuthOrder" /> gives. The
    ///     credential is the <paramref name="credentialFragment" /> if there is one (the connection settings that a
    ///     <c>Credential</c> hands over: <c>User ID=sa;Password=...;</c>, or <c>Integrated Security=SSPI;</c>), else the user and
    ///     password written in the URI. Two ways that come to the same connection string are tried once.
    /// </summary>
    /// <exception cref="InvalidOperationException">There is no way to sign in (see <see cref="AuthOrder" />), or the fragment is not connection string settings.</exception>
    public static IReadOnlyList<SqlAttempt> Attempts(Uri uri, string? credentialFragment, ConnectorAuthFlags auth, WinAuthFlags win)
    {
        string? fragment = string.IsNullOrWhiteSpace(credentialFragment) ? null : credentialFragment;
        var     fromUri  = fragment is null ? CredentialFromUri(uri) : null;

        var attempts = new List<SqlAttempt>();
        foreach (var method in AuthOrder(fragment is not null || fromUri is not null, auth, win))
        {
            var attempt = method == SqlAuthMethod.Windows ? WindowsAttempt(uri) : CredentialAttempt(uri, fragment, fromUri);
            if (attempts.All(a => a.ConnectionString != attempt.ConnectionString)) attempts.Add(attempt);
        }

        return attempts;
    }

    private static SqlAttempt WindowsAttempt(Uri uri)
    {
        var builder = Common(uri);
        builder.IntegratedSecurity = true;
        return new SqlAttempt(builder.ConnectionString, "Windows authentication", SqlAuthMethod.Windows);
    }

    private static SqlAttempt CredentialAttempt(Uri uri, string? fragment, (string User, string? Password)? fromUri)
    {
        var builder = Common(uri);

        if (fragment is not null)
        {
            // the settings are parsed, not pasted in, so that a quoted password with a ; in it stays one setting
            try
            {
                builder = new Db.SqlConnectionStringBuilder($"{builder.ConnectionString};{fragment}");
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException("The credential's settings are not valid in a connection string.", ex);
            }
        }
        else if (fromUri is { } uriCredential)
        {
            builder.UserID = uriCredential.User;
            if (uriCredential.Password is not null) builder.Password = uriCredential.Password;
        }

        if (builder.IntegratedSecurity)
            return new SqlAttempt(builder.ConnectionString, "Windows authentication", SqlAuthMethod.Windows);

        string description = builder.UserID.Length > 0 ? $"SQL login '{builder.UserID}'" : "the assigned credential";
        return new SqlAttempt(builder.ConnectionString, description);
    }
}

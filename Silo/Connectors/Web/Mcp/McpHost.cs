using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;
using Silo.Model;
using Silo.ViewModel;

namespace Silo.Connectors.Web.Mcp;

/// <summary>
///     Silo's own MCP server: lets an AI assistant look at the open Silos and their connections. It listens on this computer
///     only (127.0.0.1), speaks streamable HTTP, and wants the bearer token of <see cref="McpServerSettings" />. AI tools that
///     only start programs reach it through <c>silo mcp</c> (see <see cref="McpBridge" />), which adds the token.
/// </summary>
public sealed class McpHost
{
    private CancellationTokenSource? _stopping;
    private HttpListener?            _listener;

    public static McpHost Current { get; } = new();

    public bool IsRunning => _listener?.IsListening ?? false;

    /// <summary>Why the server could not start, else null.</summary>
    public string? LastError { get; private set; }

    public int Port { get; private set; }

    public Uri Endpoint => new($"http://127.0.0.1:{Port}/mcp");

    public event EventHandler? StateChanged;

    /// <summary>Starts the server if it was left on. Called at startup.</summary>
    public void StartIfEnabled()
    {
        if (McpServerSettings.Load().Enabled) Start();
    }

    /// <summary>Starts listening, at the remembered port or the first free one after it. Returns false (see <see cref="LastError" />) when there is none.</summary>
    public bool Start()
    {
        if (IsRunning) return true;

        var settings = McpServerSettings.Load();
        for (int port = settings.Port; port < settings.Port + 10; port++)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
            }
            catch (HttpListenerException)
            {
                listener.Close();
                continue;
            }

            _listener = listener;
            _stopping = new CancellationTokenSource();
            Port      = port;
            LastError = null;
            if (settings.Port != port || !settings.Enabled)
            {
                settings.Port    = port;
                settings.Enabled = true;
                settings.Save();
            }

            _ = Task.Run(() => AcceptAsync(listener, settings.Token, _stopping.Token));
            Log.Information("MCP server listening on {Endpoint}", Endpoint);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        LastError = $"No port from {settings.Port} to {settings.Port + 9} could be listened on.";
        StateChanged?.Invoke(this, EventArgs.Empty);
        return false;
    }

    /// <summary>Stops listening; <paramref name="remember" /> keeps it off at the next start too.</summary>
    public void Stop(bool remember = true)
    {
        var listener = _listener;
        _listener = null;
        _stopping?.Cancel();

        try
        {
            listener?.Close();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Closing the MCP listener failed");
        }

        if (remember)
        {
            var settings = McpServerSettings.Load();
            if (settings.Enabled)
            {
                settings.Enabled = false;
                settings.Save();
            }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task AcceptAsync(HttpListener listener, string token, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception)
            {
                break; // closed
            }

            _ = Task.Run(() => HandleAsync(context, token, cancel), cancel);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, string token, CancellationToken cancel)
    {
        var request  = context.Request;
        var response = context.Response;
        try
        {
            if (!IsLocalHost(request.UserHostName) || (request.Headers["Origin"] is { } origin && !IsLocalOrigin(origin)))
            {
                response.StatusCode = 403; // a web page reaching for this server through DNS rebinding
                return;
            }

            if (request.Url?.AbsolutePath.TrimEnd('/') != "/mcp")
            {
                response.StatusCode = 404;
                return;
            }

            if (!IsAuthorized(request.Headers["Authorization"], token))
            {
                response.StatusCode = 401;
                response.AddHeader("WWW-Authenticate", "Bearer");
                return;
            }

            if (request.HttpMethod == "DELETE")
            {
                response.StatusCode = 200;
                return;
            }

            if (request.HttpMethod != "POST")
            {
                response.StatusCode = 405;
                response.AddHeader("Allow", "POST");
                return;
            }

            JsonNode? message;
            using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
            {
                try
                {
                    message = JsonNode.Parse(await reader.ReadToEndAsync(cancel));
                }
                catch (JsonException)
                {
                    await WriteAsync(response, McpServerCore.Error(null, -32700, "Parse error"), cancel);
                    return;
                }
            }

            var reply = message is JsonObject single ? await McpServerCore.HandleAsync(single, cancel) : McpServerCore.Error(null, -32600, "Invalid request");
            if (reply is null)
            {
                response.StatusCode = 202; // a notification: nothing to say back
                return;
            }

            await WriteAsync(response, reply, cancel);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Handling an MCP request failed");
            try
            {
                response.StatusCode = 500;
            }
            catch (Exception)
            {
                /* the response was already on its way */
            }
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception)
            {
                /* the client went away */
            }
        }
    }

    private static async Task WriteAsync(HttpListenerResponse response, JsonNode body, CancellationToken cancel)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
        response.ContentType     = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancel);
    }

    private bool IsLocalHost(string? hostHeader)
    {
        return hostHeader is not null && (hostHeader.Equals($"127.0.0.1:{Port}", StringComparison.Ordinal) || hostHeader.Equals($"localhost:{Port}", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLocalOrigin(string origin)
    {
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) && (uri.Host == "127.0.0.1" || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAuthorized(string? header, string token)
    {
        const string prefix = "Bearer ";
        if (header is null || !header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header[prefix.Length..].Trim()), Encoding.UTF8.GetBytes(token));
    }
}

/// <summary>What Silo's MCP server says to the requests it gets: the handshake and the tools, which look at the open Silos and read what the Explorer shows.</summary>
public static class McpServerCore
{
    private static readonly string[] SupportedVersions = ["2025-06-18", "2025-03-26", "2024-11-05"];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static readonly (Tool Tool, Func<JsonObject?, Task<object>> Run)[] Tools =
    [
        (new Tool
         {
             Name        = "silo_list_silos",
             Description = "Lists the Silos that are open in Silo, and which one is active.",
             InputSchema = Schema()
         }, _ => OnUi(ListSilos)),
        (new Tool
         {
             Name        = "silo_list_connections",
             Description = "Lists the connections (files, directories, web services, MCP servers...) of the open Silos, with their state.",
             InputSchema = Schema(("silo", "string", "The Silo's file name; every open Silo when left out."))
         }, args => OnUi(() => ListConnections(args?["silo"]?.GetValue<string>()))),
        (new Tool
         {
             Name        = "silo_browse",
             Description = "Lists what is in a connection as the Explorer shows it. Give the names of the nodes to go through in 'path' to look further down.",
             InputSchema = Schema(["connection"], ("connection", "string", "The connection's name, as silo_list_connections gives it."),
                                  ("silo", "string", "The Silo's file name; the active Silo when left out."),
                                  ("path", "array", "Node names from the connection's root down to the node to list.")),
         }, args => OnUi(() => Browse(args)))
    ];

    public static async Task<JsonObject?> HandleAsync(JsonObject message, CancellationToken cancel)
    {
        string? method = message["method"]?.GetValue<string>();
        var     id     = message["id"]?.DeepClone();
        if (method is null) return id is null ? null : Error(id, -32600, "Invalid request");
        if (id is null) return null; // a notification

        try
        {
            switch (method)
            {
                case "initialize":
                    return Result(id, Initialize(message["params"]?["protocolVersion"]?.GetValue<string>()));
                case "ping":
                    return Result(id, new JsonObject());
                case "tools/list":
                    return Result(id, JsonSerializer.SerializeToNode(new ListToolsResult { Tools = Tools.Select(t => t.Tool).ToList() })!);
                case "tools/call":
                    return Result(id, await CallAsync(message["params"] as JsonObject));
                default:
                    return Error(id, -32601, $"Method not found: {method}");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "MCP request {Method} failed", method);
            return Error(id, -32603, ex.Message);
        }
    }

    public static JsonObject Error(JsonNode? id, int code, string text)
    {
        return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = text } };
    }

    private static JsonObject Result(JsonNode id, JsonNode result)
    {
        return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
    }

    private static JsonNode Initialize(string? requested)
    {
        var result = new InitializeResult
                     {
                         ProtocolVersion = requested is not null && SupportedVersions.Contains(requested) ? requested : SupportedVersions[0],
                         Capabilities    = new ServerCapabilities { Tools = new CapabilityWithListChanged { ListChanged = false } },
                         ServerInfo      = new ImplementationInfo { Name = "silo", Version = App.Version?.ToString() ?? "0" },
                         Instructions    = "Silo is a desktop app that keeps connections to files, directories, web services and more in documents called Silos. These tools read what is open in it."
                     };
        return JsonSerializer.SerializeToNode(result)!;
    }

    private static async Task<JsonNode> CallAsync(JsonObject? parameters)
    {
        string name = parameters?["name"]?.GetValue<string>() ?? throw new ArgumentException("A tool name is required.");
        var    tool = Tools.FirstOrDefault(t => t.Tool.Name == name);
        if (tool.Run is null) return Content($"There is no tool called '{name}'.", true);

        try
        {
            object value = await tool.Run(parameters["arguments"] as JsonObject);
            return Content(value as string ?? JsonSerializer.Serialize(value, Json), false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return Content(ex.Message, true); // the assistant can read this and try again
        }
    }

    private static JsonNode Content(string text, bool isError)
    {
        return JsonSerializer.SerializeToNode(new CallToolResult { Content = [new TextContent { Text = text }], IsError = isError })!;
    }

    // ---- the tools ---------------------------------------------------------------------------------

    private static object ListSilos()
    {
        var active = App.Instance?.ActiveSilo;
        return OpenSilo.OpenSilos.Select(s => new { name = s.Name, path = s.FilePath, active = ReferenceEquals(s, active), connections = s.Connections.Count }).ToList();
    }

    private static object ListConnections(string? silo)
    {
        return Silos(silo).SelectMany(s => s.Connections.Select(c => new
                                                                     {
                                                                         silo      = s.Name,
                                                                         name      = NameOf(c),
                                                                         connector = c.Connector.DisplayName,
                                                                         uri       = c.Node?.Uri?.ToString(),
                                                                         state     = c.ConnectionState switch { true => "connected", false => "disconnected", _ => "connectionless" }
                                                                     })).ToList();
    }

    private static async Task<object> Browse(JsonObject? args)
    {
        string wanted = args?["connection"]?.GetValue<string>() ?? throw new ArgumentException("'connection' is required.");
        string? silo  = args["silo"]?.GetValue<string>();

        var connection = Silos(silo ?? App.Instance?.ActiveSilo?.Name).SelectMany(s => s.Connections)
                                                                         .FirstOrDefault(c => string.Equals(NameOf(c), wanted, StringComparison.OrdinalIgnoreCase)) ??
                         throw new KeyNotFoundException($"No connection called '{wanted}'. Use silo_list_connections to see them.");

        var node = connection.Node ?? throw new InvalidOperationException($"'{wanted}' is not in the Explorer yet (is it connected?).");
        foreach (string step in (args["path"] as JsonArray)?.Select(p => p?.GetValue<string>() ?? string.Empty) ?? [])
        {
            await node.LoadChildrenAsync();
            node = node.Children.FirstOrDefault(c => string.Equals(c.Text, step, StringComparison.OrdinalIgnoreCase)) ??
                   throw new KeyNotFoundException($"There is nothing called '{step}' there.");
        }

        await node.LoadChildrenAsync();
        const int limit = 200;
        return new
               {
                   node     = node.Text,
                   type     = node.TypeName,
                   uri      = node.Uri?.ToString(),
                   children = node.Children.Take(limit).Select(c => new { name = c.Text, type = c.TypeName, uri = c.Uri?.ToString(), hasChildren = c.ChildLoader is not null || c.Children.Count > 0 }).ToList(),
                   more     = Math.Max(0, node.Children.Count - limit)
               };
    }

    private static string NameOf(IConnection c)
    {
        return c.DisplayText is { Length: > 0 } text ? text : c.Node?.Text ?? c.GetHostname() ?? c.Connector.DisplayName;
    }

    private static IEnumerable<OpenSilo> Silos(string? name)
    {
        var all = OpenSilo.OpenSilos.ToList();
        if (string.IsNullOrWhiteSpace(name)) return all;

        var match = all.Where(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        return match.Count > 0 ? match : throw new KeyNotFoundException($"No open Silo is called '{name}'. Use silo_list_silos to see them.");
    }

    /// <summary>The Silos belong to the UI thread: the work is done there.</summary>
    private static async Task<object> OnUi(Func<object> work)
    {
        var dispatcher = App.Instance?.Dispatcher ?? throw new InvalidOperationException("Silo is not running.");
        return await dispatcher.InvokeAsync(work);
    }

    private static async Task<object> OnUi(Func<Task<object>> work)
    {
        var dispatcher = App.Instance?.Dispatcher ?? throw new InvalidOperationException("Silo is not running.");
        return await await dispatcher.InvokeAsync(work);
    }

    private static JsonObject Schema(params (string Name, string Type, string Description)[] properties)
    {
        return Schema([], properties);
    }

    private static JsonObject Schema(string[] required, params (string Name, string Type, string Description)[] properties)
    {
        var props = new JsonObject();
        foreach (var (name, type, description) in properties)
        {
            var schema = new JsonObject { ["type"] = type, ["description"] = description };
            if (type == "array") schema["items"] = new JsonObject { ["type"] = "string" };
            props[name] = schema;
        }

        var result = new JsonObject { ["type"] = "object", ["properties"] = props };
        if (required.Length > 0) result["required"] = new JsonArray(required.Select(r => (JsonNode?)r).ToArray());
        return result;
    }
}

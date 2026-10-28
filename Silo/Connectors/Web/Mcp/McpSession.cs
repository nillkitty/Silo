using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;

namespace Silo.Connectors.Web.Mcp;

/// <summary>The server answered a request with an error.</summary>
public sealed class McpException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
///     One conversation with an MCP server: the handshake, then requests and their answers matched up by id. It does not
///     care how the messages travel (see <see cref="McpTransport" />).
/// </summary>
public sealed class McpSession : IAsyncDisposable
{
    public const string ProtocolVersion = "2025-06-18";

    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly McpTransport                                                _transport;

    private int  _closed;
    private long _nextId;

    public McpSession(McpTransport transport)
    {
        _transport                  =  transport;
        _transport.MessageReceived  += OnMessage;
        _transport.Closed           += OnClosed;
    }

    /// <summary>How long to wait for any one answer (the first start of a server run through <c>npx</c> can take a while).</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>What the server said about itself in the handshake; null before <see cref="StartAsync" /> has finished.</summary>
    public InitializeResult? Server { get; private set; }

    /// <summary>Raised once when the server goes away.</summary>
    public event Action<Exception?>? Closed;

    /// <summary>Starts the transport and does the handshake.</summary>
    public async Task StartAsync(CancellationToken cancel = default)
    {
        await _transport.StartAsync(cancel);

        var parameters = new InitializeParams
                         {
                             ProtocolVersion = ProtocolVersion,
                             ClientInfo = new ImplementationInfo { Name = "silo", Version = App.Version?.ToString() ?? "0" }
                         };

        var result = await RequestAsync("initialize", JsonSerializer.SerializeToNode(parameters)!.AsObject(), cancel);
        Server = result.Deserialize<InitializeResult>() ?? throw new InvalidDataException("The server's answer to the handshake was empty.");

        _transport.UseProtocolVersion(Server.ProtocolVersion);
        await NotifyAsync("notifications/initialized", null, cancel);
    }

    public async Task<JsonObject> RequestAsync(string method, JsonObject? parameters = null, CancellationToken cancel = default)
    {
        long id  = Interlocked.Increment(ref _nextId);
        var  tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        linked.CancelAfter(RequestTimeout);

        try
        {
            var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (parameters is not null) message["params"] = parameters;

            await _transport.SendAsync(message, linked.Token);
            return await tcs.Task.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new TimeoutException($"The server did not answer '{method}' within {RequestTimeout.TotalSeconds:0} seconds.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string method, JsonObject? parameters = null, CancellationToken cancel = default)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null) message["params"] = parameters;
        return _transport.SendAsync(message, cancel);
    }

    // ---- what a server offers ----------------------------------------------------------------------

    public Task<List<Tool>> ListToolsAsync(CancellationToken cancel = default)
    {
        return ListAsync<Tool>("tools/list", "tools", cancel);
    }

    public Task<List<Resource>> ListResourcesAsync(CancellationToken cancel = default)
    {
        return ListAsync<Resource>("resources/list", "resources", cancel);
    }

    public Task<List<Prompt>> ListPromptsAsync(CancellationToken cancel = default)
    {
        return ListAsync<Prompt>("prompts/list", "prompts", cancel);
    }

    public async Task<CallToolResult> CallToolAsync(string name, JsonObject? arguments = null, CancellationToken cancel = default)
    {
        var result = await RequestAsync("tools/call", JsonSerializer.SerializeToNode(new CallToolParams { Name = name, Arguments = arguments })!.AsObject(), cancel);
        try
        {
            return result.Deserialize<CallToolResult>() ?? new CallToolResult { Content = [] };
        }
        catch (JsonException)
        {
            // content of a kind this client does not model (audio, resource links...): hand it over as text
            return new CallToolResult { Content = [new TextContent { Text = result.ToJsonString() }], IsError = result["isError"]?.GetValue<bool>() ?? false };
        }
    }

    public async Task<List<ResourceContent>> ReadResourceAsync(string uri, CancellationToken cancel = default)
    {
        var result = await RequestAsync("resources/read", new JsonObject { ["uri"] = uri }, cancel);
        return result["contents"] is JsonArray items ? items.Select(i => i.Deserialize<ResourceContent>()).OfType<ResourceContent>().ToList() : [];
    }

    private async Task<List<T>> ListAsync<T>(string method, string key, CancellationToken cancel)
    {
        var     all    = new List<T>();
        string? cursor = null;

        for (int page = 0; page < 100; page++)
        {
            var result = await RequestAsync(method, cursor is null ? null : new JsonObject { ["cursor"] = cursor }, cancel);
            if (result[key] is JsonArray items)
                foreach (var item in items)
                    try
                    {
                        if (item.Deserialize<T>() is { } entry) all.Add(entry);
                    }
                    catch (JsonException ex)
                    {
                        Log.Debug(ex, "An entry of {Method} could not be read", method);
                    }

            cursor = result["nextCursor"]?.GetValue<string>();
            if (string.IsNullOrEmpty(cursor)) break;
        }

        return all;
    }

    // ---- what the server sends ---------------------------------------------------------------------

    private void OnMessage(JsonNode node)
    {
        if (node is not JsonObject message) return;

        if (message["method"] is JsonValue method)
        {
            // a request of the server's (it has an id) or a notification
            if (message["id"] is { } id) _ = AnswerAsync(id, method.GetValue<string>());
            return;
        }

        if (message["id"] is not JsonValue idValue || !TryGetId(idValue, out long key) || !_pending.TryGetValue(key, out var tcs)) return;

        if (message["error"] is JsonObject error)
            tcs.TrySetException(new McpException(error["code"]?.GetValue<int>() ?? -32000, error["message"]?.GetValue<string>() ?? "The server reported an error."));
        else
            tcs.TrySetResult(message["result"] as JsonObject ?? new JsonObject());
    }

    private static bool TryGetId(JsonValue value, out long id)
    {
        if (value.TryGetValue(out id)) return true;
        return value.TryGetValue(out string? text) && long.TryParse(text, out id);
    }

    /// <summary>This client offers nothing a server could ask for, but it does answer a ping.</summary>
    private async Task AnswerAsync(JsonNode id, string method)
    {
        var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone() };
        if (method == "ping") reply["result"] = new JsonObject();
        else reply["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"Silo does not support '{method}'." };

        try
        {
            await _transport.SendAsync(reply, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Answering the server's '{Method}' failed", method);
        }
    }

    private void OnClosed(Exception? why)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;

        var failure = why ?? new IOException("The server closed the connection.");
        foreach (var tcs in _pending.Values) tcs.TrySetException(failure);

        Closed?.Invoke(why);
    }

    public async ValueTask DisposeAsync()
    {
        _transport.MessageReceived -= OnMessage;
        _transport.Closed          -= OnClosed;
        foreach (var tcs in _pending.Values) tcs.TrySetCanceled();

        await _transport.DisposeAsync();
    }
}

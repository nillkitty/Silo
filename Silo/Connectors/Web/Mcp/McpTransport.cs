using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Serilog;

namespace Silo.Connectors.Web.Mcp;

/// <summary>
///     Carries MCP's JSON-RPC messages to and from one server. The session above it does the protocol; a transport only
///     moves messages, and says when the other end has gone.
/// </summary>
public abstract class McpTransport : IAsyncDisposable
{
    /// <summary>Raised (on a worker thread) for every message the server sends.</summary>
    public event Action<JsonNode>? MessageReceived;

    /// <summary>Raised once when the server goes away; the exception says why, or is null for an orderly end.</summary>
    public event Action<Exception?>? Closed;

    public abstract Task StartAsync(CancellationToken cancel);

    public abstract Task SendAsync(JsonNode message, CancellationToken cancel);

    /// <summary>The protocol version the session agreed with the server (a streamable HTTP transport sends it with every request).</summary>
    public virtual void UseProtocolVersion(string version)
    {
    }

    public abstract ValueTask DisposeAsync();

    protected void Receive(JsonNode message)
    {
        MessageReceived?.Invoke(message);
    }

    protected void Close(Exception? why)
    {
        Closed?.Invoke(why);
    }
}

/// <summary>Starting the program a configured MCP server is, the way a command prompt would.</summary>
public static class McpProcess
{
    /// <summary>
    ///     The program to start for <paramref name="command" />: what is on the PATH under that name (with the extensions Windows
    ///     tries), because <c>npx</c> is really <c>npx.cmd</c>, which cannot be started directly and goes through <c>cmd.exe</c>.
    /// </summary>
    public static ProcessStartInfo CreateStartInfo(string command, IEnumerable<string> args, IReadOnlyDictionary<string, string>? env = null)
    {
        string path = Find(command) ?? command;
        var info = new ProcessStartInfo
                   {
                       UseShellExecute        = false,
                       CreateNoWindow         = true,
                       RedirectStandardInput  = true,
                       RedirectStandardOutput = true,
                       RedirectStandardError  = true,
                       StandardInputEncoding  = new UTF8Encoding(false),
                       StandardOutputEncoding = Encoding.UTF8,
                       StandardErrorEncoding  = Encoding.UTF8
                   };

        if (path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            // cmd.exe has its own quoting: the whole line after /c goes in one more pair of quotes
            info.FileName  = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            info.Arguments = "/d /s /c \"" + string.Join(' ', new[] { path }.Concat(args).Select(Quote)) + "\"";
        }
        else
        {
            info.FileName = path;
            foreach (string a in args) info.ArgumentList.Add(a);
        }

        if (env is not null)
            foreach (var (key, value) in env)
                info.Environment[key] = value;

        return info;
    }

    private static string Quote(string arg)
    {
        return arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"', '&', '|', '<', '>', '^']) < 0 ? arg : "\"" + arg.Replace("\"", "\\\"") + "\"";
    }

    /// <summary>The full path of a program, looked for as Windows would (the folders of PATH, each name tried with the PATHEXT extensions); null if there is none.</summary>
    public static string? Find(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        string[] extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries);
        bool     hasPath    = command.Contains('\\') || command.Contains('/');
        string[] folders    = hasPath ? [string.Empty] : (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (string folder in folders)
        {
            string stem;
            try
            {
                stem = hasPath ? command : Path.Combine(folder.Trim('"'), command);
            }
            catch (ArgumentException)
            {
                continue;
            }

            foreach (string ext in extensions)
                if (global::System.IO.File.Exists(stem + ext))
                    return stem + ext;

            // a name that already has its extension (npx.cmd, node.exe)
            if (Path.HasExtension(stem) && global::System.IO.File.Exists(stem)) return stem;
        }

        return null;
    }
}

/// <summary>A server that is a program Silo starts, spoken to a line at a time on its standard input and output.</summary>
public sealed class McpStdioTransport(McpServer server) : McpTransport
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private          Process?      _process;

    public override Task StartAsync(CancellationToken cancel)
    {
        var info = McpProcess.CreateStartInfo(server.Command, server.Args, server.Env);
        _process = Process.Start(info) ?? throw new InvalidOperationException($"'{server.Command}' could not be started.");

        _ = Task.Run(ReadOutputAsync);
        _ = Task.Run(ReadErrorsAsync);
        return Task.CompletedTask;
    }

    private async Task ReadOutputAsync()
    {
        var process = _process!;
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    if (JsonNode.Parse(line) is { } message) Receive(message);
                }
                catch (global::System.Text.Json.JsonException)
                {
                    Log.Debug("MCP server {Server} wrote a line that is not JSON: {Line}", server.Name, line);
                }
            }

            Close(null);
        }
        catch (Exception ex)
        {
            Close(ex);
        }
    }

    private async Task ReadErrorsAsync()
    {
        try
        {
            while (await _process!.StandardError.ReadLineAsync() is { } line)
                Log.Debug("MCP server {Server}: {Line}", server.Name, line);
        }
        catch (Exception)
        {
            /* the process went away */
        }
    }

    public override async Task SendAsync(JsonNode message, CancellationToken cancel)
    {
        var process = _process ?? throw new InvalidOperationException("The server has not been started.");
        await _writeGate.WaitAsync(cancel);
        try
        {
            await process.StandardInput.WriteLineAsync(message.ToJsonString().AsMemory(), cancel);
            await process.StandardInput.FlushAsync(cancel);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        var process = _process;
        _process = null;
        if (process is null) return;

        try
        {
            process.StandardInput.Close(); // a server stops when its input ends
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(wait.Token);
        }
        catch (Exception)
        {
            try
            {
                process.Kill(true);
            }
            catch (Exception)
            {
                /* already gone */
            }
        }
        finally
        {
            process.Dispose();
        }
    }
}

/// <summary>A server at a web address (streamable HTTP): each message is a POST, answered with JSON or with a stream of events.</summary>
public sealed class McpHttpTransport(HttpClient http, Uri endpoint) : McpTransport
{
    private string? _protocol;
    private string? _sessionId;

    public override Task StartAsync(CancellationToken cancel)
    {
        return Task.CompletedTask;
    }

    public override void UseProtocolVersion(string version)
    {
        _protocol = version;
    }

    public override async Task SendAsync(JsonNode message, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                            {
                                Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json")
                            };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        if (_sessionId is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        if (_protocol is not null) request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _protocol);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var ids)) _sessionId = ids.FirstOrDefault();

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"The server answered {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);

        if (response.StatusCode == global::System.Net.HttpStatusCode.Accepted || response.Content.Headers.ContentLength == 0) return;

        await using var body = await response.Content.ReadAsStreamAsync(cancel);
        if (string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            await ReadEventsAsync(body, cancel);
            return;
        }

        var node = await JsonNode.ParseAsync(body, cancellationToken: cancel);
        if (node is JsonArray batch)
        {
            foreach (var item in batch)
                if (item is not null)
                    Receive(item);
        }
        else if (node is not null)
        {
            Receive(node);
        }
    }

    private async Task ReadEventsAsync(Stream body, CancellationToken cancel)
    {
        using var reader = new StreamReader(body, Encoding.UTF8);
        var       data   = new StringBuilder();

        while (await reader.ReadLineAsync(cancel) is { } line)
        {
            if (line.Length == 0)
            {
                Dispatch(data);
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.AsSpan(5).TrimStart(' '));
            }
        }

        Dispatch(data);
    }

    private void Dispatch(StringBuilder data)
    {
        if (data.Length == 0) return;

        string text = data.ToString();
        data.Clear();
        try
        {
            if (JsonNode.Parse(text) is { } message) Receive(message);
        }
        catch (global::System.Text.Json.JsonException)
        {
            Log.Debug("MCP server {Endpoint} sent an event that is not JSON: {Text}", endpoint, text);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (_sessionId is null) return;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, endpoint);
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var _    = await http.SendAsync(request, wait.Token);
        }
        catch (Exception)
        {
            /* the session ends on its own */
        }
    }
}

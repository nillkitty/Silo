using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Silo.Connectors.Web.Mcp;

/// <summary>
///     <c>silo mcp</c>: what an AI tool that starts MCP servers as programs runs to reach the Silo that is open. It reads
///     requests a line at a time from standard input, posts each to the running app's MCP server (<see cref="McpHost" />) with
///     the token, and writes the answers a line at a time to standard output. Runs without WPF and without <see cref="App" />.
/// </summary>
internal static class McpBridge
{
    public static int Run(string[] args)
    {
        var settings = McpServerSettings.Load();
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);

        using var input  = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };

        while (input.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonNode? id = null;
            try
            {
                id = (JsonNode.Parse(line) as JsonObject)?["id"]?.DeepClone();
                var reply = Post(http, settings.Port, line);
                if (reply is not null) output.WriteLine(reply);
            }
            catch (Exception ex)
            {
                // a request needs an answer or the AI tool waits for ever; a notification does not
                if (id is not null) output.WriteLine(McpServerCore.Error(id, -32000, $"Silo did not answer: {ex.Message} Is Silo running with its MCP server turned on (Tools > MCP Server)?").ToJsonString());
            }
        }

        return 0;
    }

    private static string? Post(HttpClient http, int port, string line)
    {
        using var content = new StringContent(line, Encoding.UTF8, "application/json");
        using var response = http.PostAsync($"http://127.0.0.1:{port}/mcp", content).GetAwaiter().GetResult();

        if (response.StatusCode == global::System.Net.HttpStatusCode.Accepted) return null;
        response.EnsureSuccessStatusCode();

        string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        return JsonNode.Parse(body)?.ToJsonString(); // one line, whatever the server wrote
    }
}

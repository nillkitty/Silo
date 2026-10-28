using System.Text.Json.Nodes;
using Silo.Connectors.Web.Mcp;

namespace Silo.Tests.Connectors;

/// <summary>The MCP client session (over a transport that answers like a server would) and the handshake of Silo's own server.</summary>
public class McpTests
{
    /// <summary>A server in memory: answers the handshake and a tool list.</summary>
    private sealed class FakeServer : McpTransport
    {
        public override Task StartAsync(CancellationToken cancel) => Task.CompletedTask;

        public override Task SendAsync(JsonNode message, CancellationToken cancel)
        {
            var request = message.AsObject();
            if (request["id"] is null) return Task.CompletedTask; // a notification

            JsonObject result = request["method"]!.GetValue<string>() switch
                                {
                                    "initialize" => new JsonObject
                                                    {
                                                        ["protocolVersion"] = "2025-06-18",
                                                        ["capabilities"]    = new JsonObject { ["tools"] = new JsonObject() },
                                                        ["serverInfo"]      = new JsonObject { ["name"] = "fake", ["version"] = "1" }
                                                    },
                                    "tools/list" => new JsonObject
                                                    {
                                                        ["tools"] = new JsonArray(new JsonObject
                                                                                  {
                                                                                      ["name"]        = "echo",
                                                                                      ["description"] = "Says it back",
                                                                                      ["inputSchema"] = new JsonObject { ["type"] = "object" }
                                                                                  })
                                                    },
                                    _ => throw new InvalidOperationException()
                                };

            Receive(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(), ["result"] = result });
            return Task.CompletedTask;
        }

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Test]
    public async Task Session_does_the_handshake_and_lists_tools()
    {
        await using var session = new McpSession(new FakeServer());
        await session.StartAsync();

        Assert.That(session.Server!.ServerInfo.Name, Is.EqualTo("fake"));
        var tools = await session.ListToolsAsync();
        Assert.That(tools.Select(t => t.Name), Is.EqualTo(new[] { "echo" }));
    }

    [Test]
    public async Task Server_handshake_echoes_a_supported_version_and_offers_tools()
    {
        var request = new JsonObject
                      {
                          ["jsonrpc"] = "2.0",
                          ["id"]      = 1,
                          ["method"]  = "initialize",
                          ["params"]  = new JsonObject { ["protocolVersion"] = "2025-03-26" }
                      };

        var reply = await McpServerCore.HandleAsync(request, CancellationToken.None);

        Assert.That(reply!["result"]!["protocolVersion"]!.GetValue<string>(), Is.EqualTo("2025-03-26"));
        Assert.That(reply["result"]!["capabilities"]!["tools"], Is.Not.Null);
    }

    [Test]
    public async Task Server_ignores_notifications_and_rejects_unknown_methods()
    {
        Assert.That(await McpServerCore.HandleAsync(new JsonObject { ["method"] = "notifications/initialized" }, CancellationToken.None), Is.Null);

        var reply = await McpServerCore.HandleAsync(new JsonObject { ["id"] = 7, ["method"] = "nope" }, CancellationToken.None);
        Assert.That(reply!["error"]!["code"]!.GetValue<int>(), Is.EqualTo(-32601));
    }

    [Test]
    public async Task Server_lists_its_tools()
    {
        var reply = await McpServerCore.HandleAsync(new JsonObject { ["id"] = 2, ["method"] = "tools/list" }, CancellationToken.None);

        var names = reply!["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.That(names, Does.Contain("silo_list_silos").And.Contain("silo_browse"));
    }
}

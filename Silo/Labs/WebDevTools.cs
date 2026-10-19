using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace Silo.Labs;

/// <summary>
///     The Chrome DevTools protocol of a WebView2, as the Web Lab's inspectors use it: call a method, enable a domain once, and hear an
///     event. The inspectors never touch the <see cref="CoreWebView2" /> themselves.
/// </summary>
internal sealed class DevTools
{
    private readonly CoreWebView2      _core;
    private readonly HashSet<string>   _enabled = new(StringComparer.Ordinal);

    public DevTools(CoreWebView2 core)
    {
        _core = core;
    }

    /// <summary>Calls a protocol method (<c>DOM.getDocument</c>) with arguments (an object that writes as JSON); returns its result.</summary>
    public async Task<JObject> CallAsync(string method, object? args = null)
    {
        string json   = args is null ? "{}" : JsonConvert.SerializeObject(args);
        string result = await _core.CallDevToolsProtocolMethodAsync(method, json);
        return string.IsNullOrWhiteSpace(result) ? new JObject() : JObject.Parse(result);
    }

    /// <summary>As <see cref="CallAsync" />, but a failure (the page went away, the method needs something else) is logged and gives null.</summary>
    public async Task<JObject?> TryCallAsync(string method, object? args = null)
    {
        try
        {
            return await CallAsync(method, args);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "DevTools call {Method} failed", method);
            return null;
        }
    }

    /// <summary>Turns a domain on (<c>Network</c>, <c>DOM</c>...) the first time it is asked for; later asks do nothing.</summary>
    public async Task EnableAsync(string domain)
    {
        if (!_enabled.Add(domain)) return;

        try
        {
            await CallAsync(domain + ".enable");
        }
        catch (Exception ex)
        {
            _enabled.Remove(domain);
            Log.Debug(ex, "DevTools domain {Domain} could not be enabled", domain);
        }
    }

    /// <summary>Hears a protocol event (<c>Network.requestWillBeSent</c>) with its parameters. Subscribe before enabling the domain.</summary>
    public void On(string eventName, Action<JObject> handler)
    {
        var receiver = _core.GetDevToolsProtocolEventReceiver(eventName);
        receiver.DevToolsProtocolEventReceived += (_, e) =>
                                                  {
                                                      try
                                                      {
                                                          handler(JObject.Parse(e.ParameterObjectAsJson));
                                                      }
                                                      catch (Exception ex)
                                                      {
                                                          Log.Debug(ex, "Handling DevTools event {Event} failed", eventName);
                                                      }
                                                  };
    }
}

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Silo.Actions;

/// <summary>
///     How a process outside Silo runs an action in the stock instance, over the single-instance pipe (see
///     <c>Hosting/SingleInstance</c>). One UTF-8 line each way:
///     <list type="bullet">
///         <item><c>ACTION&lt;TAB&gt;name&lt;TAB&gt;{json object of arguments}</c> answered by <c>{"ok":true,"result":...}</c> or <c>{"ok":false,"error":"..."}</c></item>
///         <item><c>ACTIONS</c> answered by <c>{"ok":true,"result":[{name, description, parameters}, ...]}</c></item>
///     </list>
///     Only registered actions can be run or listed (<see cref="ActionRegistry.InvokeExternalAsync" />).
/// </summary>
public static class ActionProtocol
{
    public const string ActionVerb = "ACTION";
    public const string ListVerb   = "ACTIONS";

    public static bool IsActionLine(string? line)
    {
        return line is not null && (line == ListVerb || line.StartsWith(ActionVerb + "\t", StringComparison.Ordinal));
    }

    public static string BuildRequest(string name, IReadOnlyDictionary<string, object?>? args = null)
    {
        if (name.Contains('\t') || name.Contains('\n')) throw new ArgumentException("An action name can't contain tabs or line breaks.", nameof(name));
        return $"{ActionVerb}\t{name}\t{JsonConvert.SerializeObject(args ?? new Dictionary<string, object?>(), Formatting.None)}";
    }

    /// <summary>Runs a request line and returns the one-line reply. Never throws: a failure is the reply.</summary>
    public static async Task<string> HandleAsync(ActionRegistry registry, string line, CancellationToken ct = default)
    {
        try
        {
            if (line == ListVerb)
                return Ok(registry.Registered.Select(a => new
                {
                    name        = a.Name,
                    description = a.Description,
                    parameters  = a.Parameters.Select(p => new { name = p.Name, type = p.Type.Name, required = p.Required && p.Default is null })
                }));

            var parts = line.Split('\t', 3);
            if (parts.Length < 2 || parts[0] != ActionVerb || parts[1].Length == 0) return Error("Malformed action request.");

            var args = new Dictionary<string, object?>();
            if (parts.Length == 3 && parts[2].Trim().Length > 0)
            {
                if (JToken.Parse(parts[2]) is not JObject o) return Error("Action arguments must be a JSON object.");
                foreach (var p in o.Properties()) args[p.Name] = p.Value is JValue v ? v.Value : p.Value;
            }

            return Ok(await registry.InvokeExternalAsync(parts[1], args, ct));
        }
        catch (JsonException ex)
        {
            return Error("Action arguments aren't valid JSON: " + ex.Message);
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    private static string Ok(object? result)
    {
        JToken token;
        try
        {
            token = result is null ? JValue.CreateNull() : JToken.FromObject(result, JsonSerializer.Create(new JsonSerializerSettings
                                                                                                           {
                                                                                                               ReferenceLoopHandling = ReferenceLoopHandling.Ignore
                                                                                                           }));
        }
        catch (Exception)
        {
            token = result!.ToString();
        }

        return new JObject { ["ok"] = true, ["result"] = token }.ToString(Formatting.None);
    }

    private static string Error(string message)
    {
        return new JObject { ["ok"] = false, ["error"] = message }.ToString(Formatting.None);
    }
}

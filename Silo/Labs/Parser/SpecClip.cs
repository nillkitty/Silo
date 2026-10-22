using Newtonsoft.Json;

namespace Silo.Labs.Parser;

/// <summary>What Copy and Cut carry: pieces of a tree, or whole tokenizer rules.</summary>
public sealed class ClipData
{
    public List<SpecNode> Nodes { get; set; } = [];

    public List<TokenRule> TokenRules { get; set; } = [];
}

/// <summary>
///     The clipboard of the Parser Lab. By default it only keeps the text in memory (what the tests use); the Lab's session points
///     <see cref="Write" /> and <see cref="Read" /> at the system clipboard, so a piece copied in one window pastes in another.
/// </summary>
public static class SpecClip
{
    private const string Marker = "silo-parser-lab:";

    private static string? _memory;

    public static Func<string?> Read  { get; set; } = () => _memory;
    public static Action<string> Write { get; set; } = text => _memory = text;

    public static void Put(ClipData data)
    {
        Write(Marker + JsonConvert.SerializeObject(data, Formatting.None));
    }

    /// <summary>What is on the clipboard if it is something this Lab put there; null otherwise.</summary>
    public static ClipData? Get()
    {
        try
        {
            string? text = Read();
            if (text is null || !text.StartsWith(Marker, StringComparison.Ordinal)) return null;

            var data = JsonConvert.DeserializeObject<ClipData>(text[Marker.Length..]);
            return data is { Nodes.Count: > 0 } or { TokenRules.Count: > 0 } ? data : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

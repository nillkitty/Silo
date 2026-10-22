using System.IO;
using System.IO.Compression;
using System.Text;

namespace Silo.Labs.Streams;

/// <summary>Reels the Lab comes with, to start from.</summary>
public static class StreamLibrary
{
    public sealed record Entry(string Name, string Description, Func<StreamSetup> Make);

    private const string HttpRequest = "GET / HTTP/1.1\\r\\nHost: example.com\\r\\nConnection: close\\r\\n\\r\\n";

    public static IReadOnlyList<Entry> Entries { get; } =
    [
        Item("Empty scratch reel", "A memory stream with nothing in it: write to it, read it back, cut pieces out of it.",
              () => Setup("Scratch", StreamSource.ForMemory(), write: "Hello, reel!\\n")),
        Item("Zipped greeting", "GZip data in memory with a gunzip adapter on it: the Output side shows the text, the Input side the packed bytes it came from.",
              () => Setup("Zipped greeting", StreamSource.ForMemory(GZip("Hello from inside a GZip stream! " + string.Concat(Enumerable.Repeat("Again. ", 20)))), 8,
                          new AdapterSpec { Key = "gunzip", Side = AdapterSide.Read })),
        Item("Base64 text", "Base64 in memory with an unbase64 adapter: the Output side shows what it says.",
              () => Setup("Base64 text", StreamSource.ForMemory(Encoding.ASCII.GetBytes("U2lsbyBTdHJlYW0gTGFiOiBkZWNvZGluZyBhIGJ5dGUgYXQgYSB0aW1lLg==")), 4,
                          new AdapterSpec { Key = "unbase64", Side = AdapterSide.Read })),
        Item("Trickle", "A line of text that comes a byte at a time (a trickle adapter): a read never gets more than a byte, however many it asks for.",
              () => Setup("Trickle", StreamSource.ForMemory(Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog.")), 16,
                          new AdapterSpec { Key = "trickle", Arg = "1", Side = AdapterSide.Read })),
        Item("HTTP by hand", "A plain connection to a web server: write a request, then read the answer.",
              () => Setup("HTTP by hand", StreamSource.ForTcp("example.com", 80), 256, write: HttpRequest)),
        Item("HTTPS by hand", "The same with TLS on it: the Properties show the protocol, the cipher suite and the server's certificate.",
              () => Setup("HTTPS by hand", StreamSource.ForTcp("example.com", 443, true), 256, write: HttpRequest))
    ];

    public static Entry? Find(string name) => Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    private static Entry Item(string name, string description, Func<StreamSetup> make) => new(name, description, make);

    private static StreamSetup Setup(string name, StreamSource source, int step = 16, AdapterSpec? adapter = null, string write = "")
    {
        return new StreamSetup { Name = name, Source = source, Step = step, Adapters = adapter is null ? [] : [adapter], WriteText = write };
    }

    private static byte[] GZip(string text)
    {
        using var memory = new MemoryStream();
        using (var zip = new GZipStream(memory, CompressionLevel.Optimal, true)) zip.Write(Encoding.UTF8.GetBytes(text));
        return memory.ToArray();
    }
}

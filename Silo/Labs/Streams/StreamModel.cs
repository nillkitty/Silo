using System.IO;
using System.Globalization;
using System.Text;
using Newtonsoft.Json;

namespace Silo.Labs.Streams;

/// <summary>What the stream comes from.</summary>
public enum StreamKind
{
    /// <summary>A file on disk (<see cref="FileStream" />).</summary>
    File,

    /// <summary>Bytes in memory (<see cref="MemoryStream" />): a scratch reel that can be read, written and cut.</summary>
    Memory,

    /// <summary>A TCP connection (<see cref="System.Net.Sockets.NetworkStream" />).</summary>
    Tcp,

    /// <summary>A TCP connection with TLS on it (<see cref="System.Net.Security.SslStream" />).</summary>
    Tls,

    /// <summary>A stream somebody else already has open (it is not closed by the Lab).</summary>
    Existing
}

/// <summary>Where a stream comes from and how it is opened. Described, not open: opening it is what the Lab's window does.</summary>
public sealed class StreamSource
{
    public StreamKind Kind { get; set; } = StreamKind.Memory;

    /// <summary>The file, for <see cref="StreamKind.File" />.</summary>
    public string Path { get; set; } = string.Empty;

    public FileMode Mode { get; set; } = FileMode.Open;

    public FileAccess Access { get; set; } = FileAccess.Read;

    public FileShare Share { get; set; } = FileShare.Read;

    /// <summary>What a memory stream starts with.</summary>
    public byte[] Data { get; set; } = [];

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; }

    /// <summary>The name the server's certificate has to be for, for <see cref="StreamKind.Tls" />; the host when empty.</summary>
    public string? ServerName { get; set; }

    /// <summary>Whether a certificate that does not check out is accepted anyway (to look at servers with certificates of their own making).</summary>
    public bool TrustAnyCertificate { get; set; }

    /// <summary>The stream itself, for <see cref="StreamKind.Existing" /> (never saved).</summary>
    [JsonIgnore]
    public Stream? Live { get; set; }

    /// <summary>A name for the stream in titles and lists.</summary>
    [JsonIgnore]
    public string Label =>
        Kind switch
        {
            StreamKind.File     => string.IsNullOrWhiteSpace(Path) ? "(no file)" : System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : Path,
            StreamKind.Memory   => Data.Length == 0 ? "memory" : $"memory ({Data.Length:N0} bytes)",
            StreamKind.Tcp      => $"tcp {Host}:{Port}",
            StreamKind.Tls      => $"tls {Host}:{Port}",
            StreamKind.Existing => Live?.GetType().Name ?? "stream",
            _                   => "stream"
        };

    /// <summary>The source as a line a person can read or type back (<see cref="TryParse" />).</summary>
    public override string ToString()
    {
        return Kind switch
               {
                   StreamKind.File   => Path,
                   StreamKind.Memory => Data.Length == 0 ? "mem:" : "mem:hex:" + Convert.ToHexString(Data.AsSpan(0, Math.Min(Data.Length, 64))) + (Data.Length > 64 ? "…" : string.Empty),
                   StreamKind.Tcp    => $"tcp://{Host}:{Port}",
                   StreamKind.Tls    => $"tls://{Host}:{Port}",
                   _                 => Label
               };
    }

    /// <summary>What is wrong with the way the source is set up, or null.</summary>
    public string? Problem()
    {
        switch (Kind)
        {
            case StreamKind.File:
                if (string.IsNullOrWhiteSpace(Path)) return "Choose a file.";
                if (Mode == FileMode.Append && Access != FileAccess.Write) return "A file opened to append can only be written.";
                if (Mode is FileMode.Create or FileMode.CreateNew or FileMode.Truncate && Access == FileAccess.Read) return "A file that is created or emptied has to be opened for writing.";
                return null;
            case StreamKind.Tcp or StreamKind.Tls:
                if (string.IsNullOrWhiteSpace(Host)) return "Give the host to connect to.";
                if (Port is < 1 or > 65535) return "The port is a number from 1 to 65535.";
                return null;
            case StreamKind.Existing:
                return Live is null ? "The stream is gone." : null;
            default:
                return null;
        }
    }

    public StreamSource Clone()
    {
        var copy = (StreamSource)MemberwiseClone();
        copy.Data = [.. Data];
        return copy;
    }

    public static StreamSource ForFile(string path, FileAccess access = FileAccess.Read, FileMode mode = FileMode.Open)
    {
        return new StreamSource { Kind = StreamKind.File, Path = path, Access = access, Mode = mode, Share = access == FileAccess.Read ? FileShare.Read : FileShare.ReadWrite };
    }

    public static StreamSource ForMemory(byte[]? data = null)
    {
        return new StreamSource { Kind = StreamKind.Memory, Data = data ?? [] };
    }

    public static StreamSource ForTcp(string host, int port, bool tls = false)
    {
        return new StreamSource { Kind = tls ? StreamKind.Tls : StreamKind.Tcp, Host = host, Port = port };
    }

    public static StreamSource ForStream(Stream stream)
    {
        return new StreamSource { Kind = StreamKind.Existing, Live = stream };
    }

    /// <summary>
    ///     Reads what a person typed as a source: <c>tcp://host:port</c>, <c>tls://host:port</c> (<c>http://</c> and <c>https://</c> mean the usual ports),
    ///     <c>mem:</c>, <c>mem:hex:0A1B…</c>, <c>mem:text:hello</c>, <c>file:///path</c>, or anything else as the path of a file.
    /// </summary>
    public static bool TryParse(string? text, out StreamSource? source)
    {
        source = null;
        string line = (text ?? string.Empty).Trim().Trim('"');
        if (line.Length == 0) return false;

        if (line.StartsWith("mem:", StringComparison.OrdinalIgnoreCase))
        {
            string rest = line[4..];
            if (rest.StartsWith("hex:", StringComparison.OrdinalIgnoreCase))
            {
                if (!StreamPayload.TryParseHex(rest[4..], out var bytes)) return false;

                source = ForMemory(bytes);
                return true;
            }

            source = ForMemory(Encoding.UTF8.GetBytes(StreamPayload.Unescape(rest.StartsWith("text:", StringComparison.OrdinalIgnoreCase) ? rest[5..] : rest)));
            return true;
        }

        if (Uri.TryCreate(line, UriKind.Absolute, out var uri) && line.Contains("://", StringComparison.Ordinal))
        {
            switch (uri.Scheme.ToLowerInvariant())
            {
                case "tcp" or "http":
                    source = ForTcp(uri.Host, uri.Port > 0 ? uri.Port : 80);
                    return true;
                case "tls" or "ssl" or "https":
                    source = ForTcp(uri.Host, uri.Port > 0 ? uri.Port : 443, true);
                    return true;
                case "file":
                    source = ForFile(uri.LocalPath);
                    return true;
                default:
                    return false;
            }
        }

        source = ForFile(line);
        return true;
    }

    /// <summary>Whether text is plainly a way of naming a stream (so a menu can offer the Lab for it without asking the disk).</summary>
    public static bool LooksLikeSource(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        return text.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("tls://", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("ssl://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("mem:", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Which of the two directions an adapter works on.</summary>
public enum AdapterSide
{
    /// <summary>On what is read and on what is written.</summary>
    Both,

    /// <summary>Only on what is read: the stream is the source and the adapter changes what comes out.</summary>
    Read,

    /// <summary>Only on what is written: what is written goes through the adapter before it reaches the stream.</summary>
    Write
}

/// <summary>One adapter in a chain: which one, with what argument, on which side.</summary>
public sealed class AdapterSpec
{
    public string Key { get; set; } = string.Empty;

    public string Arg { get; set; } = string.Empty;

    public AdapterSide Side { get; set; } = AdapterSide.Both;

    /// <summary>Off: the adapter stays in the chain but is left out (to compare with and without).</summary>
    public bool Enabled { get; set; } = true;

    public AdapterSpec Clone() => (AdapterSpec)MemberwiseClone();

    public override string ToString()
    {
        return Key + (Arg.Length > 0 ? " " + Arg : string.Empty) + (Side == AdapterSide.Both ? string.Empty : Side == AdapterSide.Read ? " (reads)" : " (writes)") + (Enabled ? string.Empty : " [off]");
    }
}

/// <summary>Everything about one Stream Lab window that is worth keeping: the source, the chain of adapters, and how the reel is stepped.</summary>
public sealed class StreamSetup
{
    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public StreamSource Source { get; set; } = new();

    /// <summary>The adapters, in the order the data goes through them: on a read from the stream outwards, on a write from the writer towards the stream.</summary>
    public List<AdapterSpec> Adapters { get; set; } = [];

    /// <summary>The adapters applied to the recorded output after the fact (they read the strip, not the stream): the Processed strip is what they make of it.</summary>
    public List<AdapterSpec> Post { get; set; } = [];

    /// <summary>How many bytes a read takes.</summary>
    public int Step { get; set; } = 16;

    /// <summary>The text to write, as it was typed (so it is still there when the setup is kept).</summary>
    public string WriteText { get; set; } = string.Empty;

    /// <summary>How the text to write stands for bytes.</summary>
    public StreamPayload.Format WriteFormat { get; set; } = StreamPayload.Format.Text;

    public StreamSetup Clone()
    {
        var copy = (StreamSetup)MemberwiseClone();
        copy.Source   = Source.Clone();
        copy.Adapters = Adapters.Select(a => a.Clone()).ToList();
        copy.Post     = Post.Select(a => a.Clone()).ToList();
        return copy;
    }

    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Source.ToString() : Name;
}

/// <summary>Reading and writing the bytes a person types or sees: hex, text with escapes, patterns.</summary>
public static class StreamPayload
{
    /// <summary>How the typed text stands for bytes.</summary>
    public enum Format
    {
        Text,
        Hex,
        Base64
    }

    public static readonly IReadOnlyList<string> Patterns = ["Zeros", "Ones (FF)", "Counting", "Letters", "Random", "Line feeds"];

    /// <summary>Reads hex digits; spaces, commas, dashes, colons and 0x prefixes between the bytes are ignored.</summary>
    public static bool TryParseHex(string? text, out byte[] bytes)
    {
        var digits = new StringBuilder();
        string s = (text ?? string.Empty).Replace("0x", " ", StringComparison.OrdinalIgnoreCase).Replace("\\x", " ", StringComparison.OrdinalIgnoreCase);
        foreach (char c in s)
        {
            if (Uri.IsHexDigit(c)) digits.Append(c);
            else if (!char.IsWhiteSpace(c) && c is not (',' or '-' or ':' or '_' or ';' or '.')) {
                bytes = [];
                return false;
            }
        }

        if (digits.Length % 2 != 0)
        {
            bytes = [];
            return false;
        }

        bytes = Convert.FromHexString(digits.ToString());
        return true;
    }

    /// <summary>Reads <c>\n \r \t \0 \\ \xHH \uHHHH</c>; any other backslash stays as it is.</summary>
    public static string Unescape(string text)
    {
        if (!text.Contains('\\')) return text;

        var result = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\' || i + 1 >= text.Length)
            {
                result.Append(text[i]);
                continue;
            }

            char next = text[i + 1];
            switch (next)
            {
                case 'n':
                    result.Append('\n');
                    i++;
                    break;
                case 'r':
                    result.Append('\r');
                    i++;
                    break;
                case 't':
                    result.Append('\t');
                    i++;
                    break;
                case '0':
                    result.Append('\0');
                    i++;
                    break;
                case '\\':
                    result.Append('\\');
                    i++;
                    break;
                case 'x' when i + 3 < text.Length + 0 && Uri.IsHexDigit(text[i + 2]) && Uri.IsHexDigit(text[i + 3]):
                    result.Append((char)int.Parse(text.AsSpan(i + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 3;
                    break;
                case 'u' when i + 5 < text.Length + 0 && text.Skip(i + 2).Take(4).All(Uri.IsHexDigit):
                    result.Append((char)int.Parse(text.AsSpan(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 5;
                    break;
                default:
                    result.Append('\\');
                    break;
            }
        }

        return result.ToString();
    }

    /// <summary>The bytes the typed text stands for; throws <see cref="FormatException" /> (with what is wrong) when it is not valid for the format.</summary>
    public static byte[] ToBytes(string text, Format format, Encoding? encoding = null)
    {
        switch (format)
        {
            case Format.Hex:
                if (!TryParseHex(text, out var bytes)) throw new FormatException("That is not hex: use pairs of digits such as 48 65 6C 6C 6F.");
                return bytes;
            case Format.Base64:
                try
                {
                    return Convert.FromBase64String(string.Concat(text.Where(c => !char.IsWhiteSpace(c))));
                }
                catch (FormatException)
                {
                    throw new FormatException("That is not valid Base64.");
                }
            default:
                return (encoding ?? Encoding.UTF8).GetBytes(Unescape(text));
        }
    }

    /// <summary>The bytes typed in one box: <c>hex:48 65</c>, <c>b64:SGk=</c>, or text (with the escapes of <see cref="Unescape" />). Throws <see cref="FormatException" /> when it is not valid.</summary>
    public static byte[] FromTyped(string text)
    {
        if (text.StartsWith("hex:", StringComparison.OrdinalIgnoreCase)) return ToBytes(text[4..], Format.Hex);
        if (text.StartsWith("b64:", StringComparison.OrdinalIgnoreCase)) return ToBytes(text[4..], Format.Base64);

        return ToBytes(text, Format.Text);
    }

    /// <summary>
    ///     A made-up run of bytes for trying a stream with: <c>Zeros</c>, <c>Ones (FF)</c>, <c>Counting</c> (0, 1, 2 ... 255, 0 ...), <c>Letters</c>
    ///     (a-z repeated), <c>Random</c>, <c>Line feeds</c>.
    /// </summary>
    public static byte[] Pattern(string kind, int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));

        var data = new byte[count];
        switch (kind)
        {
            case "Ones (FF)":
                Array.Fill(data, (byte)0xFF);
                break;
            case "Counting":
                for (int i = 0; i < count; i++) data[i] = (byte)(i & 0xFF);
                break;
            case "Letters":
                for (int i = 0; i < count; i++) data[i] = (byte)('a' + i % 26);
                break;
            case "Random":
                Random.Shared.NextBytes(data);
                break;
            case "Line feeds":
                Array.Fill(data, (byte)'\n');
                break;
        }

        return data;
    }
}

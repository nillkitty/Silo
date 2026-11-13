using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Silo.Providers.Network;
using Telefrag.DI;

namespace Silo.Servers.Smtp;

/// <summary>
///     A mail sink: an SMTP server that takes every message, to any address, and keeps it as a row in a table of the Silo
///     instead of delivering it. For pointing an app's outgoing mail at while it is built or tested. It offers STARTTLS on
///     endpoints marked <c>+starttls</c> (and speaks TLS from the start on <c>+tls</c> ones, such as 465), and takes any
///     user name and password a client insists on sending.
/// </summary>
[Singleton(typeof(IServerService))]
public sealed partial class SmtpStub : IServerService
{
    /// <summary>The columns of the mail table, in order.</summary>
    public static readonly IReadOnlyList<string> Columns = ["Received", "From", "To", "Subject", "Size", "Client", "Helo", "Secure", "User", "Message"];

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);

    public string Name        => "smtp-stub";
    public string DisplayName => "SMTP stub";
    public string Description => "Takes mail to any address and keeps each message as a row in a table of the Silo.";

    public IReadOnlyList<PortProtocol> Protocols { get; } = [PortProtocol.Tcp];

    public IReadOnlyList<ServerEndpoint> DefaultEndpoints { get; } =
        [new(PortProtocol.Tcp, 25, ServerTls.StartTls), new(PortProtocol.Tcp, 465, ServerTls.Implicit), new(PortProtocol.Tcp, 587, ServerTls.StartTls)];

    public IReadOnlyList<ServerSetting> Settings { get; } =
    [
        new("table", "Table", ServerSettingKind.Table, "Mail", "The table each message is added to (made when it is missing).", Columns),
        new("hostname", "Host name", ServerSettingKind.Text, null, "The name the server greets with; this computer's name when empty."),
        new("maxSizeMb", "Largest message (MB)", ServerSettingKind.Number, "10", "Messages larger than this are turned away."),
        new("auth", "Offer AUTH", ServerSettingKind.Boolean, "true", "Offers AUTH PLAIN and LOGIN (any user name and password is taken) for clients that will not send without it.")
    ];

    public async Task ServeAsync(IServerConnection connection, IServerContext context, CancellationToken cancel)
    {
        string host    = context.Setting("hostname") is { Length: > 0 } h ? h : Environment.MachineName.ToLowerInvariant();
        long   maxSize = (long)((double.TryParse(context.Setting("maxSizeMb"), NumberStyles.Float, CultureInfo.InvariantCulture, out double mb) && mb > 0 ? mb : 10) * 1024 * 1024);
        bool   auth    = !string.Equals(context.Setting("auth"), "false", StringComparison.OrdinalIgnoreCase);

        var session = new Session(connection);
        await session.Send($"220 {host} ESMTP Silo SMTP stub ready", cancel);

        while (!cancel.IsCancellationRequested)
        {
            var line = await session.ReadLine(cancel);
            if (line is null) return;

            int    space = line.IndexOf(' ');
            string verb  = (space < 0 ? line : line[..space]).ToUpperInvariant();
            string arg   = space < 0 ? "" : line[(space + 1)..].Trim();

            switch (verb)
            {
                case "EHLO":
                {
                    session.Reset();
                    session.Helo = arg;
                    var lines = new List<string> { $"{host} hello {arg}", $"SIZE {maxSize}", "8BITMIME", "PIPELINING", "ENHANCEDSTATUSCODES", "SMTPUTF8" };
                    if (connection.CanStartTls) lines.Add("STARTTLS");
                    if (auth) lines.Add("AUTH PLAIN LOGIN");
                    await session.Send(string.Join("\r\n", lines.Select((l, i) => (i == lines.Count - 1 ? "250 " : "250-") + l)), cancel);
                    break;
                }
                case "HELO":
                    session.Reset();
                    session.Helo = arg;
                    await session.Send($"250 {host}", cancel);
                    break;
                case "STARTTLS":
                    if (!connection.CanStartTls)
                    {
                        await session.Send(connection.IsSecure ? "503 5.5.1 TLS is already on" : "454 4.7.0 TLS is not available here", cancel);
                        break;
                    }

                    await session.Send("220 2.0.0 Ready to start TLS", cancel);
                    await session.StartTls(cancel);
                    break;
                case "AUTH" when auth:
                    await Authenticate(session, arg, cancel);
                    break;
                case "MAIL":
                {
                    if (Address(arg, "FROM") is not { } from)
                    {
                        await session.Send("501 5.5.4 Syntax: MAIL FROM:<address>", cancel);
                        break;
                    }

                    if (SizeParam().Match(arg) is { Success: true } size && long.TryParse(size.Groups[1].Value, out long declared) && declared > maxSize)
                    {
                        await session.Send($"552 5.3.4 The message is larger than {maxSize} bytes", cancel);
                        break;
                    }

                    session.Reset();
                    session.From = from;
                    await session.Send("250 2.1.0 Ok", cancel);
                    break;
                }
                case "RCPT":
                    if (session.From is null)
                        await session.Send("503 5.5.1 MAIL FROM first", cancel);
                    else if (Address(arg, "TO") is not { Length: > 0 } to)
                        await session.Send("501 5.5.4 Syntax: RCPT TO:<address>", cancel);
                    else if (session.To.Count >= 1000)
                        await session.Send("452 4.5.3 Too many recipients", cancel);
                    else
                    {
                        session.To.Add(to);
                        await session.Send("250 2.1.5 Ok", cancel);
                    }

                    break;
                case "DATA":
                    if (session.To.Count == 0)
                    {
                        await session.Send("503 5.5.1 RCPT TO first", cancel);
                        break;
                    }

                    await session.Send("354 End data with <CR><LF>.<CR><LF>", cancel);
                    var (message, size2) = await session.ReadData(maxSize, cancel);
                    if (message is null)
                    {
                        if (size2 < 0) return; // the connection went
                        await session.Send($"552 5.3.4 The message is larger than {maxSize} bytes", cancel);
                    }
                    else
                    {
                        string id = Keep(context, connection, session, message, size2);
                        await session.Send($"250 2.0.0 Ok: kept as {id}", cancel);
                    }

                    session.Reset();
                    break;
                case "RSET":
                    session.Reset();
                    await session.Send("250 2.0.0 Ok", cancel);
                    break;
                case "NOOP":
                    await session.Send("250 2.0.0 Ok", cancel);
                    break;
                case "VRFY":
                    await session.Send("252 2.1.5 Send some mail and see", cancel);
                    break;
                case "HELP":
                    await session.Send("214 2.0.0 EHLO HELO MAIL RCPT DATA RSET NOOP QUIT STARTTLS AUTH", cancel);
                    break;
                case "QUIT":
                    await session.Send($"221 2.0.0 {host} closing", cancel);
                    return;
                default:
                    await session.Send("502 5.5.2 Command not recognised", cancel);
                    break;
            }
        }
    }

    private static async Task Authenticate(Session session, string arg, CancellationToken cancel)
    {
        var parts     = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string method = parts.Length > 0 ? parts[0].ToUpperInvariant() : "";
        string? initial = parts.Length > 1 ? parts[1] : null;

        switch (method)
        {
            case "PLAIN":
            {
                if (initial is null)
                {
                    await session.Send("334 ", cancel);
                    initial = await session.ReadLine(cancel);
                }

                if (Decode(initial) is not { } plain)
                {
                    await session.Send("501 5.5.2 Cannot read the credentials", cancel);
                    return;
                }

                var fields = plain.Split('\0');
                session.User = fields.Length >= 2 ? fields[1] : plain;
                break;
            }
            case "LOGIN":
            {
                if (initial is null)
                {
                    await session.Send("334 VXNlcm5hbWU6", cancel); // "Username:"
                    initial = await session.ReadLine(cancel);
                }

                var user = Decode(initial);
                await session.Send("334 UGFzc3dvcmQ6", cancel); // "Password:"
                var password = await session.ReadLine(cancel);
                if (user is null || password is null || password == "*")
                {
                    await session.Send("501 5.5.2 Authentication cancelled", cancel);
                    return;
                }

                session.User = user;
                break;
            }
            default:
                await session.Send("504 5.5.4 Use AUTH PLAIN or AUTH LOGIN", cancel);
                return;
        }

        await session.Send("235 2.7.0 Authentication successful", cancel);
    }

    private static string? Decode(string? base64)
    {
        if (base64 is null || base64 == "*") return null;

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64.Trim()));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The address of <c>FROM:&lt;a@b&gt; ...</c> (empty for the null sender <c>&lt;&gt;</c>); null when it is not written so.</summary>
    public static string? Address(string arg, string keyword)
    {
        var m = Regex.Match(arg, $@"^{keyword}\s*:\s*(?:<(?<a>[^>]*)>|(?<a>\S+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return m.Success ? m.Groups["a"].Value.Trim() : null;
    }

    [GeneratedRegex(@"\bSIZE=(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizeParam();

    private static string Keep(IServerContext context, IServerConnection connection, Session session, string message, long size)
    {
        string id    = Guid.NewGuid().ToString("n")[..12];
        string table = context.Setting("table") ?? "Mail";
        string subject = HeaderOf(message, "Subject");
        var row = new Dictionary<string, string>
                  {
                      ["Received"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                      ["From"]     = session.From ?? "",
                      ["To"]       = string.Join(", ", session.To),
                      ["Subject"]  = subject,
                      ["Size"]     = size.ToString(CultureInfo.InvariantCulture),
                      ["Client"]   = connection.Remote.ToString(),
                      ["Helo"]     = session.Helo ?? "",
                      ["Secure"]   = connection.IsSecure ? "yes" : "no",
                      ["User"]     = session.User ?? "",
                      ["Message"]  = message
                  };
        context.Tables.Append(table, Columns, row);
        context.Log($"SMTP {connection.Remote.Address}: {session.From} -> {string.Join(", ", session.To)} \"{subject}\" ({size} bytes) kept in {table}");
        return id;
    }

    /// <summary>The value of a header of a message (unfolded, with encoded words decoded); empty when it has none.</summary>
    public static string HeaderOf(string message, string header)
    {
        int end = message.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = end < 0 ? message : message[..end];
        var unfolded = Regex.Replace(head, @"\r\n[ \t]+", " ");
        foreach (var line in unfolded.Split("\r\n"))
            if (line.StartsWith(header + ":", StringComparison.OrdinalIgnoreCase))
                return DecodeWords(line[(header.Length + 1)..].Trim());

        return "";
    }

    /// <summary>Decodes RFC 2047 encoded words (<c>=?utf-8?B?...?=</c>, <c>=?iso-8859-1?Q?...?=</c>).</summary>
    public static string DecodeWords(string text)
    {
        var decoded = EncodedWord().Replace(text, m =>
                                                  {
                                                      try
                                                      {
                                                          var    encoding = Encoding.GetEncoding(m.Groups[1].Value);
                                                          string data     = m.Groups[3].Value;
                                                          byte[] bytes = m.Groups[2].Value.Equals("B", StringComparison.OrdinalIgnoreCase)
                                                                             ? Convert.FromBase64String(data)
                                                                             : QuotedPrintable(data.Replace('_', ' '));
                                                          return encoding.GetString(bytes);
                                                      }
                                                      catch (Exception ex) when (ex is ArgumentException or FormatException)
                                                      {
                                                          return m.Value;
                                                      }
                                                  });
        return Regex.Replace(decoded, @"(?<=[^\s])\s+(?==\?)", ""); // whitespace between adjacent encoded words goes
    }

    [GeneratedRegex(@"=\?([^?]+)\?([BbQq])\?([^?]*)\?=", RegexOptions.CultureInvariant)]
    private static partial Regex EncodedWord();

    private static byte[] QuotedPrintable(string text)
    {
        var bytes = new List<byte>();
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '=' && i + 2 < text.Length && Uri.IsHexDigit(text[i + 1]) && Uri.IsHexDigit(text[i + 2]))
            {
                bytes.Add(Convert.ToByte(text.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                bytes.Add((byte)text[i]);
            }

        return bytes.ToArray();
    }

    /// <summary>One client's conversation: the line reader over the (possibly upgraded) stream, and the message being put together.</summary>
    private sealed class Session(IServerConnection connection)
    {
        private const int MaxLine = 64 * 1024;

        private byte[] _buffer = new byte[16 * 1024];
        private int    _start, _end;

        public string?      Helo { get; set; }
        public string?      User { get; set; }
        public string?      From { get; set; }
        public List<string> To   { get; } = [];

        public void Reset()
        {
            From = null;
            To.Clear();
        }

        public async Task Send(string reply, CancellationToken cancel)
        {
            await connection.Stream.WriteAsync(Encoding.UTF8.GetBytes(reply + "\r\n"), cancel);
            await connection.Stream.FlushAsync(cancel);
        }

        public async Task StartTls(CancellationToken cancel)
        {
            _start = _end = 0; // anything the client sent before the handshake is not to be trusted (RFC 3207)
            await connection.StartTlsAsync(cancel);
            Helo = null;
            User = null;
            Reset();
        }

        /// <summary>The next line without its line break, as bytes; null when the connection closed or went quiet for too long.</summary>
        private async Task<byte[]?> ReadLineBytes(CancellationToken cancel)
        {
            while (true)
            {
                int nl = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
                if (nl >= 0)
                {
                    int len = nl - _start;
                    if (len > 0 && _buffer[nl - 1] == '\r') len--;
                    var line = _buffer.AsSpan(_start, len).ToArray();
                    _start = nl + 1;
                    return line;
                }

                if (_end - _start >= MaxLine) throw new IOException("A line was too long.");

                if (_start > 0)
                {
                    Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                    _end   -= _start;
                    _start =  0;
                }

                if (_end == _buffer.Length) Array.Resize(ref _buffer, _buffer.Length * 2);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timeout.CancelAfter(CommandTimeout);
                int read;
                try
                {
                    read = await connection.Stream.ReadAsync(_buffer.AsMemory(_end), timeout.Token);
                }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                {
                    return null;
                }

                if (read == 0) return null;

                _end += read;
            }
        }

        public async Task<string?> ReadLine(CancellationToken cancel)
        {
            return await ReadLineBytes(cancel) is { } line ? Encoding.UTF8.GetString(line) : null;
        }

        /// <summary>
        ///     The message after DATA, up to the line holding only a dot, with dot-stuffing undone. Null when it was larger than
        ///     <paramref name="maxSize" /> (the size is then how much came) or the connection went (the size is then -1).
        /// </summary>
        public async Task<(string? Message, long Size)> ReadData(long maxSize, CancellationToken cancel)
        {
            var  body = new MemoryStream();
            long size = 0;
            while (true)
            {
                if (await ReadLineBytes(cancel) is not { } line) return (null, -1);
                if (line is [(byte)'.']) break;

                var data = line is [(byte)'.', ..] ? line.AsSpan(1) : line;
                size += data.Length + 2;
                if (size > maxSize) continue; // read to the end, then refuse

                body.Write(data);
                body.Write("\r\n"u8);
            }

            return size > maxSize ? (null, size) : (Encoding.UTF8.GetString(body.ToArray()), size);
        }
    }
}

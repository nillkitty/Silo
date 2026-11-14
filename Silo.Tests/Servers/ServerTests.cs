using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Silo.Certificates;
using Silo.Providers.Network;
using Silo.Servers;
using Silo.Servers.Dns;
using Silo.Servers.Smtp;

namespace Silo.Tests.Servers;

/// <summary>Tables in memory, standing in for a Silo's.</summary>
internal sealed class MemoryTables : IServerTables
{
    public Dictionary<string, (List<string> Columns, List<List<string>> Rows)> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ServerTable? Read(string name)
    {
        lock (Tables)
            return Tables.TryGetValue(name, out var t) ? new ServerTable(name, t.Columns.ToList(), t.Rows.Select(r => (IReadOnlyList<string>)r.ToList()).ToList()) : null;
    }

    public void Append(string name, IReadOnlyList<string> columns, IReadOnlyDictionary<string, string> row)
    {
        lock (Tables)
        {
            if (!Tables.TryGetValue(name, out var t)) Tables[name] = t = ([..columns], []);
            t.Rows.Add(t.Columns.Select(c => row.GetValueOrDefault(c, "")).ToList());
        }
    }

    public void Add(string name, string[] columns, params string[][] rows)
    {
        Tables[name] = ([..columns], rows.Select(r => r.ToList()).ToList());
    }
}

public class EndpointTextTests
{
    [Test]
    public void ParsesAndGroupsEndpoints()
    {
        var parsed = EndpointText.Parse("UDP:53; tcp:53\nTCP:25,587+starttls; TCP:465 + tls; 127.0.0.1/TCP:2525; [::1]/UDP:5353");

        Assert.That(parsed, Has.Count.EqualTo(7));
        Assert.That(parsed[2], Is.EqualTo(new ServerEndpoint(PortProtocol.Tcp, 25, ServerTls.StartTls)));
        Assert.That(parsed[4].Tls, Is.EqualTo(ServerTls.Implicit));
        Assert.That(parsed[5].Address, Is.EqualTo("127.0.0.1"));
        Assert.That(parsed[6].Address, Is.EqualTo("::1"));
        Assert.That(EndpointText.Format(parsed), Is.EqualTo("UDP:53; TCP:53; TCP:25,587+starttls; TCP:465+tls; 127.0.0.1/TCP:2525; [::1]/UDP:5353"));
    }

    [TestCase("TCP:0")]
    [TestCase("TCP:70000")]
    [TestCase("UDP:53+tls")]
    [TestCase("HTTP:80")]
    [TestCase("nope/TCP:25")]
    public void RejectsWhatIsNotAnEndpoint(string text)
    {
        Assert.Throws<FormatException>(() => EndpointText.Parse(text));
    }
}

public class DnsStubTests
{
    private static readonly IReadOnlyList<DnsRecord> Records = DnsStub.ReadTable(
        new ServerTable("DNS", ["Name", "Type", "Value", "TTL"],
                        [
                            ["router.lab.test", "", "192.168.1.1", ""],
                            ["nas.lab.test.", "A", "192.168.1.20", "300"],
                            ["nas.lab.test", "AAAA", "fd00::20", ""],
                            ["files.lab.test", "", "nas.lab.test", ""],
                            ["*.dev.test", "", "10.0.0.5", ""],
                            ["lab.test", "MX", "10 mail.lab.test", ""],
                            ["lab.test", "TXT", "v=spf1 -all", ""],
                            ["broken.lab.test", "A", "not an address", ""]
                        ]), 60);

    internal static byte[] Query(string name, DnsType type, ushort id = 0x1234)
    {
        var bytes = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in name.Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(label));
        }

        bytes.AddRange([0, (byte)((ushort)type >> 8), (byte)type, 0, 1]);
        return bytes.ToArray();
    }

    /// <summary>The rcode and the answers' data of a response.</summary>
    internal static (int Rcode, List<(DnsType Type, int Ttl, byte[] Data)> Answers) Parse(byte[] reply)
    {
        int rcode   = reply[3] & 0xF;
        int answers = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(6));
        Assert.That(DnsStub.TryReadName(reply, 12, out _, out int at), Is.True);
        at += 4;
        var list = new List<(DnsType, int, byte[])>();
        for (int i = 0; i < answers; i++)
        {
            Assert.That(DnsStub.TryReadName(reply, at, out _, out at), Is.True);
            var type = (DnsType)BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(at));
            int ttl  = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(at + 4));
            int len  = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(at + 8));
            list.Add((type, ttl, reply.AsSpan(at + 10, len).ToArray()));
            at += 10 + len;
        }

        return (rcode, list);
    }

    private static (int Rcode, List<(DnsType Type, int Ttl, byte[] Data)> Answers) Ask(string name, DnsType type)
    {
        return Parse(DnsStub.Answer(Query(name, type), Records, 512, out _)!);
    }

    [Test]
    public void AnswersAnAddressFromTheTable()
    {
        var (rcode, answers) = Ask("ROUTER.lab.test", DnsType.A);

        Assert.That(rcode, Is.Zero);
        Assert.That(answers.Single().Data, Is.EqualTo(new byte[] { 192, 168, 1, 1 }));
        Assert.That(answers.Single().Ttl, Is.EqualTo(60));
    }

    [Test]
    public void KeepsTheIdAndMarksTheReplyAuthoritative()
    {
        var reply = DnsStub.Answer(Query("router.lab.test", DnsType.A, 0xBEEF), Records, 512, out _)!;

        Assert.That(BinaryPrimitives.ReadUInt16BigEndian(reply), Is.EqualTo(0xBEEF));
        Assert.That(reply[2] & 0x80, Is.Not.Zero, "QR");
        Assert.That(reply[2] & 0x04, Is.Not.Zero, "AA");
        Assert.That(reply[2] & 0x01, Is.Not.Zero, "RD copied");
    }

    [Test]
    public void AnswersEachTypeOfAName()
    {
        Assert.That(Ask("nas.lab.test", DnsType.A).Answers.Single().Ttl, Is.EqualTo(300));
        Assert.That(new IPAddress(Ask("nas.lab.test", DnsType.Aaaa).Answers.Single().Data), Is.EqualTo(IPAddress.Parse("fd00::20")));
        Assert.That(Ask("lab.test", DnsType.Mx).Answers.Single().Data[..2], Is.EqualTo(new byte[] { 0, 10 }));
        Assert.That(Encoding.ASCII.GetString(Ask("lab.test", DnsType.Txt).Answers.Single().Data[1..]), Is.EqualTo("v=spf1 -all"));
    }

    [Test]
    public void FollowsAnAlias()
    {
        var answers = Ask("files.lab.test", DnsType.A).Answers;

        Assert.That(answers.Select(a => a.Type), Is.EqualTo(new[] { DnsType.Cname, DnsType.A }));
        Assert.That(answers[1].Data, Is.EqualTo(new byte[] { 192, 168, 1, 20 }));
    }

    [Test]
    public void AWildcardAnswersForNamesUnderIt()
    {
        Assert.That(Ask("api.dev.test", DnsType.A).Answers.Single().Data, Is.EqualTo(new byte[] { 10, 0, 0, 5 }));
        Assert.That(Ask("dev.test", DnsType.A).Rcode, Is.EqualTo(3));
    }

    [Test]
    public void AnswersReverseQuestionsFromTheAddresses()
    {
        var answer = Ask("1.1.168.192.in-addr.arpa", DnsType.Ptr).Answers.Single();

        Assert.That(answer.Type, Is.EqualTo(DnsType.Ptr));
        Assert.That(DnsStub.TryReadName(answer.Data, 0, out var name, out _) && name == "router.lab.test", Is.True);
    }

    [Test]
    public void UnknownNamesAreNxdomainAndKnownOnesWithoutTheTypeAreEmpty()
    {
        Assert.That(Ask("nothing.lab.test", DnsType.A).Rcode, Is.EqualTo(3));
        var (rcode, answers) = Ask("router.lab.test", DnsType.Aaaa);
        Assert.That(rcode, Is.Zero);
        Assert.That(answers, Is.Empty);
    }

    [Test]
    public void LeavesOutRowsThatCannotBeAnswered()
    {
        Assert.That(Records.Any(r => r.Name == "broken.lab.test"), Is.False);
    }

    [Test]
    public void AMissingTableFailsTheQuestion()
    {
        Assert.That(Parse(DnsStub.Answer(Query("router.lab.test", DnsType.A), null, 512, out _)!).Rcode, Is.EqualTo(2));
    }

    [Test]
    public void IgnoresResponsesAndRunts()
    {
        var response = Query("a.test", DnsType.A);
        response[2] |= 0x80;

        Assert.That(DnsStub.Answer(response, Records, 512, out _), Is.Null);
        Assert.That(DnsStub.Answer(new byte[5], Records, 512, out _), Is.Null);
    }
}

[NonParallelizable]
public class ServerRunnerTests
{
    private static readonly List<string> Log = [];

    private static X509Certificate2 Certificate() =>
        CertificateFactory.CreateSelfSigned(new NewCertificate("CN=localhost", AltNames: ["localhost", "127.0.0.1"], ServerAuth: true)).Open();

    private static ServerRunner Start(IServerService service, MemoryTables tables, X509Certificate2? cert, Dictionary<string, string>? settings, params ServerEndpoint[] endpoints)
    {
        var config = new ServerConfig { Name = service.DisplayName, Service = service.Name, Endpoints = [..endpoints], Settings = settings ?? new Dictionary<string, string>() };
        return ServerRunner.Start(config, service, tables, cert, line =>
                                                                {
                                                                    lock (Log) Log.Add(line);
                                                                });
    }

    private static int PortOf(ServerRunner runner, int index = 0) => runner.Bound[index].Bound.Port;

    [Test]
    public async Task DnsAnswersOverUdpAndTcp()
    {
        var tables = new MemoryTables();
        tables.Add("Hosts", ["Name", "Value"], ["printer.lab.test", "192.168.1.9"]);
        await using var runner = Start(new DnsStub(), tables, null, new Dictionary<string, string> { ["table"] = "Hosts" },
                                       new ServerEndpoint(PortProtocol.Udp, 0, address: "127.0.0.1"), new ServerEndpoint(PortProtocol.Tcp, 0, address: "127.0.0.1"));

        using var udp = new UdpClient();
        await udp.SendAsync(DnsStubTests.Query("printer.lab.test", DnsType.A), new IPEndPoint(IPAddress.Loopback, PortOf(runner)));
        var received = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(DnsStubTests.Parse(received.Buffer).Answers.Single().Data, Is.EqualTo(new byte[] { 192, 168, 1, 9 }));

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, PortOf(runner, 1));
        var stream = tcp.GetStream();
        var query  = DnsStubTests.Query("printer.lab.test", DnsType.A);
        await stream.WriteAsync(new byte[] { 0, (byte)query.Length }.Concat(query).ToArray());
        var length = new byte[2];
        await stream.ReadExactlyAsync(length);
        var reply = new byte[BinaryPrimitives.ReadUInt16BigEndian(length)];
        await stream.ReadExactlyAsync(reply);
        Assert.That(DnsStubTests.Parse(reply).Answers.Single().Data, Is.EqualTo(new byte[] { 192, 168, 1, 9 }));
    }

    /// <summary>A line-at-a-time SMTP client.</summary>
    private sealed class Client(Stream stream)
    {
        private readonly StreamReader _reader = new(stream, Encoding.UTF8, false, 1024, true);

        public async Task<string> Reply()
        {
            string reply = "", line;
            do
            {
                line  =  await _reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) ?? throw new EndOfStreamException();
                reply += line + "\n";
            } while (line.Length > 3 && line[3] == '-');

            return reply;
        }

        public async Task<string> Say(string command)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(command + "\r\n"));
            return await Reply();
        }
    }

    private static async Task SendMail(Client client, string subject)
    {
        Assert.That(await client.Say("MAIL FROM:<app@example.com> SIZE=100"), Does.StartWith("250"));
        Assert.That(await client.Say("RCPT TO:<anyone@anywhere.test>"), Does.StartWith("250"));
        Assert.That(await client.Say("RCPT TO:<second@else.test>"), Does.StartWith("250"));
        Assert.That(await client.Say("DATA"), Does.StartWith("354"));
        Assert.That(await client.Say($"Subject: {subject}\r\nFrom: app@example.com\r\n\r\nHello\r\n..dot line\r\n."), Does.StartWith("250"));
    }

    [Test]
    public async Task SmtpKeepsEveryMessageInTheTable()
    {
        var tables = new MemoryTables();
        await using var runner = Start(new SmtpStub(), tables, null, null, new ServerEndpoint(PortProtocol.Tcp, 0, address: "127.0.0.1"));

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, PortOf(runner));
        var client = new Client(tcp.GetStream());
        Assert.That(await client.Reply(), Does.StartWith("220"));
        var ehlo = await client.Say("EHLO tester");
        Assert.That(ehlo, Does.Contain("250 ").And.Contain("AUTH PLAIN LOGIN"));
        Assert.That(ehlo, Does.Not.Contain("STARTTLS"));
        Assert.That(await client.Say("AUTH PLAIN " + Convert.ToBase64String(Encoding.UTF8.GetBytes("\0bob\0secret"))), Does.StartWith("235"));
        Assert.That(await client.Say("RCPT TO:<x@y>"), Does.StartWith("503"));

        await SendMail(client, "=?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes("GrÃ¼ÃŸe")) + "?=");
        Assert.That(await client.Say("QUIT"), Does.StartWith("221"));

        var table = tables.Read("Mail")!;
        Assert.That(table.Columns, Is.EqualTo(SmtpStub.Columns));
        var row = table.Rows.Single();
        Assert.That(row[table.ColumnOf("From")], Is.EqualTo("app@example.com"));
        Assert.That(row[table.ColumnOf("To")], Is.EqualTo("anyone@anywhere.test, second@else.test"));
        Assert.That(row[table.ColumnOf("Subject")], Is.EqualTo("GrÃ¼ÃŸe"));
        Assert.That(row[table.ColumnOf("User")], Is.EqualTo("bob"));
        Assert.That(row[table.ColumnOf("Secure")], Is.EqualTo("no"));
        Assert.That(row[table.ColumnOf("Message")], Does.EndWith("\r\nHello\r\n.dot line\r\n"));
    }

    [Test]
    public async Task SmtpUpgradesWithStartTls()
    {
        var tables = new MemoryTables();
        await using var runner = Start(new SmtpStub(), tables, Certificate(), null, new ServerEndpoint(PortProtocol.Tcp, 0, ServerTls.StartTls, "127.0.0.1"));

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, PortOf(runner));
        var plain = new Client(tcp.GetStream());
        await plain.Reply();
        Assert.That(await plain.Say("EHLO tester"), Does.Contain("STARTTLS"));
        Assert.That(await plain.Say("STARTTLS"), Does.StartWith("220"));

        var ssl    = await TlsWrap.AsClientAsync(tcp.GetStream(), "localhost", verify: false);
        var secure = new Client(ssl);
        Assert.That(await secure.Say("EHLO tester"), Does.Not.Contain("STARTTLS"));
        await SendMail(secure, "over tls");

        Assert.That(tables.Read("Mail")!.Rows.Single()[SmtpStub.Columns.ToList().IndexOf("Secure")], Is.EqualTo("yes"));
    }

    [Test]
    public async Task TlsWrapsAnyTcpServiceInEitherDirection()
    {
        var cert   = Certificate();
        var tables = new MemoryTables();

        // an SMTP stub that only speaks TLS ...
        await using var smtps = Start(new SmtpStub(), tables, cert, null, new ServerEndpoint(PortProtocol.Tcp, 0, ServerTls.Implicit, "127.0.0.1"));

        // ... reached by a plain client through a relay that speaks TLS upstream (TLS as the client) ...
        await using var outward = Start(new TcpRelay(), tables, null,
                                        new Dictionary<string, string> { ["host"] = "localhost", ["port"] = PortOf(smtps).ToString(), ["tls"] = "true", ["verify"] = "false" },
                                        new ServerEndpoint(PortProtocol.Tcp, 0, address: "127.0.0.1"));

        // ... which is itself behind a relay that takes TLS in and passes plain text on (TLS as the server)
        await using var inward = Start(new TcpRelay(), tables, cert,
                                       new Dictionary<string, string> { ["host"] = "127.0.0.1", ["port"] = PortOf(outward).ToString() },
                                       new ServerEndpoint(PortProtocol.Tcp, 0, ServerTls.Implicit, "127.0.0.1"));

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, PortOf(inward));
        var client = new Client(await TlsWrap.AsClientAsync(tcp.GetStream(), "localhost", verify: false));
        Assert.That(await client.Reply(), Does.StartWith("220"));
        Assert.That(await client.Say("EHLO tester"), Does.StartWith("250"));
        await SendMail(client, "through two relays");

        Assert.That(tables.Read("Mail")!.Rows.Single()[SmtpStub.Columns.ToList().IndexOf("Secure")], Is.EqualTo("yes"));
    }

    [Test]
    public async Task AnIpv4ClientOfADualStackListenerIsSeenAsIpv4()
    {
        var tables = new MemoryTables();
        await using var runner = Start(new SmtpStub(), tables, null, null, new ServerEndpoint(PortProtocol.Tcp, 0));

        using var tcp = new TcpClient(AddressFamily.InterNetwork);
        await tcp.ConnectAsync(IPAddress.Loopback, PortOf(runner));
        var client = new Client(tcp.GetStream());
        await client.Reply();
        await client.Say("EHLO tester");
        await SendMail(client, "mapped");

        Assert.That(tables.Read("Mail")!.Rows.Single()[SmtpStub.Columns.ToList().IndexOf("Client")], Does.StartWith("127.0.0.1:"));
    }

    [Test]
    public void ImplicitTlsNeedsACertificate()
    {
        Assert.Throws<InvalidOperationException>(() => Start(new SmtpStub(), new MemoryTables(), null, null, new ServerEndpoint(PortProtocol.Tcp, 0, ServerTls.Implicit, "127.0.0.1")));
    }

    [Test]
    public void ATransportTheServiceDoesNotSpeakIsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => Start(new SmtpStub(), new MemoryTables(), null, null, new ServerEndpoint(PortProtocol.Udp, 0, address: "127.0.0.1")));
    }

    [Test]
    public async Task APortInUseFailsAndLeavesNothingOpen()
    {
        await using var first = Start(new DnsStub(), new MemoryTables(), null, null, new ServerEndpoint(PortProtocol.Tcp, 0, address: "127.0.0.1"));
        int port = PortOf(first);

        Assert.Throws<SocketException>(() => Start(new DnsStub(), new MemoryTables(), null, null,
                                                   new ServerEndpoint(PortProtocol.Udp, 0, address: "127.0.0.1"), new ServerEndpoint(PortProtocol.Tcp, port, address: "127.0.0.1")));
    }
}

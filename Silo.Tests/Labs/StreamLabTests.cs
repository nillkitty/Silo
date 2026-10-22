using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Newtonsoft.Json;
using Silo.Labs.Streams;

namespace Silo.Tests.Labs;

public class StreamLabTests
{
    private static AdapterSpec Adapter(string key, string arg = "", AdapterSide side = AdapterSide.Both)
    {
        return new AdapterSpec { Key = key, Arg = arg, Side = side };
    }

    private static async Task<byte[]> ReadAll(Stream source, params AdapterSpec[] adapters)
    {
        using var pipe = ReadPipeline.Build(source, adapters);
        var step = await pipe.ReadAsync(1 << 20, ReadMode.ToEnd);
        Assert.That(step.Error, Is.Null);
        return step.Output;
    }

    private static async Task<byte[]> WriteThrough(byte[] payload, params AdapterSpec[] adapters)
    {
        var target = new MemoryStream();
        var step   = await WritePipeline.WriteAsync(target, adapters, payload);
        Assert.That(step.Error, Is.Null);
        return target.ToArray();
    }

    private static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);

    // ---- what is typed ---------------------------------------------------------------------------

    [Test]
    public void A_source_is_read_from_what_is_typed()
    {
        Assert.That(StreamSource.TryParse("tcp://example.com:8080", out var tcp), Is.True);
        Assert.That(tcp!.Kind, Is.EqualTo(StreamKind.Tcp));
        Assert.That(tcp.Host, Is.EqualTo("example.com"));
        Assert.That(tcp.Port, Is.EqualTo(8080));

        Assert.That(StreamSource.TryParse("https://example.com", out var tls), Is.True);
        Assert.That(tls!.Kind, Is.EqualTo(StreamKind.Tls));
        Assert.That(tls.Port, Is.EqualTo(443));

        Assert.That(StreamSource.TryParse("http://example.com", out var http), Is.True);
        Assert.That(http!.Kind, Is.EqualTo(StreamKind.Tcp));
        Assert.That(http.Port, Is.EqualTo(80));

        Assert.That(StreamSource.TryParse("mem:hex:48 65 6C", out var mem), Is.True);
        Assert.That(mem!.Data, Is.EqualTo(new byte[] { 0x48, 0x65, 0x6C }));

        Assert.That(StreamSource.TryParse("mem:text:a\\nb", out var text), Is.True);
        Assert.That(text!.Data, Is.EqualTo(new byte[] { 0x61, 0x0A, 0x62 }));

        Assert.That(StreamSource.TryParse("\"/tmp/some file.bin\"", out var file), Is.True);
        Assert.That(file!.Kind, Is.EqualTo(StreamKind.File));
        Assert.That(file.Path, Is.EqualTo("/tmp/some file.bin"));

        Assert.That(StreamSource.TryParse("mem:hex:4", out _), Is.False);
        Assert.That(StreamSource.TryParse("gopher://x", out _), Is.False);
        Assert.That(StreamSource.TryParse("   ", out _), Is.False);
        Assert.That(StreamSource.LooksLikeSource("tcp://a:1"), Is.True);
        Assert.That(StreamSource.LooksLikeSource("/etc/hosts"), Is.False);
    }

    [Test]
    public void A_source_that_is_set_up_wrong_says_so()
    {
        Assert.That(new StreamSource { Kind = StreamKind.File }.Problem(), Does.Contain("Choose a file"));
        Assert.That(new StreamSource { Kind = StreamKind.File, Path = "x", Mode = FileMode.Append, Access = FileAccess.Read }.Problem(), Does.Contain("append"));
        Assert.That(new StreamSource { Kind = StreamKind.File, Path = "x", Mode = FileMode.Create, Access = FileAccess.Read }.Problem(), Does.Contain("writing"));
        Assert.That(new StreamSource { Kind = StreamKind.Tcp, Host = "", Port = 80 }.Problem(), Does.Contain("host"));
        Assert.That(new StreamSource { Kind = StreamKind.Tls, Host = "a", Port = 0 }.Problem(), Does.Contain("port"));
        Assert.That(StreamSource.ForTcp("a", 80).Problem(), Is.Null);
        Assert.That(StreamSource.ForMemory().Problem(), Is.Null);
    }

    [Test]
    public void Typed_bytes_are_read_as_text_hex_or_base64()
    {
        Assert.That(StreamPayload.Unescape(@"a\nb\tc\\d\x41é\q"), Is.EqualTo("a\nb\tc\\dAé\\q"));
        Assert.That(StreamPayload.ToBytes("48 65-6C,6c 0x6F", StreamPayload.Format.Hex), Is.EqualTo(Text("Hello")));
        Assert.That(StreamPayload.ToBytes("SGVsbG8=", StreamPayload.Format.Base64), Is.EqualTo(Text("Hello")));
        Assert.That(StreamPayload.ToBytes("é\\n", StreamPayload.Format.Text), Is.EqualTo(new byte[] { 0xC3, 0xA9, 0x0A }));

        var bad = Assert.Throws<FormatException>(() => StreamPayload.ToBytes("4G", StreamPayload.Format.Hex));
        Assert.That(bad.Message, Does.Contain("not hex"));
        Assert.Throws<FormatException>(() => StreamPayload.ToBytes("***", StreamPayload.Format.Base64));
    }

    [Test]
    public void Made_up_bytes_follow_the_pattern()
    {
        Assert.That(StreamPayload.Pattern("Zeros", 3), Is.EqualTo(new byte[] { 0, 0, 0 }));
        Assert.That(StreamPayload.Pattern("Ones (FF)", 2), Is.EqualTo(new byte[] { 255, 255 }));
        Assert.That(StreamPayload.Pattern("Counting", 258).Skip(254).ToArray(), Is.EqualTo(new byte[] { 254, 255, 0, 1 }));
        Assert.That(StreamPayload.Pattern("Letters", 28).Skip(24).ToArray(), Is.EqualTo(Text("yzab")));
        Assert.That(StreamPayload.Pattern("Random", 40).Length, Is.EqualTo(40));
    }

    [Test]
    public void A_hex_dump_lays_out_offset_hex_and_characters()
    {
        string dump = HexDump.Format(Text("Hello, world! 0123456789\n"), 0x10);

        string[] lines = dump.Split(Environment.NewLine);
        Assert.That(lines.Length, Is.EqualTo(2));
        Assert.That(lines[0], Is.EqualTo("00000010  48 65 6C 6C 6F 2C 20 77  6F 72 6C 64 21 20 30 31  |Hello, world! 01|"));
        Assert.That(lines[1], Does.StartWith("00000020  32 33 34 35 36 37 38 39  0A "));
        Assert.That(lines[1], Does.EndWith("|23456789.|"));
        Assert.That(HexDump.HexColumn(0), Is.EqualTo(0));
        Assert.That(HexDump.HexColumn(8), Is.EqualTo(25));
        Assert.That(HexDump.Hex(new byte[] { 1 }).Length, Is.EqualTo(HexDump.HexWidth()));
    }

    [Test]
    public void The_strip_keeps_what_went_by_up_to_its_limit()
    {
        var strip = new ByteStrip(10);

        strip.Add(Text("hello"));
        Assert.That(strip.Last, Is.EqualTo((0, 5)));
        strip.Add(Text("world!!"));
        Assert.That(strip.Length, Is.EqualTo(10));
        Assert.That(strip.Overflowed, Is.True);
        Assert.That(strip.Slice(3, 7), Is.EqualTo(Text("lowo")));
        Assert.That(strip.Slice(8, 100), Is.EqualTo(Text("ld")));
        strip.Trim(4);
        Assert.That(strip.Length, Is.EqualTo(6));
        strip.Clear();
        Assert.That(strip.Length, Is.EqualTo(0));
        Assert.That(strip.Overflowed, Is.False);
    }

    // ---- adapters --------------------------------------------------------------------------------

    [Test]
    public async Task Writing_through_adapters_changes_what_reaches_the_stream()
    {
        Assert.That(await WriteThrough(Text("Hello"), Adapter("base64")), Is.EqualTo(Text("SGVsbG8=")));
        Assert.That(await WriteThrough(Text("Hi"), Adapter("hex", "upper")), Is.EqualTo(Text("4869")));
        Assert.That(await WriteThrough(Text("Hi"), Adapter("upper"), Adapter("hex")), Is.EqualTo(Text("4849")));
        Assert.That(await WriteThrough(Text("hi"), Adapter("upper"), Adapter("hex", "lower")), Is.EqualTo(Text("4849")));
        Assert.That(await WriteThrough(Text("a\nb"), Adapter("crlf")), Is.EqualTo(Text("a\r\nb")));
        Assert.That(await WriteThrough(Text("a\r\nb\r"), Adapter("lf")), Is.EqualTo(Text("a\nb\r")));
    }

    [Test]
    public async Task Reading_through_adapters_changes_what_comes_out()
    {
        Assert.That(await ReadAll(new MemoryStream(Text("SGVsbG8=")), Adapter("unbase64")), Is.EqualTo(Text("Hello")));
        Assert.That(await ReadAll(new MemoryStream(Text("48 65 6c\n6C6f")), Adapter("unhex")), Is.EqualTo(Text("Hello")));
        Assert.That(await ReadAll(new MemoryStream(Text("Hello")), Adapter("base64"), Adapter("unbase64")), Is.EqualTo(Text("Hello")));
        Assert.That(await ReadAll(new MemoryStream(Text("abc")), Adapter("not"), Adapter("not")), Is.EqualTo(Text("abc")));
        Assert.That(await ReadAll(new MemoryStream(Text("abc")), Adapter("xor", "0x20")), Is.EqualTo(Text("ABC")));
    }

    [Test]
    public async Task Xor_goes_round_the_key_and_undoes_itself()
    {
        var data = StreamPayload.Pattern("Counting", 300);

        var once  = await ReadAll(new MemoryStream(data), Adapter("xor", "secret"));
        var twice = await ReadAll(new MemoryStream(once), Adapter("xor", "secret"));

        Assert.That(once, Is.Not.EqualTo(data));
        Assert.That(once[0], Is.EqualTo((byte)(0 ^ (byte)'s')));
        Assert.That(once[7], Is.EqualTo((byte)(7 ^ (byte)'e')));
        Assert.That(twice, Is.EqualTo(data));
    }

    [Test]
    public async Task Compression_works_in_both_directions()
    {
        var data = Text(string.Concat(Enumerable.Repeat("the quick brown fox ", 200)));

        foreach (string pair in new[] { "gzip:gunzip", "deflate:inflate", "zlib:unzlib", "brotli:unbrotli" })
        {
            string[] names = pair.Split(':');
            var packed = await WriteThrough(data, Adapter(names[0]));
            Assert.That(packed.Length, Is.LessThan(data.Length / 4), names[0]);
            Assert.That(await ReadAll(new MemoryStream(packed), Adapter(names[1])), Is.EqualTo(data), names[1]);

            // the same adapter on a read compresses what the stream gives
            var packedOnRead = await ReadAll(new MemoryStream(data), Adapter(names[0]));
            Assert.That(await ReadAll(new MemoryStream(packedOnRead), Adapter(names[1])), Is.EqualTo(data), names[0] + " on a read");
        }

        Assert.That(await ReadAll(new MemoryStream(data), Adapter("gzip"), Adapter("gunzip")), Is.EqualTo(data));
    }

    [Test]
    public async Task A_stream_can_be_read_a_byte_at_a_time_through_a_decompressor()
    {
        var data   = Text("Hello, Silo! " + string.Concat(Enumerable.Repeat("again ", 50)));
        var packed = await WriteThrough(data, Adapter("gzip"));

        using var pipe = ReadPipeline.Build(new MemoryStream(packed), [Adapter("gunzip")]);
        var all = new List<byte>();
        while (!pipe.Ended)
        {
            var step = await pipe.ReadAsync(1, ReadMode.Once);
            Assert.That(step.Error, Is.Null);
            Assert.That(step.Output.Length, Is.AtMost(1));
            all.AddRange(step.Output);
        }

        Assert.That(all.ToArray(), Is.EqualTo(data));
        Assert.That(pipe.InputTotal, Is.EqualTo((long)packed.Length));
        Assert.That(pipe.OutputTotal, Is.EqualTo((long)data.Length));
    }

    [Test]
    public async Task Adapters_cope_with_a_stream_that_gives_a_byte_at_a_time()
    {
        var b64 = Text("SGVsbG8sIHdvcmxkIQ==");
        Assert.That(await ReadAll(new MemoryStream(b64), Adapter("trickle", "1"), Adapter("unbase64")), Is.EqualTo(Text("Hello, world!")));

        var packed = await WriteThrough(Text("trickled"), Adapter("gzip"));
        Assert.That(await ReadAll(new MemoryStream(packed), Adapter("trickle", "1"), Adapter("gunzip")), Is.EqualTo(Text("trickled")));

        var target = new WriteCounter();
        var step   = await WritePipeline.WriteAsync(target, [Adapter("trickle", "2")], Text("hello"));
        Assert.That(step.Error, Is.Null);
        Assert.That(target.Sizes, Is.EqualTo(new[] { 2, 2, 1 }));
    }

    [Test]
    public async Task Text_is_changed_between_encodings_without_breaking_characters()
    {
        string text = "héllo € 😀";

        var utf16 = await WriteThrough(Encoding.UTF8.GetBytes(text), Adapter("transcode", "utf-8>utf-16"));
        Assert.That(utf16, Is.EqualTo(Encoding.Unicode.GetBytes(text)));

        // a byte at a time: the pieces end in the middle of characters
        var target = new MemoryStream();
        var spec   = new[] { Adapter("transcode", "utf-8>utf-16"), Adapter("trickle", "1") };
        await WritePipeline.WriteAsync(target, spec, Encoding.UTF8.GetBytes(text));
        Assert.That(target.ToArray(), Is.EqualTo(Encoding.Unicode.GetBytes(text)));

        var back = await ReadAll(new MemoryStream(utf16), Adapter("trickle", "1"), Adapter("transcode", "utf-16>utf-8"));
        Assert.That(Encoding.UTF8.GetString(back), Is.EqualTo(text));
    }

    [Test]
    public async Task Skip_and_take_cut_the_data_and_take_stops_asking_the_stream()
    {
        using var pipe = ReadPipeline.Build(new MemoryStream(Text("abcdefghij")), [Adapter("skip", "2"), Adapter("take", "3")]);

        var step = await pipe.ReadAsync(3, ReadMode.Exactly);
        Assert.That(Encoding.UTF8.GetString(step.Output), Is.EqualTo("cde"));
        Assert.That(pipe.InputTotal, Is.EqualTo(5L));

        var more = await pipe.ReadAsync(3, ReadMode.Once);
        Assert.That(more.Output, Is.Empty);
        Assert.That(more.Ended, Is.True);
        Assert.That(pipe.InputTotal, Is.EqualTo(5L));
    }

    [Test]
    public async Task The_meter_and_the_digest_report_on_what_went_through()
    {
        using var pipe = ReadPipeline.Build(new MemoryStream(Text("abc")), [Adapter("hash", "sha256"), Adapter("meter")]);
        await pipe.ReadAsync(3, ReadMode.ToEnd);

        var reports = pipe.Reports();
        Assert.That(reports.Count, Is.EqualTo(2));
        Assert.That(reports[0].Text, Does.Contain("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
        Assert.That(reports[0].Text, Does.Contain("SHA256 of 3 byte(s)"));
        Assert.That(reports[1].Text, Does.Contain("3 byte(s) in 1 piece(s)"));

        var write = await WritePipeline.WriteAsync(new MemoryStream(), [Adapter("meter"), Adapter("trickle", "1", AdapterSide.Write), Adapter("meter")], Text("abcd"));
        Assert.That(write.Reports[0].Text, Does.Contain("4 byte(s) in 1 piece(s)"));
        Assert.That(write.Reports[2].Text, Does.Contain("4 byte(s) in 4 piece(s)"));
    }

    [Test]
    public async Task Encryption_comes_back_with_the_same_key_and_not_with_another()
    {
        string key  = "000102030405060708090A0B0C0D0E0F";
        var    data = Text("a secret that is longer than one block of sixteen bytes");

        var sealedBytes = await WriteThrough(data, Adapter("aes", key));
        Assert.That(sealedBytes.Length % 16, Is.EqualTo(0));
        Assert.That(sealedBytes, Is.Not.EqualTo(data));
        Assert.That(await ReadAll(new MemoryStream(sealedBytes), Adapter("unaes", key)), Is.EqualTo(data));

        using var wrong = ReadPipeline.Build(new MemoryStream(sealedBytes), [Adapter("unaes", "0F0E0D0C0B0A09080706050403020100")]);
        var step = await wrong.ReadAsync(100, ReadMode.ToEnd);
        Assert.That(step.Error, Is.Not.Null);

        Assert.That(StreamAdapters.Find("aes")!.Check("0011"), Does.Contain("32, 48 or 64"));
        Assert.That(StreamAdapters.Find("aes")!.Check(key + ":00"), Does.Contain("IV"));
        Assert.That(StreamAdapters.Find("aes")!.Check(key + ":" + key), Is.Null);
    }

    [Test]
    public void An_argument_that_is_not_acceptable_is_said_so()
    {
        Assert.That(StreamAdapters.Find("xor")!.Check(""), Does.Contain("key"));
        Assert.That(StreamAdapters.Find("gzip")!.Check("tiny"), Does.Contain("level"));
        Assert.That(StreamAdapters.Find("skip")!.Check("-1"), Does.Contain("number"));
        Assert.That(StreamAdapters.Find("transcode")!.Check("utf-8"), Does.Contain("from>to"));
        Assert.That(StreamAdapters.Find("transcode")!.Check("utf-8>nope"), Does.Contain("no such encoding"));
        Assert.That(StreamAdapters.Find("hash")!.Check("crc"), Does.Contain("sha256"));
        Assert.That(StreamAdapters.Find("gzip")!.Check("fastest"), Is.Null);
        Assert.That(StreamAdapters.Find("GZIP"), Is.Not.Null);
        Assert.That(StreamAdapters.Find("nope"), Is.Null);

        var def = StreamAdapters.Find("gunzip")!;
        Assert.That(def.CanRead, Is.True);
        Assert.That(def.CanWrite, Is.False);
        Assert.That(StreamAdapters.Find("gzip")!.CanWrite, Is.True);
        Assert.Throws<AdapterException>(() => ReadPipeline.Build(new MemoryStream(), [Adapter("nope")]));
        Assert.Throws<AdapterException>(() => ReadPipeline.Build(new MemoryStream(), [Adapter("xor", "")]));
        Assert.That(StreamAdapters.All.Select(a => a.Key).Distinct().Count(), Is.EqualTo(StreamAdapters.All.Count));
    }

    [Test]
    public async Task Adapters_left_off_or_on_one_side_are_left_out_of_the_other()
    {
        Assert.That(await WriteThrough(Text("a"), Adapter("upper", "", AdapterSide.Read)), Is.EqualTo(Text("a")));
        Assert.That(await WriteThrough(Text("a"), Adapter("upper", "", AdapterSide.Write)), Is.EqualTo(Text("A")));
        Assert.That(await ReadAll(new MemoryStream(Text("a")), Adapter("upper", "", AdapterSide.Write)), Is.EqualTo(Text("a")));
        Assert.That(await ReadAll(new MemoryStream(Text("a")), new AdapterSpec { Key = "upper", Enabled = false }), Is.EqualTo(Text("a")));
        // a decompressor has no write side, so it is skipped there rather than failing
        Assert.That(await WriteThrough(Text("a"), Adapter("gunzip")), Is.EqualTo(Text("a")));
    }

    // ---- reading and writing ---------------------------------------------------------------------

    [Test]
    public async Task A_read_can_ask_once_exactly_or_to_the_end()
    {
        var data = StreamPayload.Pattern("Letters", 100);

        using (var once = ReadPipeline.Build(new MemoryStream(data), [Adapter("trickle", "7")]))
        {
            var step = await once.ReadAsync(30, ReadMode.Once);
            Assert.That(step.Output.Length, Is.EqualTo(7));
            Assert.That(step.Calls, Is.EqualTo(1));
        }

        using (var exactly = ReadPipeline.Build(new MemoryStream(data), [Adapter("trickle", "7")]))
        {
            var step = await exactly.ReadAsync(30, ReadMode.Exactly);
            Assert.That(step.Output.Length, Is.EqualTo(30));
            Assert.That(step.Calls, Is.EqualTo(5));
            Assert.That(step.Input, Is.EqualTo(step.Output));
        }

        using (var end = ReadPipeline.Build(new MemoryStream(data)))
        {
            var step = await end.ReadAsync(1000, ReadMode.ToEnd);
            Assert.That(step.Output, Is.EqualTo(data));
            Assert.That(step.Ended, Is.True);
            Assert.That((await end.ReadAsync(10, ReadMode.Exactly)).Output, Is.Empty);
        }

        using (var limited = ReadPipeline.Build(new MemoryStream(data)))
        {
            var step = await limited.ReadAsync(40, ReadMode.ToEnd);
            Assert.That(step.Output.Length, Is.EqualTo(40));
            Assert.That(step.Ended, Is.False);
        }
    }

    [Test]
    public async Task A_read_that_goes_wrong_says_so_and_keeps_what_came_before()
    {
        using var pipe = ReadPipeline.Build(new MemoryStream(Text("this is not gzip data at all")), [Adapter("gunzip")]);

        var step = await pipe.ReadAsync(10, ReadMode.Exactly);

        Assert.That(step.Error, Is.Not.Null);
        Assert.That(step.Output, Is.Empty);

        using var hex = ReadPipeline.Build(new MemoryStream(Text("48 6x")), [Adapter("unhex")]);
        var bad = await hex.ReadAsync(10, ReadMode.ToEnd);
        Assert.That(bad.Error, Does.Contain("not a hex digit"));
        Assert.That(bad.Output, Is.EqualTo(Text("H")));
    }

    [Test]
    public async Task A_read_can_be_stopped()
    {
        using var cts = new CancellationTokenSource();
        var never = new NeverStream();
        using var pipe = ReadPipeline.Build(never, []);

        var reading = pipe.ReadAsync(10, ReadMode.Exactly, cts.Token);
        cts.CancelAfter(50);
        var step = await reading;

        Assert.That(step.Error, Is.EqualTo("Stopped."));
    }

    // ---- opening streams -------------------------------------------------------------------------

    [Test]
    public async Task A_file_is_opened_and_can_be_moved_around_in()
    {
        string path = Path.Combine(Path.GetTempPath(), "silo-streamlab-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, StreamPayload.Pattern("Counting", 100));
        try
        {
            await using var reel = await Reel.OpenAsync(StreamSource.ForFile(path, FileAccess.ReadWrite), TimeSpan.FromSeconds(5));
            Assert.That(reel.Stream.CanSeek, Is.True);
            Assert.That(reel.Stream.Length, Is.EqualTo(100L));

            using (var pipe = ReadPipeline.Build(reel.Stream, []))
            {
                Assert.That((await pipe.ReadAsync(4, ReadMode.Exactly)).Output, Is.EqualTo(new byte[] { 0, 1, 2, 3 }));
                Assert.That(reel.Stream.Position, Is.EqualTo(4L));
            }

            reel.Stream.Position = 50;
            using (var pipe = ReadPipeline.Build(reel.Stream, []))
            {
                Assert.That((await pipe.ReadAsync(2, ReadMode.Exactly)).Output, Is.EqualTo(new byte[] { 50, 51 }));
            }

            reel.Stream.Position = 10;
            var written = await WritePipeline.WriteAsync(reel.Stream, [Adapter("hex", "upper")], new byte[] { 0xAB });
            Assert.That(written.OnStream, Is.EqualTo(Text("AB")));
            Assert.That(reel.Stream.Length, Is.EqualTo(100L));

            var facts = reel.Facts();
            Assert.That(facts.First(f => f.Name == "CanSeek").Value, Is.EqualTo("yes"));
            Assert.That(facts.First(f => f.Name == "Length").Value, Is.EqualTo("100"));
            Assert.That(facts.First(f => f.Name == "Position").Value, Is.EqualTo("12"));
            Assert.That(facts.Any(f => f.Group == "File" && f.Name == "Name"), Is.True);
            Assert.That(facts.First(f => f.Name == "Type").Value, Is.EqualTo("System.IO.FileStream"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task A_file_that_is_not_there_is_an_error_to_show()
    {
        try
        {
            await Reel.OpenAsync(StreamSource.ForFile("/nonexistent/silo/none.bin"), TimeSpan.FromSeconds(1));
            Assert.That(false, Is.True, "it opened");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.That(ex.Message, Is.Not.Empty);
        }

        try
        {
            await Reel.OpenAsync(new StreamSource { Kind = StreamKind.Tcp }, TimeSpan.FromSeconds(1));
            Assert.That(false, Is.True, "it opened");
        }
        catch (InvalidOperationException ex)
        {
            Assert.That(ex.Message, Does.Contain("host"));
        }
    }

    [Test]
    public async Task A_memory_reel_starts_with_its_data_and_can_be_written()
    {
        await using var reel = await Reel.OpenAsync(StreamSource.ForMemory(Text("hello")), TimeSpan.FromSeconds(1));

        Assert.That(reel.Stream.Length, Is.EqualTo(5L));
        Assert.That(reel.Stream.Position, Is.EqualTo(0L));
        reel.Stream.Position = reel.Stream.Length;
        await WritePipeline.WriteAsync(reel.Stream, [], Text(", world"));
        reel.Stream.Position = 0;
        Assert.That(await ReadAll(reel.Stream), Is.EqualTo(Text("hello, world")));
        Assert.That(reel.TrySetTimeout(true, 100), Is.False);
        Assert.That(reel.Facts().First(f => f.Name == "ReadTimeout" || f.Name == "Length").Name, Is.EqualTo("Length"));
    }

    [Test]
    public async Task A_stream_somebody_else_has_is_left_open()
    {
        var mine = new MemoryStream(Text("abc"));
        await using (var reel = await Reel.OpenAsync(StreamSource.ForStream(mine), TimeSpan.FromSeconds(1)))
        {
            Assert.That(await ReadAll(reel.Stream), Is.EqualTo(Text("abc")));
        }

        Assert.That(mine.CanRead, Is.True);
    }

    [Test]
    public async Task A_connection_is_opened_read_and_written_with_its_facts()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
                              {
                                  using var client = await listener.AcceptTcpClientAsync();
                                  var stream = client.GetStream();
                                  await stream.WriteAsync(Text("hello from the server"));
                                  var buffer = new byte[64];
                                  int n      = await stream.ReadAsync(buffer);
                                  await stream.WriteAsync(Text("you said: " + Encoding.UTF8.GetString(buffer, 0, n)));
                                  client.Client.Shutdown(SocketShutdown.Send);
                                  await stream.ReadAsync(buffer);
                              });
        try
        {
            await using var reel = await Reel.OpenAsync(StreamSource.ForTcp("127.0.0.1", port), TimeSpan.FromSeconds(5));
            using var pipe = ReadPipeline.Build(reel.Stream, []);

            var first = await pipe.ReadAsync(5, ReadMode.Exactly);
            Assert.That(Encoding.UTF8.GetString(first.Output), Is.EqualTo("hello"));

            var facts = reel.Facts();
            Assert.That(facts.First(f => f.Name == "CanSeek").Value, Is.EqualTo("no"));
            Assert.That(facts.First(f => f.Name == "Position").Value, Does.Contain("not known"));
            Assert.That(facts.First(f => f.Name == "Remote").Value, Does.Contain("port " + port));
            Assert.That(reel.TrySetTimeout(true, 2000), Is.True);
            Assert.That(reel.Facts().First(f => f.Name == "ReadTimeout").Value, Is.EqualTo("2000 ms"));

            await WritePipeline.WriteAsync(reel.Stream, [Adapter("upper")], Text("hi"));
            var rest = await pipe.ReadAsync(1000, ReadMode.ToEnd);
            Assert.That(Encoding.UTF8.GetString(rest.Output), Is.EqualTo(" from the serveryou said: HI"));
            Assert.That(rest.Ended, Is.True);
            Assert.That(reel.TryShutdownSend(), Is.True);
        }
        finally
        {
            listener.Stop();
        }

        await server;
    }

    [Test]
    public async Task A_connection_that_is_refused_is_an_error_to_show()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        try
        {
            await Reel.OpenAsync(StreamSource.ForTcp("127.0.0.1", port), TimeSpan.FromSeconds(5));
            Assert.That(false, Is.True, "it connected");
        }
        catch (SocketException ex)
        {
            Assert.That(ex.Message, Is.Not.Empty);
        }
    }

    [Test]
    public async Task A_tls_connection_checks_the_certificate_unless_told_not_to()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=silo-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var cert = new X509Certificate2(made.Export(X509ContentType.Pfx));

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
                              {
                                  for (int i = 0; i < 2; i++)
                                  {
                                      using var client = await listener.AcceptTcpClientAsync();
                                      using var ssl    = new SslStream(client.GetStream());
                                      try
                                      {
                                          await ssl.AuthenticateAsServerAsync(cert);
                                          await ssl.WriteAsync(Text("secure hello"));
                                          await ssl.FlushAsync();
                                          var buffer = new byte[16];
                                          await ssl.ReadAsync(buffer);
                                      }
                                      catch (Exception ex) when (ex is AuthenticationException or IOException)
                                      {
                                          // the client did not like the certificate
                                      }
                                  }
                              });
        try
        {
            try
            {
                await Reel.OpenAsync(StreamSource.ForTcp("127.0.0.1", port, true), TimeSpan.FromSeconds(10));
                Assert.That(false, Is.True, "an untrusted certificate was accepted");
            }
            catch (AuthenticationException ex)
            {
                Assert.That(ex.Message, Is.Not.Empty);
            }

            var trusting = StreamSource.ForTcp("127.0.0.1", port, true);
            trusting.TrustAnyCertificate = true;
            await using var reel = await Reel.OpenAsync(trusting, TimeSpan.FromSeconds(10));

            Assert.That(reel.CertificateErrors, Is.Not.EqualTo(SslPolicyErrors.None));
            using var pipe = ReadPipeline.Build(reel.Stream, []);
            var step = await pipe.ReadAsync(12, ReadMode.Exactly);
            Assert.That(Encoding.UTF8.GetString(step.Output), Is.EqualTo("secure hello"));

            var facts = reel.Facts();
            Assert.That(facts.First(f => f.Name == "Encrypted").Value, Is.EqualTo("yes"));
            Assert.That(facts.First(f => f.Name == "Protocol").Value, Does.StartWith("Tls"));
            Assert.That(facts.First(f => f.Group == "Certificate" && f.Name == "Subject").Value, Does.Contain("silo-test"));
            Assert.That(facts.First(f => f.Name == "Certificate problems").Value, Does.Contain("accepted anyway"));
            Assert.That(facts.First(f => f.Name == "Cipher suite").Value, Is.Not.Empty);
        }
        finally
        {
            listener.Stop();
            try
            {
                await server;
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException or InvalidOperationException)
            {
                // the listener stopped while the server was waiting for a second client
            }
        }
    }

    [Test]
    public void Post_processing_applies_adapters_to_recorded_bytes_and_reports_a_failure_with_what_came_before()
    {
        var (upper, problem) = StreamPost.Apply("hello"u8, [Adapter("upper")]);
        Assert.That(problem, Is.Null);
        Assert.That(System.Text.Encoding.ASCII.GetString(upper), Is.EqualTo("HELLO"));

        // several adapters go in order; one that is off or only for writes is left out
        var (chain, _) = StreamPost.Apply("hi"u8, [Adapter("hex"), new AdapterSpec { Key = "upper", Enabled = false }, Adapter("xor", "0xFF", AdapterSide.Write)]);
        Assert.That(System.Text.Encoding.ASCII.GetString(chain), Is.EqualTo("6869"));

        // nothing to apply: the same bytes
        var (same, none) = StreamPost.Apply(new byte[] { 1, 2, 3 }, []);
        Assert.That(same, Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(none, Is.Null);

        // text that is not hex: what came before the failure is kept
        var (partial, failure) = StreamPost.Apply("4849zz"u8, [Adapter("unhex")]);
        Assert.That(failure, Is.Not.Null);
        Assert.That(partial, Is.EqualTo(new byte[] { 0x48, 0x49 }));
    }

    [Test]
    public void A_strip_changes_its_version_whenever_it_changes()
    {
        var strip = new ByteStrip(100);
        long v0   = strip.Version;
        strip.Add([1, 2, 3]);
        long v1 = strip.Version;
        strip.Trim(1);
        long v2 = strip.Version;
        strip.Clear();
        Assert.That(v1, Is.GreaterThan(v0));
        Assert.That(v2, Is.GreaterThan(v1));
        Assert.That(strip.Version, Is.GreaterThan(v2));
    }

    [Test]
    public void A_setup_copy_has_its_own_post_processing()
    {
        var setup = new StreamSetup { Post = [Adapter("upper")] };
        var copy  = setup.Clone();
        copy.Post[0].Key = "lower";
        Assert.That(setup.Post[0].Key, Is.EqualTo("upper"));
        var back = JsonConvert.DeserializeObject<StreamSetup>(JsonConvert.SerializeObject(setup))!;
        Assert.That(back.Post.Count, Is.EqualTo(1));
    }

    [Test]
    public void A_setup_is_kept_and_copied_with_all_its_parts()
    {
        var setup = new StreamSetup { Name = "Zipped", Step = 8, Source = StreamSource.ForMemory([1, 2, 3]), Adapters = [Adapter("gunzip"), Adapter("xor", "0xFF", AdapterSide.Write)] };

        var back = JsonConvert.DeserializeObject<StreamSetup>(JsonConvert.SerializeObject(setup))!;

        Assert.That(back.Name, Is.EqualTo("Zipped"));
        Assert.That(back.Source.Data, Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(back.Adapters.Count, Is.EqualTo(2));
        Assert.That(back.Adapters[1].Side, Is.EqualTo(AdapterSide.Write));

        var copy = setup.Clone();
        copy.Adapters[0].Arg = "changed";
        copy.Source.Data[0] = 9;
        Assert.That(setup.Adapters[0].Arg, Is.EqualTo(string.Empty));
        Assert.That(setup.Source.Data[0], Is.EqualTo((byte)1));
        Assert.That(setup.Adapters[1].ToString(), Is.EqualTo("xor 0xFF (writes)"));
    }

    private sealed class WriteCounter : Stream
    {
        public List<int> Sizes { get; } = [];

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Sizes.Add(count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Sizes.Add(buffer.Length);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A stream that has nothing to give for as long as it is asked (a connection to a server that says nothing).</summary>
    private sealed class NeverStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

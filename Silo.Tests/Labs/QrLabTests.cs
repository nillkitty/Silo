using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Silo.Labs.Qr;

namespace Silo.Tests.Labs;

public class QrLabTests
{
    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static Dictionary<string, string> Fields(params (string, string)[] pairs) => pairs.ToDictionary(p => p.Item1, p => p.Item2);

    private static QrPayloadGuess RoundTrip(QrPayloadKind kind, Dictionary<string, string> fields)
    {
        var made = QrPayloads.Build(kind, fields);
        Assert.That(made.IsOk, Is.True, made.Problem);
        return QrPayloads.Detect(made.Text!);
    }

    // ---- the encoder ------------------------------------------------------------------------

    // matrices made by python-qrcode with the same version, level and mask, hashed (SHA-256 of rows of 1 and 0, a line feed after each)
    [TestCase("HELLO WORLD", QrEcc.Medium, 1, 3, "855825bc565b4ed30ecfb492a46d77cf5bcb00d7b2469571dfccdc73c227236f")]
    [TestCase("01234567", QrEcc.Medium, 1, 2, "1fd7121c43b3846a901e80806d6421d39482c61b0daf77fbbdd59d6bf87f4c50")]
    [TestCase("https://example.com/qr", QrEcc.Quartile, 3, 5, "5a87c20fb1de70d94f0af87c1548a9b8a325f1f48e4b0bbeb7be6e34f78ade1c")]
    [TestCase("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA1234567890", QrEcc.High, 7, 0, "882d510b5256b23b0c744d0f8601d6e7a962af697c6d7b005d029ac5fa2c3a8a")]
    [TestCase("The quick brown fox jumps over the lazy dog", QrEcc.Low, 4, 6, "ae191d92492a23e98aae5eba21532a11819ada59ecbe604eaad6ed4e64ab4d47")]
    public void The_code_is_module_for_module_what_another_library_makes(string text, QrEcc ecc, int version, int mask, string hash)
    {
        var code = QrCode.EncodeText(text, ecc, null, false, version, version, mask, false);

        Assert.That(code.Version, Is.EqualTo(version));
        Assert.That(Sha(QrRender.Matrix(code)), Is.EqualTo(hash));
    }

    [Test]
    public void The_capacity_table_gives_the_known_limits()
    {
        var v1  = QrBuilder.Capacity(1).ToList();
        var v40 = QrBuilder.Capacity(40).ToList();

        Assert.That(v1[0], Is.EqualTo((QrEcc.Low, 41, 25, 17)));
        Assert.That(v1[3], Is.EqualTo((QrEcc.High, 17, 10, 7)));
        Assert.That(v40[0], Is.EqualTo((QrEcc.Low, 7089, 4296, 2953)));
        Assert.That(v40[3], Is.EqualTo((QrEcc.High, 3057, 1852, 1273)));
    }

    [Test]
    public void The_smallest_version_that_holds_the_text_is_chosen_and_too_much_text_is_refused()
    {
        Assert.That(QrCode.EncodeText(new string('a', 17), QrEcc.Low, boost: false).Version, Is.EqualTo(1));
        Assert.That(QrCode.EncodeText(new string('a', 18), QrEcc.Low, boost: false).Version, Is.EqualTo(2));
        Assert.That(QrCode.EncodeText(new string('7', 7089), QrEcc.Low, boost: false).Version, Is.EqualTo(40));
        Assert.Throws<QrCapacityException>(() => QrCode.EncodeText(new string('7', 7090), QrEcc.Low, boost: false));
        Assert.Throws<QrCapacityException>(() => QrCode.EncodeText(new string('a', 40), QrEcc.Low, minVersion: 1, maxVersion: 2));
    }

    [Test]
    public void The_level_of_correction_is_raised_when_there_is_room_and_only_then()
    {
        // 5 bytes at level Low fit in version 1, and so would the highest level
        Assert.That(QrCode.EncodeText("hello", QrEcc.Low, boost: true).Ecc, Is.EqualTo(QrEcc.High));
        Assert.That(QrCode.EncodeText("hello", QrEcc.Low, boost: false).Ecc, Is.EqualTo(QrEcc.Low));

        // 17 bytes fill version 1 at Low, so there is no room for more
        Assert.That(QrCode.EncodeText(new string('a', 17), QrEcc.Low, boost: true).Ecc, Is.EqualTo(QrEcc.Low));
    }

    [Test]
    public void The_format_and_version_bits_are_the_known_ones()
    {
        // from the standard's own examples: level M with mask 5, and version 7
        Assert.That(QrCode.FormatBits(QrEcc.Medium, 5), Is.EqualTo(0b100000011001110));
        Assert.That(QrCode.VersionBits(7), Is.EqualTo(0b000111110010010100));
        Assert.That(QrCode.VersionBits(40), Is.EqualTo(0b101000110001101001));
    }

    [Test]
    public void A_mask_that_is_asked_for_is_used_and_the_penalties_name_the_best()
    {
        var chosen = QrCode.EncodeText("penalty", QrEcc.Medium, boost: false);
        Assert.That(chosen.Penalties.Count, Is.EqualTo(8));
        Assert.That(chosen.Penalties[chosen.Mask], Is.EqualTo(chosen.Penalties.Min()));

        Assert.That(QrCode.EncodeText("penalty", QrEcc.Medium, mask: 4, boost: false).Mask, Is.EqualTo(4));
    }

    // ---- reading ----------------------------------------------------------------------------

    [TestCase(QrEcc.Low)]
    [TestCase(QrEcc.Medium)]
    [TestCase(QrEcc.Quartile)]
    [TestCase(QrEcc.High)]
    public void Every_kind_of_text_reads_back_at_every_level(QrEcc ecc)
    {
        foreach (var text in new[] { "0123456789", "HELLO WORLD $%*+-./:", "hello, wörld ✓", new string('x', 300), "1" })
        {
            var code   = QrCode.EncodeText(text, ecc);
            var result = QrDecoder.Decode(code.ToMatrix());

            Assert.That(result.IsOk, Is.True, result.Error);
            Assert.That(result.Value!.Text, Is.EqualTo(text));
            Assert.That(result.Value.Version, Is.EqualTo(code.Version));
            Assert.That(result.Value.Ecc, Is.EqualTo(code.Ecc));
            Assert.That(result.Value.Mask, Is.EqualTo(code.Mask));
            Assert.That(result.Value.Corrected, Is.EqualTo(0));
        }
    }

    [Test]
    public void Big_codes_read_back_too()
    {
        var random = new Random(11);
        foreach (int version in new[] { 7, 14, 27, 33, 40 })
        {
            var text   = new string(Enumerable.Range(0, version * 6).Select(_ => (char)('a' + random.Next(26))).ToArray());
            var code   = QrCode.EncodeText(text, QrEcc.Quartile, null, false, version, version, -1, false);
            var result = QrDecoder.Decode(code.ToMatrix());

            Assert.That(result.IsOk, Is.True, $"version {version}: {result.Error}");
            Assert.That(result.Value!.Text, Is.EqualTo(text));
        }
    }

    [Test]
    public void The_segments_and_the_utf8_marker_are_reported()
    {
        var plain = QrDecoder.Decode(QrCode.EncodeText("hello world", QrEcc.Low, boost: false).ToMatrix()).Value!;
        Assert.That(plain.Segments.Select(s => s.Mode), Is.EqualTo(new[] { QrMode.Byte }));

        var marked = QrDecoder.Decode(QrCode.EncodeText("héllo", QrEcc.Low, QrMode.Byte, true).ToMatrix()).Value!;
        Assert.That(marked.Text, Is.EqualTo("héllo"));

        var numeric = QrDecoder.Decode(QrCode.EncodeText("12345", QrEcc.Low).ToMatrix()).Value!;
        Assert.That(numeric.Segments.Single().Mode, Is.EqualTo(QrMode.Numeric));
        Assert.That(numeric.Segments.Single().Characters, Is.EqualTo(5));
    }

    [Test]
    public void Damage_within_what_each_block_can_put_right_is_put_right()
    {
        var random = new Random(3);
        int tried  = 0;
        foreach (var ecc in new[] { QrEcc.Low, QrEcc.Medium, QrEcc.Quartile, QrEcc.High })
        foreach (int version in new[] { 1, 5, 12, 25, 40 })
        {
            var text   = new string(Enumerable.Range(0, version * 3).Select(_ => (char)('a' + random.Next(26))).ToArray());
            var code   = QrCode.EncodeText(text, ecc, null, false, version, version, -1, false);
            var blocks = QrDamage.BlockOfCodewords(code.Version, code.Ecc);
            int limit  = QrCode.EccCodewordsPerBlock(code.Version, code.Ecc) / 2;

            for (int attempt = 0; attempt < 6; attempt++)
            {
                var (modules, hit) = QrDamage.Damage(code, random.Next(1, 6), random);
                if (hit.GroupBy(h => blocks[h]).Any(g => g.Count() > limit)) continue;

                var result = QrDecoder.Decode(modules);
                Assert.That(result.IsOk, Is.True, $"v{version} {ecc}: {result.Error}");
                Assert.That(result.Value!.Text, Is.EqualTo(text));
                Assert.That(result.Value.Corrected, Is.EqualTo(hit.Count));
                tried++;
            }
        }

        Assert.That(tried, Is.GreaterThan(60));
    }

    [Test]
    public void Damage_beyond_what_can_be_put_right_is_reported_and_never_throws()
    {
        var code   = QrCode.EncodeText("spoiled beyond help", QrEcc.Low, null, false, 2, 2, -1, false);
        var random = new Random(9);
        var (modules, _) = QrDamage.Damage(code, 30, random);

        var result = QrDecoder.Decode(modules);

        // either it says it could not read the code, or (rarely) a wrong text that the checks happened to accept; it never throws
        Assert.That(result.IsOk && result.Value!.Text == "spoiled beyond help", Is.False);
        if (!result.IsOk) Assert.That(result.Error, Is.Not.Empty);
    }

    [Test]
    public void Format_bits_that_are_a_little_wrong_are_put_right()
    {
        var code    = QrCode.EncodeText("format", QrEcc.Quartile, null, false, 1, 1, 2, false);
        var modules = code.ToMatrix();

        // the format bits are written twice: along row 8 and column 8 by the top left finder, and split between the other two finders. Spoil three bits of each copy.
        int n = code.Size;
        foreach (int x in new[] { 0, 2, 4 }) modules[8, x] = !modules[8, x];
        foreach (int y in new[] { n - 1, n - 3, n - 5 }) modules[y, 8] = !modules[y, 8];

        var result = QrDecoder.Decode(modules);
        Assert.That(result.IsOk, Is.True, result.Error);
        Assert.That(result.Value!.Text, Is.EqualTo("format"));
        Assert.That(result.Value.FormatErrors, Is.GreaterThan(0));
    }

    [Test]
    public void The_reed_solomon_code_puts_right_as_many_bytes_as_it_has_half_its_check_bytes()
    {
        var random  = new Random(5);
        var data    = new byte[40];
        random.NextBytes(data);
        const int check = 10;
        var block = data.Concat(QrReedSolomon.Remainder(data, QrReedSolomon.Divisor(check))).ToArray();

        var five = (byte[])block.Clone();
        foreach (int at in new[] { 0, 7, 19, 33, 49 }) five[at] ^= (byte)(random.Next(1, 256));
        Assert.That(QrReedSolomon.Correct(five, check), Is.EqualTo(5));
        Assert.That(five, Is.EqualTo(block));

        var none = (byte[])block.Clone();
        Assert.That(QrReedSolomon.Correct(none, check), Is.EqualTo(0));

        var six = (byte[])block.Clone();
        foreach (int at in new[] { 0, 7, 19, 33, 49, 12 }) six[at] ^= (byte)(random.Next(1, 256));
        // one more than it can take: it says so, or (rarely) lands on some other block; it never gives back the original and calls it right
        int outcome = QrReedSolomon.Correct(six, check);
        Assert.That(outcome == -1 || !six.SequenceEqual(block), Is.True);
    }

    // ---- pictures ---------------------------------------------------------------------------

    [Test]
    public void A_picture_of_gray_levels_is_read_at_several_sizes_and_with_inverted_colors()
    {
        var code = QrCode.EncodeText("Picture this: https://example.com/", QrEcc.Medium);
        foreach (int scale in new[] { 2, 3, 6, 10 })
        {
            var (gray, size) = QrRender.Gray(code, scale, 4);
            var result = QrDecoder.DecodeImage(gray, size, size);
            Assert.That(result.IsOk, Is.True, $"scale {scale}: {result.Error}");
            Assert.That(result.Value!.Text, Is.EqualTo("Picture this: https://example.com/"));
        }

        var (light, side) = QrRender.Gray(code, 4, 4);
        var inverted = light.Select(b => (byte)(255 - b)).ToArray();
        Assert.That(QrDecoder.DecodeImage(inverted, side, side).Value?.Text, Is.EqualTo("Picture this: https://example.com/"));

        var blank = new byte[100 * 100];
        Array.Fill(blank, (byte)255);
        Assert.That(QrDecoder.DecodeImage(blank, 100, 100).IsOk, Is.False);
    }

    [Test]
    public void A_png_has_the_right_header_checksums_and_size()
    {
        var code = QrCode.EncodeText("png", QrEcc.Low);
        var png  = QrRender.Png(code, 5, 3);

        Assert.That(png.Take(8).ToArray(), Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
        int expected = (code.Size + 6) * 5;
        Assert.That(System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), Is.EqualTo(expected));
        Assert.That(System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)), Is.EqualTo(expected));

        // the last chunk is IEND, whose checksum is a known constant
        Assert.That(png.Skip(png.Length - 12).ToArray(), Is.EqualTo(new byte[] { 0, 0, 0, 0, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82 }));
        Assert.That(QrRender.Crc32("123456789"u8), Is.EqualTo(0xCBF43926u));
    }

    [Test]
    public void An_svg_has_a_view_box_of_the_size_with_the_margin_and_one_square_for_each_run_of_dark_modules()
    {
        var code = QrCode.EncodeText("svg", QrEcc.Low);
        var svg  = QrRender.Svg(code, 2, QrColors.From("#112233", "#ffeedd"));

        Assert.That(svg, Does.Contain($"viewBox=\"0 0 {code.Size + 4} {code.Size + 4}\""));
        Assert.That(svg, Does.Contain("#112233"));
        Assert.That(svg, Does.Contain("#FFEEDD"));

        // the dark modules the path adds up to are the dark modules of the code
        int dark = 0;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(svg, @"h(\d+)v1")) dark += int.Parse(m.Groups[1].Value);
        int expected = 0;
        for (int y = 0; y < code.Size; y++)
        for (int x = 0; x < code.Size; x++)
            if (code[x, y]) expected++;
        Assert.That(dark, Is.EqualTo(expected));
    }

    [Test]
    public void A_grid_as_text_is_read_back_whether_it_is_1_and_0_or_half_blocks()
    {
        var code = QrCode.EncodeText("grid", QrEcc.Medium);

        foreach (var text in new[] { QrRender.Matrix(code, 4), QrRender.Matrix(code, 0, '#', '.'), QrRender.Blocks(code, 2) })
        {
            Assert.That(QrRender.TryParseMatrix(text, out var modules, out var problem), Is.True, problem);
            var result = QrDecoder.Decode(modules);
            Assert.That(result.Value?.Text, Is.EqualTo("grid"));
        }

        Assert.That(QrRender.TryParseMatrix("101\n010", out _, out var why), Is.False);
        Assert.That(why, Is.Not.Empty);
        Assert.That(QrRender.TryParseMatrix("  \n ", out _, out _), Is.False);
    }

    [Test]
    public void Colors_are_read_and_judged()
    {
        Assert.That(QrColors.TryParseHex("#abc", out var short_), Is.True);
        Assert.That(short_, Is.EqualTo(((byte)0xAA, (byte)0xBB, (byte)0xCC)));
        Assert.That(QrColors.TryParseHex("12345", out _), Is.False);
        Assert.That(QrColors.TryParseHex("#GGGGGG", out _), Is.False);

        Assert.That(QrColors.BlackOnWhite.Contrast, Is.EqualTo(1).Within(0.001));
        Assert.That(QrColors.From("#777777", "#888888").Contrast, Is.LessThan(0.4));
        Assert.That(QrColors.From("#FFFFFF", "#000000").IsInverted, Is.True);
        Assert.That(QrColors.From("nope", "also bad"), Is.EqualTo(QrColors.BlackOnWhite));
    }

    // ---- the doc ----------------------------------------------------------------------------

    [Test]
    public void A_doc_makes_a_code_that_reads_back_and_says_what_is_wrong_when_it_cannot()
    {
        var ok = QrBuilder.Build(QrDoc.ForText("Hello, Silo"));
        Assert.That(ok.IsBuilt, Is.True);
        Assert.That(ok.CheckPassed, Is.True);
        Assert.That(ok.Payload, Is.EqualTo("Hello, Silo"));
        Assert.That(ok.Fill, Is.InRange(0.0, 1.0));

        Assert.That(QrBuilder.Build(QrDoc.ForText("")).Problem, Does.Contain("nothing"));
        Assert.That(QrBuilder.Build(new QrDoc { Mode = "Numeric", Fields = Fields(("text", "12a")) }).Problem, Does.Contain("digits"));
        Assert.That(QrBuilder.Build(new QrDoc { Mode = "Alphanumeric", Fields = Fields(("text", "lower")) }).Problem, Does.Contain("capitals"));
        Assert.That(QrBuilder.Build(new QrDoc { Fields = Fields(("text", new string('a', 3000))), Ecc = QrEcc.High }).Problem, Does.Contain("too long"));
        Assert.That(QrBuilder.Build(new QrDoc { Kind = QrPayloadKind.Url }).Problem, Does.Contain("Address"));
    }

    [Test]
    public void A_doc_warns_of_what_will_make_the_code_hard_to_read()
    {
        var doc = QrDoc.ForText("warn");
        doc.Border = 1;
        doc.Dark   = "#777777";
        doc.Light  = "#888888";
        doc.Ecc    = QrEcc.Low;
        doc.Boost  = true;

        var warnings = QrBuilder.Build(doc).Warnings;

        Assert.That(warnings.Any(w => w.Contains("too alike")), Is.True);
        Assert.That(warnings.Any(w => w.Contains("margin")), Is.True);
        Assert.That(warnings.Any(w => w.Contains("raised")), Is.True);
        Assert.That(QrBuilder.Build(QrDoc.ForText("fine")).Warnings.Any(w => w.Contains("too alike") || w.Contains("margin")), Is.False);
    }

    [Test]
    public void A_doc_is_kept_and_copied_with_all_its_parts()
    {
        var doc = QrDoc.FromPayload("WIFI:T:WPA;S:Home;P:secret;;", "Home wifi");
        doc.Ecc   = QrEcc.High;
        doc.Mask  = 3;
        doc.Scale = 12;

        var back = JsonConvert.DeserializeObject<QrDoc>(JsonConvert.SerializeObject(doc))!;
        Assert.That(back.Name, Is.EqualTo("Home wifi"));
        Assert.That(back.Kind, Is.EqualTo(QrPayloadKind.Wifi));
        Assert.That(back.Fields["ssid"], Is.EqualTo("Home"));
        Assert.That(back.Ecc, Is.EqualTo(QrEcc.High));
        Assert.That(back.Mask, Is.EqualTo(3));
        Assert.That(QrBuilder.Build(back).Payload, Is.EqualTo("WIFI:T:WPA;S:Home;P:secret;;"));

        var copy = doc.Clone();
        copy.Fields["ssid"] = "Other";
        Assert.That(doc.Fields["ssid"], Is.EqualTo("Home"));

        var odd = new QrDoc { MinVersion = 99, MaxVersion = -3, Mask = 40, Border = -5, Scale = 1000, Mode = "nonsense" };
        odd.Normalize();
        Assert.That((odd.MinVersion, odd.MaxVersion, odd.Mask, odd.Border, odd.Scale, odd.Mode), Is.EqualTo((40, 40, 7, 0, 64, "Auto")));
    }

    // ---- payloads ---------------------------------------------------------------------------

    [Test]
    public void A_wifi_network_is_written_with_its_special_characters_escaped_and_read_back()
    {
        var text = QrPayloads.Build(QrPayloadKind.Wifi, Fields(("ssid", "Caf\\e;1"), ("password", "a:b,c\"d"), ("security", "WPA"), ("hidden", "true"))).Text!;
        Assert.That(text, Is.EqualTo("WIFI:T:WPA;S:Caf\\\\e\\;1;P:a\\:b\\,c\\\"d;H:true;;"));

        var back = QrPayloads.Detect(text);
        Assert.That(back.Kind, Is.EqualTo(QrPayloadKind.Wifi));
        Assert.That(back.Fields["ssid"], Is.EqualTo("Caf\\e;1"));
        Assert.That(back.Fields["password"], Is.EqualTo("a:b,c\"d"));
        Assert.That(back.Fields["hidden"], Is.EqualTo("true"));

        Assert.That(QrPayloads.Build(QrPayloadKind.Wifi, Fields(("ssid", "Open"), ("security", "nopass"), ("password", "ignored"))).Text, Is.EqualTo("WIFI:T:nopass;S:Open;;"));
        Assert.That(QrPayloads.Build(QrPayloadKind.Wifi, Fields(("ssid", ""))).IsOk, Is.False);
    }

    [Test]
    public void Addresses_mail_phone_texts_and_places_are_written_and_read_back()
    {
        Assert.That(QrPayloads.Build(QrPayloadKind.Url, Fields(("url", "example.com/x"))).Text, Is.EqualTo("https://example.com/x"));
        Assert.That(RoundTrip(QrPayloadKind.Url, Fields(("url", "http://example.com/a?b=c"))).Kind, Is.EqualTo(QrPayloadKind.Url));

        var mail = QrPayloads.Build(QrPayloadKind.Email, Fields(("to", "ada@example.com"), ("subject", "Hi there"), ("body", "a & b"))).Text!;
        Assert.That(mail, Is.EqualTo("mailto:ada@example.com?subject=Hi%20there&body=a%20%26%20b"));
        var mailBack = QrPayloads.Detect(mail);
        Assert.That(mailBack.Fields["to"], Is.EqualTo("ada@example.com"));
        Assert.That(mailBack.Fields["subject"], Is.EqualTo("Hi there"));
        Assert.That(mailBack.Fields["body"], Is.EqualTo("a & b"));

        Assert.That(QrPayloads.Build(QrPayloadKind.Phone, Fields(("number", "+1 555 123 4567"))).Text, Is.EqualTo("tel:+15551234567"));
        Assert.That(QrPayloads.Detect("tel:+15551234567").Fields["number"], Is.EqualTo("+15551234567"));

        var sms = QrPayloads.Build(QrPayloadKind.Sms, Fields(("number", "+15551234567"), ("message", "on my way"))).Text!;
        Assert.That(sms, Is.EqualTo("SMSTO:+15551234567:on my way"));
        Assert.That(QrPayloads.Detect(sms).Fields["message"], Is.EqualTo("on my way"));

        var geo = QrPayloads.Build(QrPayloadKind.Geo, Fields(("lat", "51.5007"), ("lon", "-0.1246"), ("query", "Big Ben"))).Text!;
        Assert.That(geo, Is.EqualTo("geo:51.5007,-0.1246?q=Big%20Ben"));
        Assert.That(QrPayloads.Detect(geo).Fields["query"], Is.EqualTo("Big Ben"));
        Assert.That(QrPayloads.Build(QrPayloadKind.Geo, Fields(("lat", "95"), ("lon", "0"))).IsOk, Is.False);
    }

    [Test]
    public void A_contact_is_written_as_a_vcard_and_read_back_with_its_special_characters()
    {
        var fields = Fields(("first", "Ada"), ("last", "Lovelace"), ("org", "Engines; Analytical"), ("phone", "+441234567890"), ("email", "ada@example.com"), ("city", "London"), ("country", "UK"),
                            ("note", "line one\nline two, with a comma"));
        var back = RoundTrip(QrPayloadKind.Contact, fields);

        Assert.That(back.Kind, Is.EqualTo(QrPayloadKind.Contact));
        foreach (var key in new[] { "first", "last", "org", "phone", "email", "city", "country", "note" })
            Assert.That(back.Fields[key], Is.EqualTo(fields[key]), key);

        Assert.That(QrPayloads.Build(QrPayloadKind.Contact, Fields()).IsOk, Is.False);
    }

    [Test]
    public void A_one_time_password_is_written_for_an_authenticator_and_read_back()
    {
        var text = QrPayloads.Build(QrPayloadKind.Otp, Fields(("type", "totp"), ("label", "ada@example.com"), ("issuer", "Silo Inc"), ("secret", "jbsw y3dp-ehpk 3pxp"), ("algorithm", "SHA256"), ("digits", "8"), ("period", "60"))).Text!;
        Assert.That(text, Is.EqualTo("otpauth://totp/Silo%20Inc:ada%40example.com?secret=JBSWY3DPEHPK3PXP&issuer=Silo%20Inc&algorithm=SHA256&digits=8&period=60"));

        var back = QrPayloads.Detect(text);
        Assert.That(back.Kind, Is.EqualTo(QrPayloadKind.Otp));
        Assert.That(back.Fields["label"], Is.EqualTo("ada@example.com"));
        Assert.That(back.Fields["issuer"], Is.EqualTo("Silo Inc"));
        Assert.That(back.Fields["secret"], Is.EqualTo("JBSWY3DPEHPK3PXP"));
        Assert.That(back.Fields["period"], Is.EqualTo("60"));

        Assert.That(QrPayloads.Build(QrPayloadKind.Otp, Fields(("label", "x"), ("secret", "not base32!"))).IsOk, Is.False);
        Assert.That(QrPayloads.Build(QrPayloadKind.Otp, Fields(("type", "hotp"), ("label", "x"), ("secret", "ABCD"), ("counter", "5"))).Text, Does.EndWith("&counter=5"));
    }

    [Test]
    public void An_event_is_written_for_a_calendar_and_read_back_as_a_time_or_as_days()
    {
        var timed = QrPayloads.Build(QrPayloadKind.Event, Fields(("summary", "Launch, with cake"), ("start", "2026-10-31 19:30"), ("end", "2026-10-31 21:00"), ("location", "The hall"))).Text!;
        Assert.That(timed, Does.Contain("DTSTART:20261031T193000"));
        Assert.That(timed, Does.Contain("SUMMARY:Launch\\, with cake"));

        var back = QrPayloads.Detect(timed);
        Assert.That(back.Kind, Is.EqualTo(QrPayloadKind.Event));
        Assert.That(back.Fields["summary"], Is.EqualTo("Launch, with cake"));
        Assert.That(back.Fields["start"], Is.EqualTo("2026-10-31 19:30"));
        Assert.That(back.Fields["end"], Is.EqualTo("2026-10-31 21:00"));

        // a day's event ends the day after, in the standard, and is shown as the last day
        var days = QrPayloads.Build(QrPayloadKind.Event, Fields(("summary", "Trip"), ("start", "2026-11-02"), ("end", "2026-11-04"))).Text!;
        Assert.That(days, Does.Contain("DTEND;VALUE=DATE:20261105"));
        Assert.That(QrPayloads.Detect(days).Fields["end"], Is.EqualTo("2026-11-04"));

        Assert.That(QrPayloads.Build(QrPayloadKind.Event, Fields(("summary", "x"), ("start", "tomorrow"))).IsOk, Is.False);
        Assert.That(QrPayloads.Build(QrPayloadKind.Event, Fields(("summary", "x"), ("start", "2026-10-31 19:30"), ("end", "2026-10-31 18:00"))).IsOk, Is.False);
    }

    [Test]
    public void Text_that_is_not_one_of_the_kinds_stays_plain_text_and_a_payload_makes_a_code_that_reads_back()
    {
        Assert.That(QrPayloads.Detect("just some words").Kind, Is.EqualTo(QrPayloadKind.Text));
        Assert.That(QrPayloads.Detect("https://example.com with spaces").Kind, Is.EqualTo(QrPayloadKind.Text));
        Assert.That(QrPayloads.Detect("WIFI:nonsense").Kind, Is.EqualTo(QrPayloadKind.Text));
        Assert.That(QrPayloads.Detect("geo:somewhere").Kind, Is.EqualTo(QrPayloadKind.Text));
        Assert.That(QrPayloads.Detect("").Kind, Is.EqualTo(QrPayloadKind.Text));

        foreach (var kind in Enum.GetValues<QrPayloadKind>())
            Assert.That(QrPayloads.FieldsOf(kind).Select(f => f.Key), Is.Unique, kind.ToString());

        var doc    = QrDoc.FromPayload("WIFI:T:WPA;S:Guest;P:welcome;;");
        var build  = QrBuilder.Build(doc);
        var result = QrDecoder.Decode(build.Code!.ToMatrix());
        Assert.That(QrPayloads.Detect(result.Value!.Text).Kind, Is.EqualTo(QrPayloadKind.Wifi));
    }
}

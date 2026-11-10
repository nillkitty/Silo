using System.IO;
using Silo.DataEditing.Compression;

namespace Silo.Tests.Tools;

public class KosinskiTests
{
    [Test]
    public void A_single_byte_compresses_to_the_stream_the_format_gives_it()
    {
        // flags 1 (literal), 0 1 (the end mark's "full" code): 0b101 = 05 00; the literal; then the end mark 00 F0 00
        Assert.That(Kosinski.Compress([0x41]), Is.EqualTo(new byte[] { 0x05, 0x00, 0x41, 0x00, 0xF0, 0x00 }));
        Assert.That(Kosinski.Decompress([0x05, 0x00, 0x41, 0x00, 0xF0, 0x00]), Is.EqualTo(new byte[] { 0x41 }));
    }

    [Test]
    public void Empty_data_is_just_the_end_mark()
    {
        byte[] packed = Kosinski.Compress([]);
        Assert.That(packed, Is.EqualTo(new byte[] { 0x02, 0x00, 0x00, 0xF0, 0x00 }));
        Assert.That(Kosinski.Decompress(packed), Is.Empty);
    }

    [Test]
    public void Each_kind_of_copy_decodes()
    {
        // 'ab', then a short copy (00 + 01 = 3 bytes, offset 2: byte 0xFE) which overlaps what it writes, then the end mark
        // flags, low bit first: 1 1 | 0 0 0 1 | 0 1 -> 0b10_1000_11 = 0x0A3 ... built by hand below
        var  bits  = new List<bool> { true, true, false, false, false, true, false, true };
        int  word  = 0;
        for (int i = 0; i < bits.Count; i++) if (bits[i]) word |= 1 << i;

        byte[] data = [(byte)word, (byte)(word >> 8), (byte)'a', (byte)'b', 0xFE, 0x00, 0xF0, 0x00];
        Assert.That(System.Text.Encoding.ASCII.GetString(Kosinski.Decompress(data)), Is.EqualTo("ababa"));
    }

    [Test]
    public void Data_of_every_shape_survives_a_round_trip()
    {
        var random = new Random(7);
        var cases  = new List<byte[]>
                     {
                         new byte[1000],                                                                  // one long run
                         Enumerable.Range(0, 5000).Select(i => (byte)(i % 7)).ToArray(),                  // short period
                         Enumerable.Range(0, 20000).Select(i => (byte)(i * i >> 3)).ToArray(),            // far repeats
                         Enumerable.Range(0, 3000).Select(_ => (byte)random.Next(256)).ToArray(),         // nothing to find
                         Enumerable.Range(0, 40000).Select(i => (byte)(random.Next(4) + (i / 9000))).ToArray()
                     };

        foreach (var data in cases)
        {
            byte[] packed = Kosinski.Compress(data);
            Assert.That(Kosinski.Decompress(packed), Is.EqualTo(data));
        }

        Assert.That(Kosinski.Compress(cases[0]).Length, Is.LessThan(100), "a run of zeroes packs small");
    }

    [Test]
    public void Matches_reach_as_far_back_as_the_format_allows()
    {
        // the same 40 bytes 8192 apart, with noise between: a full-form copy at the greatest offset
        var random = new Random(3);
        var chunk  = Enumerable.Range(0, 40).Select(_ => (byte)random.Next(256)).ToArray();
        var data   = chunk.Concat(Enumerable.Range(0, 8192 - 40).Select(_ => (byte)random.Next(256))).Concat(chunk).ToArray();

        byte[] packed = Kosinski.Compress(data);
        Assert.That(Kosinski.Decompress(packed), Is.EqualTo(data));

        // without the repeat there would be 40 more literals (45 bytes); the far copy costs 3
        byte[] without = Kosinski.Compress(data[..^40]);
        Assert.That(packed.Length - without.Length, Is.LessThan(8), "the far copy was used");
    }

    [Test]
    public void Bytes_after_the_end_mark_are_ignored_and_counted()
    {
        byte[] packed = Kosinski.Compress([1, 2, 3, 4]).Concat(new byte[] { 0xAA, 0xBB }).ToArray();
        var    data   = Kosinski.Decompress(packed, out int used);

        Assert.That(data, Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
        Assert.That(used, Is.EqualTo(packed.Length - 2));
    }

    [Test]
    public void Broken_data_is_refused_with_where()
    {
        Assert.That(Assert.Throws<InvalidDataException>(() => Kosinski.Decompress([0x05, 0x00, 0x41]))!.Message, Does.Contain("before its end mark"));
        // a short copy with nothing before it to copy
        Assert.That(Assert.Throws<InvalidDataException>(() => Kosinski.Decompress([0x00, 0x00, 0xFF, 0x00, 0xF0, 0x00]))!.Message, Does.Contain("before the start"));
    }

    /// <summary>Set SILO_KOS_DIR to a folder of real .kos files (the disassembly's) to check them all; skipped otherwise.</summary>
    [Test]
    public void Real_kosinski_files_decompress_and_recompress()
    {
        string? dir = Environment.GetEnvironmentVariable("SILO_KOS_DIR");
        if (dir is null || !Directory.Exists(dir)) Assert.Ignore("SILO_KOS_DIR is not set to a folder.");

        var files = Directory.EnumerateFiles(dir!, "*.kos", SearchOption.AllDirectories).ToList();
        Assert.That(files, Is.Not.Empty);
        long before = 0, after = 0;
        foreach (string file in files)
        {
            byte[] packed = File.ReadAllBytes(file);
            var    data   = Kosinski.Decompress(packed, out int used);
            Assert.That(used, Is.EqualTo(packed.Length).Or.LessThanOrEqualTo(packed.Length), file);
            Assert.That(Kosinski.Decompress(Kosinski.Compress(data)), Is.EqualTo(data), file);
            before += packed.Length;
            after  += Kosinski.Compress(data).Length;
        }

        TestContext.Out.WriteLine($"{files.Count} file(s): the originals total {before:N0} bytes, ours {after:N0}");
    }
}

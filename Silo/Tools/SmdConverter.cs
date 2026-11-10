using System.IO;
using System.Text;

namespace Silo.Tools;

/// <summary>
///     Converts a Super Magic Drive (<c>.smd</c>) dump of a Mega Drive / Genesis game to the plain ROM image (<c>.bin</c>) that assemblers and
///     disassembly tools expect. An SMD is a 512-byte header and then blocks of 16 KB, each block holding the odd bytes of 16 KB of ROM in its
///     first half and the even bytes in its second half.
/// </summary>
public static class SmdConverter
{
    public const int HeaderSize = 512;

    public const int BlockSize = 16 * 1024;

    private const int Half = BlockSize / 2;

    /// <summary>What a file turned out to be.</summary>
    public enum Kind
    {
        /// <summary>Neither: the report says what was seen.</summary>
        Unknown,

        /// <summary>An SMD: a 512-byte header and interleaved blocks.</summary>
        Smd,

        /// <summary>Already a plain ROM image.</summary>
        Rom
    }

    /// <summary>What was found out about a file: its kind, and the lines of the report (what was looked at and what it showed) that explain why.</summary>
    public sealed record Analysis(Kind Kind, IReadOnlyList<string> Report);

    /// <summary>What a file turned into.</summary>
    /// <param name="Rom">The ROM image.</param>
    /// <param name="Blocks">How many 16 KB blocks were converted.</param>
    /// <param name="Notes">Anything odd about the file, one line each.</param>
    /// <param name="Report">The analysis of the input and a check of the output.</param>
    public sealed record Result(byte[] Rom, int Blocks, IReadOnlyList<string> Notes, IReadOnlyList<string> Report);

    /// <summary>Whether the bytes are already a ROM image: "SEGA" (or " SEGA", as some carts have) at $100.</summary>
    public static bool LooksLikeRom(ReadOnlySpan<byte> data) =>
        data.Length >= 0x105 && (data[0x100..0x104].SequenceEqual("SEGA"u8) || (data[0x100] == ' ' && data[0x101..0x105].SequenceEqual("SEGA"u8)));

    /// <summary>
    ///     Whether the bytes look like an SMD: at least a header and a block, and the header's mark (<c>AA BB</c> at bytes 8 and 9), or -- for a
    ///     header that lacks it -- a size of a header and whole blocks that deinterleaves into a ROM with a Sega header.
    /// </summary>
    public static bool LooksLikeSmd(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize + BlockSize) return false;
        if (data[8] == 0xAA && data[9] == 0xBB) return true;

        return (data.Length - HeaderSize) % BlockSize == 0 && LooksLikeRom(Deinterleave(data, 1));
    }

    /// <summary>Looks at a file and says what it is, and why that is what was decided: the size, the header, the Sega header, the checksum.</summary>
    public static Analysis Analyze(ReadOnlySpan<byte> data)
    {
        var report = new List<string> { $"Size: {data.Length:N0} bytes ({data.Length / 1024.0:N1} KB); " + $"{data.Length % BlockSize:N0} left over after whole 16 KB blocks, {(data.Length - HeaderSize) % BlockSize:N0} after a 512-byte header and whole blocks." };

        if (data.Length >= 16) report.Add("First 16 bytes: " + Convert.ToHexString(data[..16]).Chunk(2).Select(c => new string(c)).Aggregate((a, b) => a + " " + b));

        bool rom = LooksLikeRom(data);
        report.Add(data.Length >= 0x105 ? $"At $100 (where a ROM has \"SEGA\"): {Printable(data.Slice(0x100, Math.Min(16, data.Length - 0x100)))}" : "Too short to have a ROM header at $100.");

        if (data.Length >= HeaderSize + 0x105)
            report.Add($"At $300 (where an SMD, after its 512-byte header, would have it interleaved): {Printable(data.Slice(0x300, Math.Min(16, data.Length - 0x300)))}");

        if (rom)
        {
            report.Add("It has the Sega header at $100: this is already a plain ROM image.");
            ReportRom(data, report);
            return new Analysis(Kind.Rom, report);
        }

        bool marks = data.Length > 9 && data[8] == 0xAA && data[9] == 0xBB;
        if (data.Length > 10) report.Add($"Header marks: AA BB at bytes 8-9 {(marks ? "present" : "absent")}; byte 1 = ${data[1]:X2} (an SMD normally has $03, but many do not); byte 10 = ${data[10]:X2} (06 for a Genesis game).");

        if (data.Length < HeaderSize + BlockSize)
        {
            report.Add("Too small to be an SMD (a header and at least one 16 KB block).");
            return new Analysis(Kind.Unknown, report);
        }

        int blocks = (data.Length - HeaderSize) / BlockSize;
        if (marks) report.Add($"The header says {data[0]} block(s); the file holds {blocks} whole block(s) of 16 KB.");

        var converted = Deinterleave(data, blocks);
        if (LooksLikeRom(converted))
        {
            report.Add($"Deinterleaved after the 512-byte header, it has the Sega header at $100{(marks ? string.Empty : " (even though the header has no AA BB mark)")}: this is an SMD.");
            ReportRom(converted, report);
            return new Analysis(Kind.Smd, report);
        }

        if (marks)
        {
            report.Add("The header marks say SMD, but deinterleaving does not give a Sega header at $100; it will be converted anyway and may not be a Mega Drive ROM.");
            return new Analysis(Kind.Smd, report);
        }

        report.Add("It is neither a plain ROM nor an SMD that deinterleaves to one.");
        return new Analysis(Kind.Unknown, report);
    }

    /// <summary>
    ///     Undoes the interleave of each block. Whole blocks only: a partial block at the end is dropped and noted. A file that is already a
    ///     plain ROM, or that is not an SMD, is refused with the analysis as the reason.
    /// </summary>
    /// <exception cref="InvalidDataException">The bytes are not an SMD.</exception>
    public static Result ToBin(ReadOnlySpan<byte> smd)
    {
        var analysis = Analyze(smd);
        if (analysis.Kind == Kind.Rom) throw new InvalidDataException("This is already a plain ROM image (it has the Sega header at $100): no conversion is needed. " + Summary(analysis));
        if (analysis.Kind != Kind.Smd) throw new InvalidDataException("This does not look like an SMD file. " + Summary(analysis));

        var notes  = new List<string>();
        int blocks = (smd.Length - HeaderSize) / BlockSize;
        int extra  = (smd.Length - HeaderSize) % BlockSize;
        if (extra != 0) notes.Add($"{extra} byte(s) after the last whole block were left out.");
        if (smd[0] != 0 && smd[0] != blocks) notes.Add($"The header says {smd[0]} block(s) but the file holds {blocks}; the file's length was used.");

        var rom = Deinterleave(smd, blocks);
        if (!LooksLikeRom(rom)) notes.Add("The result has no Sega header at $100; it may not be a Mega Drive ROM, or it may be a different interleave.");

        return new Result(rom, blocks, notes, analysis.Report);
    }

    private static string Summary(Analysis a) => string.Join(" ", a.Report);

    /// <summary>The ROM of the whole blocks after a 512-byte header (<paramref name="blocks" /> 1 to convert just the first, for a trial).</summary>
    private static byte[] Deinterleave(ReadOnlySpan<byte> smd, int blocks)
    {
        blocks = Math.Min(blocks, Math.Max(0, (smd.Length - HeaderSize) / BlockSize));
        var rom = new byte[blocks * BlockSize];
        for (int b = 0; b < blocks; b++)
        {
            var block = smd.Slice(HeaderSize + b * BlockSize, BlockSize);
            int at    = b * BlockSize;
            for (int i = 0; i < Half; i++)
            {
                rom[at + 2 * i + 1] = block[i];        // the first half is the odd bytes
                rom[at + 2 * i]     = block[Half + i]; // the second half is the even bytes
            }
        }

        return rom;
    }

    /// <summary>The Sega header's facts: the names, the ROM's size by its header against its length, and whether the checksum adds up.</summary>
    private static void ReportRom(ReadOnlySpan<byte> rom, List<string> report)
    {
        string title = TitleOf(rom);
        if (title.Length > 0) report.Add("Title: " + title);
        if (rom.Length < 0x200) return;

        uint start = Be32(rom, 0x1A0), end = Be32(rom, 0x1A4);
        if (end >= start && end < 0x4000000)
            report.Add($"The header says the ROM runs ${start:X} to ${end:X} ({(long)end + 1 - start:N0} bytes); the data is {rom.Length:N0} bytes{(end + 1 == rom.Length ? ", which agrees" : (end + 1 > rom.Length ? ", which is shorter: the dump is cut short" : ", which is longer (padding)"))}.");

        ushort stored = (ushort)((rom[0x18E] << 8) | rom[0x18F]);
        uint   sum    = 0;
        int    limit  = end >= start && end + 1 <= (uint)rom.Length ? (int)(end + 1) : rom.Length;
        for (int i = 0x200; i + 1 < limit; i += 2) sum += (uint)((rom[i] << 8) | rom[i + 1]);
        report.Add($"Checksum: the header has ${stored:X4}; the data adds up to ${(ushort)sum:X4}{((ushort)sum == stored ? " (a match: the dump is good)" : " (no match: a few games ship with a wrong checksum, but it can mean a bad dump or a changed ROM)")}.");
    }

    private static uint Be32(ReadOnlySpan<byte> d, int at) => (uint)((d[at] << 24) | (d[at + 1] << 16) | (d[at + 2] << 8) | d[at + 3]);

    private static string Printable(ReadOnlySpan<byte> bytes)
    {
        var text = new StringBuilder();
        foreach (byte b in bytes) text.Append(b is >= 32 and < 127 ? (char)b : '.');
        return $"\"{text}\" ({Convert.ToHexString(bytes)})";
    }

    /// <summary>The interleave again, with a header (for tests, and for tools that want an SMD).</summary>
    public static byte[] FromBin(ReadOnlySpan<byte> rom)
    {
        int blocks = (rom.Length + BlockSize - 1) / BlockSize;
        var smd    = new byte[HeaderSize + blocks * BlockSize];
        smd[0]  = (byte)Math.Min(blocks, 255);
        smd[1]  = 0x03;
        smd[8]  = 0xAA;
        smd[9]  = 0xBB;
        smd[10] = 0x06;

        for (int b = 0; b < blocks; b++)
        {
            int at = HeaderSize + b * BlockSize;
            for (int i = 0; i < Half; i++)
            {
                int even = b * BlockSize + 2 * i, odd = even + 1;
                smd[at + i]        = odd < rom.Length ? rom[odd] : (byte)0;
                smd[at + Half + i] = even < rom.Length ? rom[even] : (byte)0;
            }
        }

        return smd;
    }

    /// <summary>Converts a file; the ROM is written to <paramref name="output" /> (beside the SMD, with <c>.bin</c>, when null).</summary>
    /// <returns>Where the ROM was written, and how it went.</returns>
    public static (string Path, Result Result) ConvertFile(string input, string? output = null, bool overwrite = false)
    {
        string target = output ?? Path.ChangeExtension(input, ".bin");
        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(input), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The output would replace the input.");
        if (!overwrite && File.Exists(target)) throw new IOException($"{Path.GetFileName(target)} already exists.");

        var result = ToBin(File.ReadAllBytes(input));
        File.WriteAllBytes(target, result.Rom);
        return (target, result);
    }

    /// <summary>The name stored in a ROM's header (overseas, at $150, else domestic, at $120), for showing what was converted; empty when there is none.</summary>
    public static string TitleOf(ReadOnlySpan<byte> rom)
    {
        if (rom.Length < 0x180) return string.Empty;

        static string Clean(string s) => string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        string over = Clean(Encoding.ASCII.GetString(rom.Slice(0x150, 48)).Trim('\0', ' '));
        return over.Length > 0 ? over : Clean(Encoding.ASCII.GetString(rom.Slice(0x120, 48)).Trim('\0', ' '));
    }
}

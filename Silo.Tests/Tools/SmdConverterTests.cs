using System.IO;
using System.Text;
using Silo.Providers.Conversion;

namespace Silo.Tests.Tools;

public class SmdConverterTests
{
    /// <summary>A ROM of a few blocks with the Sega header where a Mega Drive has it and a recognisable pattern elsewhere.</summary>
    private static byte[] Rom(int blocks)
    {
        var rom = new byte[blocks * SmdConverter.BlockSize];
        for (int i = 0; i < rom.Length; i++) rom[i] = (byte)(i * 7 + (i >> 8));

        Encoding.ASCII.GetBytes("SEGA MEGA DRIVE ").CopyTo(rom, 0x100);
        Encoding.ASCII.GetBytes("SONIC THE               HEDGEHOG 2".PadRight(48)).CopyTo(rom, 0x150);
        return rom;
    }

    [Test]
    public void Converting_an_interleaved_dump_gives_back_the_rom()
    {
        var rom = Rom(3);
        var smd = SmdConverter.FromBin(rom);

        Assert.That(SmdConverter.LooksLikeSmd(smd), Is.True);
        Assert.That(smd, Has.Length.EqualTo(SmdConverter.HeaderSize + rom.Length));

        var result = SmdConverter.ToBin(smd);
        Assert.That(result.Rom, Is.EqualTo(rom));
        Assert.That(result.Blocks, Is.EqualTo(3));
        Assert.That(result.Notes, Is.Empty);
    }

    [Test]
    public void The_first_half_of_a_block_is_the_odd_bytes_and_the_second_the_even()
    {
        var smd = new byte[SmdConverter.HeaderSize + SmdConverter.BlockSize];
        smd[1] = 0x03; smd[8] = 0xAA; smd[9] = 0xBB;
        smd[SmdConverter.HeaderSize]                          = 0x11; // first of the odd bytes: ROM byte 1
        smd[SmdConverter.HeaderSize + SmdConverter.BlockSize / 2] = 0x22; // first of the even bytes: ROM byte 0

        var rom = SmdConverter.ToBin(smd).Rom;
        Assert.That(rom[0], Is.EqualTo(0x22));
        Assert.That(rom[1], Is.EqualTo(0x11));
    }

    [Test]
    public void A_file_that_is_not_an_smd_is_refused_with_a_reason()
    {
        Assert.That(Assert.Throws<InvalidDataException>(() => SmdConverter.ToBin(new byte[100]))!.Message, Does.Contain("Too small to be an SMD"));
        Assert.That(Assert.Throws<InvalidDataException>(() => SmdConverter.ToBin(Rom(1)))!.Message, Does.Contain("already a plain ROM"));
    }

    [Test]
    public void Oddities_are_noted_not_fatal()
    {
        var smd = SmdConverter.FromBin(Rom(2));
        smd[0] = 9; // a wrong block count
        var longer = smd.Concat(new byte[10]).ToArray();

        var result = SmdConverter.ToBin(longer);
        Assert.That(result.Blocks, Is.EqualTo(2));
        Assert.That(result.Notes, Has.Some.Contain("header says 9"));
        Assert.That(result.Notes, Has.Some.Contain("10 byte(s)"));
    }

    [Test]
    public void A_header_with_the_marks_but_no_03_at_byte_1_is_still_an_smd()
    {
        // what real dumps of Sonic 2 look like: 40 00 00 00 00 00 00 00 AA BB 06
        var smd = SmdConverter.FromBin(Rom(2));
        smd[1] = 0x00;

        Assert.That(SmdConverter.LooksLikeSmd(smd), Is.True);
        Assert.That(SmdConverter.ToBin(smd).Rom, Is.EqualTo(Rom(2)));
    }

    [Test]
    public void A_header_without_the_marks_is_recognised_when_it_deinterleaves_to_a_rom()
    {
        var smd = SmdConverter.FromBin(Rom(2));
        smd[8] = smd[9] = 0;

        var analysis = SmdConverter.Analyze(smd);
        Assert.That(analysis.Kind, Is.EqualTo(SmdConverter.Kind.Smd));
        Assert.That(analysis.Report, Has.Some.Contain("even though the header has no AA BB mark"));
    }

    [Test]
    public void The_analysis_says_what_it_saw_when_a_file_is_not_recognised()
    {
        var analysis = SmdConverter.Analyze(new byte[SmdConverter.HeaderSize + SmdConverter.BlockSize]);

        Assert.That(analysis.Kind, Is.EqualTo(SmdConverter.Kind.Unknown));
        Assert.That(analysis.Report, Has.Some.StartWith("Size:"));
        Assert.That(analysis.Report, Has.Some.StartWith("First 16 bytes:"));
        Assert.That(analysis.Report, Has.Some.Contain("At $100"));
        Assert.That(analysis.Report, Has.Some.Contain("neither a plain ROM nor an SMD"));
        Assert.That(Assert.Throws<InvalidDataException>(() => SmdConverter.ToBin(new byte[SmdConverter.HeaderSize + SmdConverter.BlockSize]))!.Message, Does.Contain("Size:"));
    }

    [Test]
    public void The_checksum_of_the_rom_is_checked_and_reported()
    {
        var rom = Rom(1);
        rom[0x1A0] = rom[0x1A1] = rom[0x1A2] = rom[0x1A3] = 0;
        rom[0x1A4] = 0; rom[0x1A5] = 0; rom[0x1A6] = 0x3F; rom[0x1A7] = 0xFF;
        uint sum = 0;
        for (int i = 0x200; i < rom.Length; i += 2) sum += (uint)((rom[i] << 8) | rom[i + 1]);
        rom[0x18E] = (byte)(sum >> 8);
        rom[0x18F] = (byte)sum;

        Assert.That(SmdConverter.Analyze(rom).Report, Has.Some.Contain("a match"));

        rom[0x18F] ^= 0xFF;
        Assert.That(SmdConverter.Analyze(rom).Report, Has.Some.Contain("no match"));
    }

    /// <summary>Set SILO_SMD_SAMPLE to the path of a real dump to check it end to end; skipped otherwise.</summary>
    [Test]
    public void A_real_dump_converts_to_a_rom_with_a_good_header()
    {
        string? path = Environment.GetEnvironmentVariable("SILO_SMD_SAMPLE");
        if (path is null || !File.Exists(path)) Assert.Ignore("SILO_SMD_SAMPLE is not set to a file.");

        var result = SmdConverter.ToBin(File.ReadAllBytes(path!));
        TestContext.Out.WriteLine(string.Join(Environment.NewLine, result.Report));
        foreach (string note in result.Notes) TestContext.Out.WriteLine("note: " + note);

        Assert.That(SmdConverter.LooksLikeRom(result.Rom), Is.True);
        Assert.That(result.Notes, Is.Empty);
    }

    [Test]
    public void The_rom_title_is_read_from_the_header()
    {
        Assert.That(SmdConverter.TitleOf(Rom(1)), Does.StartWith("SONIC THE"));
        Assert.That(SmdConverter.TitleOf(new byte[16]), Is.Empty);
    }

    [Test]
    public void A_file_is_converted_beside_itself_and_will_not_overwrite_unless_asked()
    {
        string dir = Path.Combine(Path.GetTempPath(), "smdtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string smd = Path.Combine(dir, "game.smd");
            File.WriteAllBytes(smd, SmdConverter.FromBin(Rom(1)));

            var (path, result) = SmdConverter.ConvertFile(smd);
            Assert.That(path, Is.EqualTo(Path.Combine(dir, "game.bin")));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(result.Rom));

            Assert.Throws<IOException>(() => SmdConverter.ConvertFile(smd));
            Assert.DoesNotThrow(() => SmdConverter.ConvertFile(smd, null, true));
            Assert.Throws<IOException>(() => SmdConverter.ConvertFile(smd, smd, true), "never over the input");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

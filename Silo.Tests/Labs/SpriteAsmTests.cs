using System.IO;
using Silo.DataEditing.Sprites;
using Silo.Labs.Parser;

namespace Silo.Tests.Labs;

public class SpriteAsmTests
{
    private const string Sample = """
        Map_obj0B:	mappingsTable
        	mappingsTableEntry.w	Map_obj0B_000A
        	mappingsTableEntry.w	Map_obj0B_001C
        	mappingsTableEntry.w	Map_obj0B_000A

        Map_obj0B_000A:	spriteHeader
        	spritePiece	-$10, -$10, 4, 1, 0, 0, 0, 0, 0	; the top
        	spritePiece	-$10, -8, 4, 3, $24, 1, 0, 2, 1
        Map_obj0B_000A_End

        Map_obj0B_001C:	spriteHeader
        Map_obj0B_001C_End

        	even
        """;

    private static IEnumerable<string> Described(SpriteMappings m) =>
        m.Frames.Select(f => string.Join(";", f.Pieces.Select(p => $"{p.X},{p.Y},{p.Width},{p.Height},{p.Tile},{p.XFlip},{p.YFlip},{p.Palette},{p.Priority}")));

    [Test]
    public void The_disassemblys_macros_are_read_with_shared_frames_and_expressions_of_numbers()
    {
        var map = SpriteAsm.ParseMappings(Sample);

        Assert.That(map.Frames, Has.Count.EqualTo(3));
        Assert.That(map.Frames[0].Pieces, Has.Count.EqualTo(2));
        Assert.That(map.Frames[1].Pieces, Is.Empty);
        Assert.That(Described(map).First(), Is.EqualTo("-16,-16,4,1,0,False,False,0,False;-16,-8,4,3,36,True,False,2,True"));
        Assert.That(Described(map).ElementAt(2), Is.EqualTo(Described(map).First()), "the third entry points at the first frame");
    }

    [Test]
    public void Local_labels_belong_to_the_label_before_them()
    {
        var map = SpriteAsm.ParseMappings("""
            .offsets:	mappingsTable
            	mappingsTableEntry.w	.frame0
            	mappingsTableEntry.w	.frame1

            .frame0:	spriteHeader
            	spritePiece	0, 0, 1, 1, 1, 0, 0, 0, 0
            .frame1:	spriteHeader
            	spritePiece	8, 8, 2, 2, 2, 0, 0, 0, 0
            """);

        Assert.That(map.Frames.Select(f => f.Pieces[0].Tile), Is.EqualTo(new[] { 1, 2 }));
    }

    [Test]
    public void Problems_are_reported_with_the_line()
    {
        Assert.That(Assert.Throws<FormatException>(() => SpriteAsm.ParseMappings("nothing here"))!.Message, Does.Contain("no mappings table"));
        Assert.That(Assert.Throws<FormatException>(() => SpriteAsm.ParseMappings(Sample.Replace("$24", "$2G")))!.Message, Does.Contain("not a number"));
        Assert.That(Assert.Throws<FormatException>(() => SpriteAsm.ParseMappings(Sample.Replace("Map_obj0B_001C\n", "Map_obj0B_9999\n")))!.Message, Does.Contain("Map_obj0B_9999"));
        Assert.That(Assert.Throws<FormatException>(() => SpriteAsm.ParseMappings(Sample.Replace("4, 3, $24", "5, 3, $24")))!.Message, Does.Contain("1 to 4 tiles"));
    }

    [Test]
    public void Written_text_reads_back_the_same_and_shares_equal_frames()
    {
        var map  = SpriteAsm.ParseMappings(Sample);
        var text = SpriteAsm.WriteMappings(map, "Map_obj0B");

        Assert.That(text, Does.StartWith("Map_obj0B:\tmappingsTable"));
        Assert.That(Described(SpriteAsm.ParseMappings(text)), Is.EqualTo(Described(map)));
        Assert.That(text.Split("spriteHeader").Length - 1, Is.EqualTo(2), "three entries, two distinct frames");
        Assert.That(text, Does.Contain("-$10, -$10, 4, 1, 0, 0, 0, 0, 0"));
    }

    [Test]
    public void A_dplc_is_read_and_written()
    {
        string text = """
            DPLC_obj08:	mappingsTable
            	mappingsTableEntry.w	DPLC_obj08_002C
            	mappingsTableEntry.w	DPLC_obj08_002E

            DPLC_obj08_002C:	dplcHeader
            DPLC_obj08_002C_End

            DPLC_obj08_002E:	dplcHeader
            	dplcEntry	4, 0
            	dplcEntry	16, $123
            DPLC_obj08_002E_End
            	even
            """;

        Assert.That(SpriteAsm.IsDplc(text), Is.True);
        var dplc = SpriteAsm.ParseDplc(text);
        Assert.That(dplc.Frames[0], Is.Empty);
        Assert.That(dplc.Frames[1], Is.EqualTo(new[] { new DplcRequest(4, 0), new DplcRequest(16, 0x123) }));
        Assert.That(SpriteAsm.ParseDplc(SpriteAsm.WriteDplc(dplc, "DPLC_obj08")).Frames[1], Is.EqualTo(dplc.Frames[1]));
    }

    [Test]
    public void Files_that_are_not_sprite_mappings_are_named_for_what_they_are()
    {
        Assert.That(SpriteAsm.NotSpriteMappings(@"C:\x\s2disasm\mappings\16x16\EHZ.kos"), Does.Contain("16x16 block map"));
        Assert.That(SpriteAsm.NotSpriteMappings(@"C:\x\s2disasm\mappings\128x128\EHZ.kos"), Does.Contain("128x128 chunk map"));
        Assert.That(SpriteAsm.NotSpriteMappings(@"C:\x\s2disasm\mappings\misc\Level Select.eni"), Does.Contain("Enigma"));
        Assert.That(SpriteAsm.NotSpriteMappings(@"C:\x\s2disasm\mappings\special stage\Begin curve right - Frame 1.bin"), Does.Contain("special stage"));
        Assert.That(SpriteAsm.NotSpriteMappings(@"C:\x\s2disasm\mappings\sprite\obj0B.asm"), Is.Null);
    }

    /// <summary>Set SILO_S2_MAPPINGS to the disassembly's mappings folder to check every file in it; skipped otherwise.</summary>
    [Test]
    public void Every_file_of_the_disassemblys_mappings_folder_loads_or_is_named_for_what_it_is()
    {
        string? root = Environment.GetEnvironmentVariable("SILO_S2_MAPPINGS");
        if (root is null || !Directory.Exists(root)) Assert.Ignore("SILO_S2_MAPPINGS is not set to a folder.");

        var binary   = new ParserDataSpec(SpriteMappingSpecs.Sonic2Mappings());
        var dplcSpec = new ParserDataSpec(SpriteMappingSpecs.Sonic2Dplc());
        var bindings = new SpriteBindings(binary.Editor);

        int sprites = 0, dplcs = 0, named = 0, frames = 0, pieces = 0;
        var problems = new List<string>();
        foreach (string file in Directory.EnumerateFiles(root!, "*", SearchOption.AllDirectories).OrderBy(f => f))
        {
            string rel = Path.GetRelativePath(root!, file);
            try
            {
                if (SpriteAsm.NotSpriteMappings(file) is not null)
                {
                    named++;
                    continue;
                }

                if (rel == "MapMacros.asm") continue; // the macros themselves

                string text = File.ReadAllText(file);
                if (SpriteAsm.IsDplc(text))
                {
                    var dplc = SpriteAsm.ParseDplc(text);
                    Assert.That(dplc.Frames, Is.Not.Empty);
                    Assert.That(SpriteAsm.ParseDplc(SpriteAsm.WriteDplc(dplc, "DPLC_x")).Frames.Select(f => string.Join(",", f)), Is.EqualTo(dplc.Frames.Select(f => string.Join(",", f))));

                    // and as bytes through the specification
                    if (dplc.Frames.All(f => f.All(r => r.Tiles is >= 1 and <= 16 && r.Offset <= 0xFFF)))
                    {
                        var back = SpriteCodec.ReadDplc(dplcSpec, SpriteCodec.WriteDplc(dplcSpec, dplc, bindings), bindings);
                        Assert.That(back.Frames.Select(f => string.Join(",", f)), Is.EqualTo(dplc.Frames.Select(f => string.Join(",", f))));
                    }

                    dplcs++;
                    continue;
                }

                var map = SpriteAsm.ParseMappings(text);
                Assert.That(map.Frames, Is.Not.Empty);
                Assert.That(Described(SpriteAsm.ParseMappings(SpriteAsm.WriteMappings(map, "Map_x"))), Is.EqualTo(Described(map)), "text round trip");

                var bytes = SpriteCodec.WriteMappings(binary, map, bindings);
                Assert.That(Described(SpriteCodec.ReadMappings(binary, bytes, bindings)), Is.EqualTo(Described(map)), "binary round trip");

                sprites++;
                frames += map.Frames.Count;
                pieces += map.Frames.Sum(f => f.Pieces.Count);
            }
            catch (Exception ex) when (ex is FormatException or AssertionException)
            {
                problems.Add($"{rel}: {ex.Message.Split('\n')[0]}");
            }
        }

        TestContext.Out.WriteLine($"{sprites} sprite mapping file(s) ({frames} frames, {pieces} pieces), {dplcs} DPLC file(s), {named} file(s) that are not sprite mappings");
        Assert.That(problems, Is.Empty, string.Join(Environment.NewLine, problems));
        Assert.That(sprites, Is.GreaterThan(100));
    }
}

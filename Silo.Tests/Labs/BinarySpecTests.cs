using System.IO;
using Silo.Labs.Parser;
using Silo.Providers.Data;

namespace Silo.Tests.Labs;

public class BinarySpecTests
{
    // Map_obj0B from the s2disasm: two frames, each of two pieces (SonicMappingsVer = 2).
    //   spritePiece -$10, -$10, 4, 1, 0,   0, 0, 0, 0   and   spritePiece -$10, -8, 4, 3, $24, 0, 0, 0, 0
    private static byte[] TwoFrames()
    {
        return
        [
            0x00, 0x04, 0x00, 0x16,                                                         // offsets: frame 0 at 4, frame 1 at $16
            0x00, 0x02,                                                                     // frame 0: 2 pieces
            0xF0, 0x0C, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xF0,                                 // y -16, 4x1 tiles, tile 0, x -16
            0xF8, 0x0E, 0x00, 0x24, 0x00, 0x12, 0xFF, 0xF0,                                 // y -8, 4x3 tiles, tile $24 (2P: $12), x -16
            0x00, 0x01,                                                                     // frame 1: 1 piece
            0xE8, 0x0F, 0x18, 0x04, 0x0C, 0x02, 0xFF, 0xF0                                  // y -24, 4x4, yflip+xflip..., x -16
        ];
    }

    [Test]
    public void Sonic2_mappings_read_frames_and_pieces()
    {
        var spec = CompiledBinarySpec.Compile(SpriteMappingSpecs.Sonic2Mappings());
        Assert.That(spec.Diagnostics, Is.Empty);

        var data = TwoFrames();
        var r    = spec.Parse(data);

        Assert.That(r.Success, Is.True, r.Error);
        Assert.That(r.Consumed, Is.EqualTo(data.Length));

        var frames = (object?[])r.Value!;
        Assert.That(frames, Has.Length.EqualTo(2));
        Assert.That((object?[])frames[0]!, Has.Length.EqualTo(2));

        var piece = (Dictionary<string, object?>)((object?[])frames[0]!)[1]!;
        Assert.That(piece["y"], Is.EqualTo(-8L));
        Assert.That(piece["x"], Is.EqualTo(-16L));
        Assert.That(piece["width"], Is.EqualTo(3L), "width-1 is what is stored");
        Assert.That(piece["height"], Is.EqualTo(2L));
        var tile = (Dictionary<string, object?>)piece["tile1P"]!;
        Assert.That(tile["tile"], Is.EqualTo(0x24L));
        Assert.That(((Dictionary<string, object?>)piece["tile2P"]!)["tile"], Is.EqualTo(0x12L));

        var flipped = (Dictionary<string, object?>)((Dictionary<string, object?>)((object?[])frames[1]!)[0]!)["tile1P"]!;
        Assert.That(flipped["yflip"], Is.EqualTo(1L));
        Assert.That(flipped["xflip"], Is.EqualTo(1L));
        Assert.That(flipped["tile"], Is.EqualTo(4L));
    }

    [Test]
    public void A_truncated_file_fails_where_the_data_ran_out()
    {
        var spec = CompiledBinarySpec.Compile(SpriteMappingSpecs.Sonic2Mappings());
        var data = TwoFrames()[..^3];
        var r    = spec.Parse(data);

        Assert.That(r.Success, Is.False);
        Assert.That(r.ErrorPosition, Is.GreaterThanOrEqualTo(data.Length - 8));
        Assert.That(r.Error, Does.Contain("more byte"));
    }

    [Test]
    public void An_offset_outside_the_data_is_refused()
    {
        var spec = CompiledBinarySpec.Compile(SpriteMappingSpecs.Sonic2Mappings());
        Assert.That(spec.Parse(new byte[] { 0x00, 0x02, 0x7F, 0xFF }).Success, Is.False);
        Assert.That(spec.Parse(new byte[] { 0x00, 0x03 }).Success, Is.False, "an odd first offset is not a table of words");
    }

    [Test]
    public void The_dplc_reads_tile_counts_and_offsets()
    {
        var spec = CompiledBinarySpec.Compile(SpriteMappingSpecs.Sonic2Dplc());
        // one frame (first offset 2), one request: 4 tiles (stored as 3), offset 5
        var r = spec.Parse(new byte[] { 0x00, 0x02, 0x00, 0x01, 0x30, 0x05 });

        Assert.That(r.Success, Is.True, r.Error);
        var request = (Dictionary<string, object?>)((object?[])((object?[])r.Value!)[0]!)[0]!;
        Assert.That(request["tiles"], Is.EqualTo(3L));
        Assert.That(request["offset"], Is.EqualTo(5L));
    }

    [Test]
    public void The_spec_is_an_IDataSpec_that_recognises_and_parses_bytes()
    {
        IDataSpec spec = new ParserDataSpec(SpriteMappingSpecs.Sonic2Mappings());

        Assert.That(spec.MatchesData(new MemoryStream(TwoFrames())), Is.True);
        Assert.That(spec.MatchesData(new MemoryStream("hello, this is text"u8.ToArray())), Is.False);
        Assert.That(spec.MatchesData(new MemoryStream()), Is.Null);

        var r = spec.Parse(new ReadOnlyMemory<byte>(TwoFrames()));
        Assert.That(r.Success, Is.True);
        Assert.That(r.Consumed, Is.EqualTo(TwoFrames().Length));
    }

    [Test]
    public void Numbers_honour_size_sign_and_byte_order()
    {
        var doc = new ParserSpecDoc
                  {
                      Name  = "T.Ints",
                      Mode  = SpecMode.Binary,
                      Rules = [new RuleDef { Name = "r", Body = SpecNode.Seq(SpecNode.Named("a", SpecNode.Int("u16le")), SpecNode.Named("b", SpecNode.Int("s8")), SpecNode.Named("c", SpecNode.Int("u32"))) }]
                  };

        var r = CompiledBinarySpec.Compile(doc).Parse(new byte[] { 0x34, 0x12, 0xFF, 0, 0, 1, 0 });
        var v = (Dictionary<string, object?>)r.Value!;

        Assert.That(v["a"], Is.EqualTo(0x1234L));
        Assert.That(v["b"], Is.EqualTo(-1L));
        Assert.That(v["c"], Is.EqualTo(256L));
    }

    [Test]
    public void Unknown_nodes_and_bad_fields_are_diagnosed()
    {
        var doc = new ParserSpecDoc
                  {
                      Mode  = SpecMode.Binary,
                      Rules = [new RuleDef { Name = "r", Body = SpecNode.Seq(SpecNode.Int("float"), SpecNode.Bits("a:3"), new SpecNode { Kind = "literal", Text = "x" }) }]
                  };

        var spec = CompiledBinarySpec.Compile(doc);
        Assert.That(spec.Diagnostics, Has.Count.EqualTo(3));
    }
}

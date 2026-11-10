using Silo.DataEditing.Sprites;
using Silo.Labs.Parser;
using Silo.Providers.Data;

namespace Silo.Tests.Labs;

public class SpriteEditorTests
{
    // Map_obj0B (two frames: two pieces and one piece), as in BinarySpecTests
    private static byte[] TwoFrames() =>
    [
        0x00, 0x04, 0x00, 0x16,
        0x00, 0x02,
        0xF0, 0x0C, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xF0,
        0xF8, 0x0E, 0x00, 0x24, 0x00, 0x12, 0xFF, 0xF0,
        0x00, 0x01,
        0xE8, 0x0F, 0x18, 0x04, 0x0C, 0x02, 0xFF, 0xF0
    ];

    private static ParserDataSpec Mappings() => new(SpriteMappingSpecs.Sonic2Mappings());

    // ---- the writer ------------------------------------------------------------------------------

    [Test]
    public void Writing_what_was_read_gives_the_same_bytes()
    {
        var spec = CompiledBinarySpec.Compile(SpriteMappingSpecs.Sonic2Mappings());
        var data = TwoFrames();

        var read = spec.Parse(data);
        Assert.That(read.Success, Is.True, read.Error);
        Assert.That(spec.Write(read.Value), Is.EqualTo(data));
    }

    [Test]
    public void An_offset_table_points_equal_frames_at_one_copy()
    {
        var spec   = CompiledBinarySpec.Compile(SpriteMappingSpecs.Sonic2Mappings());
        var piece  = new Dictionary<string, object?> { ["y"] = -8L, ["width"] = 1L, ["height"] = 1L, ["x"] = -8L };
        var frame  = new object?[] { piece };
        var bytes  = spec.Write(new object?[] { frame, frame, frame });

        // three entries, one frame of a count and one piece
        Assert.That(bytes, Has.Length.EqualTo(6 + 2 + 8));
        Assert.That(bytes[..6], Is.EqualTo(new byte[] { 0, 6, 0, 6, 0, 6 }));
    }

    [Test]
    public void A_number_that_does_not_fit_is_refused_when_written()
    {
        var spec = CompiledBinarySpec.Compile(SpriteMappingSpecs.Sonic2Mappings());
        var piece = new Dictionary<string, object?> { ["y"] = 300L };
        Assert.Throws<FormatException>(() => spec.Write(new object?[] { new object?[] { piece } }));
    }

    // ---- drawing ---------------------------------------------------------------------------------

    private static TileSheet Art(params (int Tile, int X, int Y, int Index)[] dots)
    {
        var data = new byte[8 * TileSheet.TileBytes];
        foreach (var (tile, x, y, index) in dots)
        {
            int at = tile * TileSheet.TileBytes + y * 4 + (x >> 1);
            data[at] |= (byte)((x & 1) == 0 ? index << 4 : index);
        }

        return new TileSheet(data);
    }

    [Test]
    public void Tiles_of_a_piece_run_down_its_columns()
    {
        var piece = new SpritePiece { Width = 2, Height = 2 };

        Assert.That(SpriteComposer.Locate(piece, 0, 0).TileOffset, Is.EqualTo(0));
        Assert.That(SpriteComposer.Locate(piece, 0, 8).TileOffset, Is.EqualTo(1), "the next tile is below");
        Assert.That(SpriteComposer.Locate(piece, 8, 0).TileOffset, Is.EqualTo(2), "then the next column");
        Assert.That(SpriteComposer.Locate(piece, 15, 15).TileOffset, Is.EqualTo(3));
    }

    [Test]
    public void A_flip_mirrors_the_whole_piece_and_its_tiles()
    {
        var piece = new SpritePiece { Width = 2, Height = 1, XFlip = true };

        var (tile, x, y) = SpriteComposer.Locate(piece, 0, 0);
        Assert.That((tile, x, y), Is.EqualTo((1, 7, 0)), "the left edge shows the last column, mirrored");

        piece.XFlip = false;
        piece.YFlip = true;
        Assert.That(SpriteComposer.Locate(piece, 3, 0), Is.EqualTo((0, 3, 7)));
    }

    [Test]
    public void A_frame_is_drawn_with_its_palette_line_and_transparent_zero()
    {
        var art     = Art((0, 0, 0, 1), (0, 1, 0, 2));
        var palette = MdPalette.Greys();
        palette[16 + 1] = 0xFF112233;
        palette[16 + 2] = 0xFF445566;

        var frame = SpriteComposer.Render([new SpritePiece { X = -4, Y = -8, Palette = 1 }], art, palette);

        Assert.That((frame.Left, frame.Top, frame.Width, frame.Height), Is.EqualTo((-4, -8, 8, 8)));
        Assert.That(frame.Pixels[0], Is.EqualTo(0xFF112233u));
        Assert.That(frame.Pixels[1], Is.EqualTo(0xFF445566u));
        Assert.That(frame.Pixels[2], Is.EqualTo(0u), "colour 0 is transparent");
    }

    [Test]
    public void The_first_piece_is_on_top_where_pieces_overlap()
    {
        var art     = Art((0, 0, 0, 1), (1, 0, 0, 2));
        var palette = MdPalette.Greys();
        palette[1] = 0xFF0000FF;
        palette[2] = 0xFF00FF00;

        var frame = SpriteComposer.Render([new SpritePiece { Tile = 0 }, new SpritePiece { Tile = 1 }], art, palette);
        Assert.That(frame.Pixels[0], Is.EqualTo(0xFF0000FFu));

        frame = SpriteComposer.Render([new SpritePiece { Tile = 1 }, new SpritePiece { Tile = 0 }], art, palette);
        Assert.That(frame.Pixels[0], Is.EqualTo(0xFF00FF00u));
    }

    [Test]
    public void A_dplc_decides_which_art_a_tile_number_means()
    {
        int[] slots = SpriteComposer.ExpandDplc([new DplcRequest(2, 5), new DplcRequest(1, 9)]);
        Assert.That(slots, Is.EqualTo(new[] { 5, 6, 9 }));

        var through = SpriteComposer.ThroughDplc(slots);
        Assert.That(through(2), Is.EqualTo(9));
        Assert.That(through(3), Is.EqualTo(-1), "a slot nothing fills draws as nothing");

        var art     = Art((5, 0, 0, 1));
        var palette = MdPalette.Greys();
        palette[1] = 0xFFABCDEF;
        var frame = SpriteComposer.Render([new SpritePiece { Tile = 0 }], art, palette, through);
        Assert.That(frame.Pixels[0], Is.EqualTo(0xFFABCDEFu));
    }

    [Test]
    public void Stats_count_the_busiest_scanline()
    {
        var pieces = Enumerable.Range(0, 3).Select(i => new SpritePiece { X = i * 8, Y = 0, Width = 1, Height = 1 }).Append(new SpritePiece { Y = 8 }).ToList();
        var stats  = SpriteComposer.Stats(pieces);

        Assert.That((stats.Pieces, stats.MaxPerLine, stats.Tiles), Is.EqualTo((4, 3, 4)));
        Assert.That(stats.OverLimit, Is.False);
        Assert.That(SpriteComposer.Stats(Enumerable.Range(0, 21).Select(_ => new SpritePiece()).ToList()).OverLimit, Is.True);
    }

    [Test]
    public void Palette_words_become_the_colours_the_VDP_makes()
    {
        Assert.That(MdPalette.Color(0x0EEE), Is.EqualTo(0xFFFFFFFFu));
        Assert.That(MdPalette.Color(0x0E00), Is.EqualTo(0xFF0000FFu));
        Assert.That(MdPalette.Color(0x0000), Is.EqualTo(0xFF000000u));
        Assert.That(MdPalette.Word(MdPalette.Color(0x0A42)), Is.EqualTo((ushort)0x0A42));

        var colors = MdPalette.Read([0x0E, 0xEE, 0x00, 0x0E], 1);
        Assert.That(colors[16], Is.EqualTo(0xFFFFFFFFu));
        Assert.That(colors[17], Is.EqualTo(0xFFFF0000u));
        Assert.That(colors[0], Is.EqualTo(0xFF000000u));
    }

    // ---- the codec -------------------------------------------------------------------------------

    [Test]
    public void The_codec_reads_and_writes_pieces_through_the_spec()
    {
        var spec     = Mappings();
        var bindings = new SpriteBindings(spec.Editor);
        var mappings = SpriteCodec.ReadMappings(spec, TwoFrames(), bindings);

        Assert.That(mappings.Frames, Has.Count.EqualTo(2));
        var piece = mappings.Frames[0].Pieces[1];
        Assert.That((piece.X, piece.Y, piece.Width, piece.Height, piece.Tile), Is.EqualTo((-16, -8, 4, 3, 0x24)));

        var flipped = mappings.Frames[1].Pieces[0];
        Assert.That((flipped.XFlip, flipped.YFlip, flipped.Tile, flipped.Width, flipped.Height), Is.EqualTo((true, true, 4, 4, 4)));

        piece.Tile = 0x30;
        piece.Palette = 2;
        var back = SpriteCodec.ReadMappings(spec, SpriteCodec.WriteMappings(spec, mappings, bindings), bindings);
        Assert.That(back.Frames[0].Pieces[1].Tile, Is.EqualTo(0x30));
        Assert.That(back.Frames[0].Pieces[1].Palette, Is.EqualTo(2));
    }

    [Test]
    public void The_two_player_tile_word_follows_the_first_with_the_tile_halved()
    {
        var spec     = Mappings();
        var bindings = new SpriteBindings(spec.Editor);
        var written  = SpriteCodec.WriteMappings(spec, new SpriteMappings { Frames = [new SpriteFrame { Pieces = [new SpritePiece { Width = 2, Height = 2, Tile = 0x24, Palette = 1, YFlip = true }] }] }, bindings);

        // offsets (2) + count (2) + y, size, 1P word, 2P word, x
        Assert.That(written[6..8], Is.EqualTo(new byte[] { 0x30, 0x24 }), "pal 1, yflip, tile $24");
        Assert.That(written[8..10], Is.EqualTo(new byte[] { 0x30, 0x12 }), "the same, tile $12");
    }

    [Test]
    public void The_dplc_codec_round_trips_requests()
    {
        var spec     = new ParserDataSpec(SpriteMappingSpecs.Sonic2Dplc());
        var bindings = new SpriteBindings();
        var dplc     = new DplcFile { Frames = [[new DplcRequest(4, 0), new DplcRequest(16, 0x123)], []] };

        byte[] bytes = SpriteCodec.WriteDplc(spec, dplc, bindings);
        var    back  = SpriteCodec.ReadDplc(spec, bytes, bindings);

        Assert.That(back.Frames[0], Is.EqualTo(dplc.Frames[0]));
        Assert.That(back.Frames[1], Is.Empty);
    }

    [Test]
    public void Another_games_field_names_are_mapped_in_the_editor_settings()
    {
        var doc = new ParserSpecDoc
                  {
                      Name = "T.Other",
                      Mode = SpecMode.Binary,
                      Root = "m",
                      Rules =
                      [
                          new RuleDef { Name = "m", Body = SpecNode.Offsets("u16", SpecNode.Counted(SpecNode.Int("u8"), SpecNode.Ref("p"))) },
                          new RuleDef { Name = "p", Body = SpecNode.Seq(SpecNode.Named("px", SpecNode.Int("s8")), SpecNode.Named("py", SpecNode.Int("s8")), SpecNode.Bits("w:2,h:2,t:12")) }
                      ],
                      Editor = new DataEditorSpec
                               {
                                   Kind = SpriteMappingSpecs.EditorKind,
                                   Settings = new Dictionary<string, string>
                                              {
                                                  ["piece.x"] = "px", ["piece.y"] = "py", ["piece.width"] = "w", ["piece.height"] = "h", ["piece.tile"] = "t",
                                                  ["piece.palette"] = "pal", ["piece.xflip"] = "xf", ["piece.yflip"] = "yf", ["piece.priority"] = "pr", ["piece.mirror"] = "-"
                                              }
                               }
                  };
        var spec     = new ParserDataSpec(doc);
        var bindings = new SpriteBindings(spec.Editor);

        var map  = new SpriteMappings { Frames = [new SpriteFrame { Pieces = [new SpritePiece { X = 3, Y = -4, Width = 3, Height = 2, Tile = 0x155 }] }] };
        var back = SpriteCodec.ReadMappings(spec, SpriteCodec.WriteMappings(spec, map, bindings), bindings);

        Assert.That((back.Frames[0].Pieces[0].X, back.Frames[0].Pieces[0].Y, back.Frames[0].Pieces[0].Width, back.Frames[0].Pieces[0].Tile), Is.EqualTo((3, -4, 3, 0x155)));
    }

    // ---- the editor spec -------------------------------------------------------------------------

    [Test]
    public void A_spec_keeps_its_editor_in_its_document()
    {
        var doc  = SpriteMappingSpecs.Sonic2Mappings();
        var back = ParserSpecDoc.FromJson(doc.ToJson())!;

        Assert.That(back.Editor, Is.Not.Null);
        Assert.That(back.Editor!.Kind, Is.EqualTo(SpriteMappingSpecs.EditorKind));
        Assert.That(back.Editor.Get("piece.tile"), Is.EqualTo("tile1P.tile"));

        IDataSpec spec = new ParserDataSpec(back);
        Assert.That(spec.Editor, Is.Not.Null.And.Property("Title").EqualTo("Sonic Mappings Editor"));
        Assert.That(new ParserDataSpec(SpriteMappingSpecs.Sonic2Dplc()).Editor, Is.Null);
        Assert.That(((IDataSpec)new ParserDataSpec(new ParserSpecDoc { Name = "T.None" })).Editor, Is.Null);
    }

    [Test]
    public void The_editor_checks_that_the_spec_keeps_the_fields_it_sets()
    {
        var editor = new SonicMappingsEditor();
        var spec   = Mappings();

        Assert.That(editor.CanEdit(spec, spec.Editor!), Is.True);
        Assert.That(editor.Problems(spec, spec.Editor!), Is.Empty);
        Assert.That(editor.CanEdit(new ParserDataSpec(new ParserSpecDoc { Name = "T.Text" }), spec.Editor!), Is.False);

        // a spec that drops the flip fields cannot be edited faithfully
        var lossy = SpriteMappingSpecs.Sonic2Mappings();
        lossy.Rules.First(r => r.Name == "piece").Body = SpecNode.Seq(SpecNode.Named("y", SpecNode.Int("s8")), SpecNode.Bits("unused:4,width:2,height:2"), SpecNode.Named("tile1P", SpecNode.Bits("pri:1,pal:2,unused:3,tile:10")), SpecNode.Named("tile2P", SpecNode.Bits("pri:1,pal:2,yflip:1,xflip:1,tile:11")), SpecNode.Named("x", SpecNode.Int("s16")));
        var lossySpec = new ParserDataSpec(lossy);
        Assert.That(editor.Problems(lossySpec, lossySpec.Editor!), Is.Not.Empty);
    }

    [Test]
    public void A_binary_spec_is_tried_in_the_lab_with_hex_bytes()
    {
        var compiled = CompiledSpec.Compile(SpriteMappingSpecs.Sonic2Mappings());
        var outcome  = SpecEval.Eval(compiled, "00 04 00 16 00 02 F0 0C 00 00 00 00 FF F0 F8 0E 00 24 00 12 FF F0 00 01 E8 0F 18 04 0C 02 FF F0", null);

        Assert.That(outcome.Success, Is.True, outcome.Error);
        Assert.That(outcome.Consumed, Is.EqualTo(32));
        Assert.That(SpecEval.Eval(compiled, "00 04 zz", null).Success, Is.False);
        Assert.That(SpecEval.Eval(compiled, "7F FF", null).Success, Is.False);
    }
}

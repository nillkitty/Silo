namespace Silo.Labs.Parser;

/// <summary>
///     Sonic the Hedgehog 2's sprite mappings and DPLC (dynamic pattern load cue) files as <see cref="SpecMode.Binary" /> specs, after the
///     s2disasm's <c>mappings/MapMacros.asm</c> (<c>SonicMappingsVer = 2</c>, <c>SonicDplcVer = 2</c>).
/// </summary>
/// <remarks>
///     A mappings file is a table of word offsets (from the start of the table) to frames; a frame is a word count and that many 8-byte pieces: Y
///     (signed byte), <c>0000WWHH</c> (width-1 and height-1 in tiles), the 1P tile word, the 2P tile word and X (signed word). A piece's
///     <c>width</c> and <c>height</c> fields are in the file as size-1. A DPLC file is the same table of frames of words: <c>(tiles-1)&lt;&lt;12 | offset</c>.
///     The VDP's 80-sprites-per-line limit is not in the data: it is what drawing the pieces runs into.
/// </remarks>
public static class SpriteMappingSpecs
{
    private const string TileWord = "pri:1,pal:2,yflip:1,xflip:1,tile:11";

    /// <summary>The kind of editor the mappings ask for: what <c>SonicMappingsEditor</c> answers to.</summary>
    public const string EditorKind = "sprite-mappings";

    /// <summary>The Sonic 2 sprite mappings of an object: <c>object[]</c> of frames, each an <c>object[]</c> of piece records.</summary>
    public static ParserSpecDoc Sonic2Mappings() => new()
                                                    {
                                                        Name        = "Sega.Genesis.Sonic2.SpriteMappings",
                                                        Mode        = SpecMode.Binary,
                                                        Root        = "mappings",
                                                        ConvertsTo  = ["json"],
                                                        Editor      = new Silo.Providers.Data.DataEditorSpec
                                                                      {
                                                                          Kind     = EditorKind,
                                                                          Title    = "Sonic Mappings Editor",
                                                                          Settings = new Dictionary<string, string>(Silo.DataEditing.Sprites.SpriteBindings.Defaults, StringComparer.OrdinalIgnoreCase)
                                                                      },
                                                        Rules       =
                                                        [
                                                            new RuleDef
                                                            {
                                                                Name        = "mappings",
                                                                Description = "A table of word offsets to frames.",
                                                                Body        = SpecNode.Offsets("u16", SpecNode.Ref("frame"))
                                                            },
                                                            new RuleDef
                                                            {
                                                                Name        = "frame",
                                                                Description = "A word count of pieces, then the pieces.",
                                                                Body        = SpecNode.Counted(SpecNode.Int("u16"), SpecNode.Ref("piece"))
                                                            },
                                                            new RuleDef
                                                            {
                                                                Name        = "piece",
                                                                Description = "One hardware sprite: where, how big (size-1 tiles) and which tiles.",
                                                                Body = SpecNode.Seq(SpecNode.Named("y", SpecNode.Int("s8")),
                                                                                    SpecNode.Bits("unused:4,width:2,height:2"),
                                                                                    SpecNode.Named("tile1P", SpecNode.Bits(TileWord)),
                                                                                    SpecNode.Named("tile2P", SpecNode.Bits(TileWord)),
                                                                                    SpecNode.Named("x", SpecNode.Int("s16")))
                                                            }
                                                        ]
                                                    };

    /// <summary>The Sonic 2 DPLC of an object: <c>object[]</c> of frames, each an <c>object[]</c> of (tiles-1, offset) records.</summary>
    public static ParserSpecDoc Sonic2Dplc() => Dplc("Sega.Genesis.Sonic2.Dplc", "tiles:4,offset:12");


    private static ParserSpecDoc Dplc(string name, string fields) => new()
                                                                     {
                                                                         Name = name,
                                                                         Mode = SpecMode.Binary,
                                                                         Root = "dplc",
                                                                         Rules =
                                                                         [
                                                                             new RuleDef { Name = "dplc", Body = SpecNode.Offsets("u16", SpecNode.Ref("frame")) },
                                                                             new RuleDef { Name = "frame", Body = SpecNode.Counted(SpecNode.Int("u16"), SpecNode.Ref("request")) },
                                                                             new RuleDef { Name = "request", Description = "Load (tiles-1)+1 tiles of art from this tile offset.", Body = SpecNode.Bits(fields) }
                                                                         ]
                                                                     };
}

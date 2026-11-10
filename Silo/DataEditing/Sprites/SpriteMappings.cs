using Silo.Labs.Parser;
using Silo.Providers.Data;

namespace Silo.DataEditing.Sprites;

/// <summary>One frame of an object: its sprite pieces, the first drawn on top.</summary>
public sealed class SpriteFrame
{
    public List<SpritePiece> Pieces { get; set; } = [];

    public SpriteFrame Clone() => new() { Pieces = Pieces.Select(p => p.Clone()).ToList() };
}

/// <summary>The sprite mappings of an object: its frames, in the order the game numbers them.</summary>
public sealed class SpriteMappings
{
    public List<SpriteFrame> Frames { get; set; } = [];

    public SpriteMappings Clone() => new() { Frames = Frames.Select(f => f.Clone()).ToList() };
}

/// <summary>The DPLC of an object: for each frame, the art it asks to be loaded.</summary>
public sealed class DplcFile
{
    public List<List<DplcRequest>> Frames { get; set; } = [];

    public DplcFile Clone() => new() { Frames = Frames.Select(f => f.ToList()).ToList() };
}

/// <summary>
///     Which fields of a specification's values the sprite editor reads and writes, as dotted paths into the records the specification yields
///     (<c>tile1P.tile</c>). The defaults are Sonic 2's (<see cref="SpriteMappingSpecs" />); another game's specification with other names says
///     so in its <see cref="IDataEditorSpec.Settings" />.
/// </summary>
public sealed class SpriteBindings
{
    public static IReadOnlyDictionary<string, string> Defaults { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                                                                          {
                                                                              ["piece.x"]        = "x",
                                                                              ["piece.y"]        = "y",
                                                                              ["piece.width"]    = "width",
                                                                              ["piece.height"]   = "height",
                                                                              ["piece.tile"]     = "tile1P.tile",
                                                                              ["piece.palette"]  = "tile1P.pal",
                                                                              ["piece.xflip"]    = "tile1P.xflip",
                                                                              ["piece.yflip"]    = "tile1P.yflip",
                                                                              ["piece.priority"] = "tile1P.pri",
                                                                              ["piece.sizeBias"] = "1",
                                                                              ["piece.mirror"]   = "tile1P:tile2P",
                                                                              ["dplc.tiles"]     = "tiles",
                                                                              ["dplc.offset"]    = "offset",
                                                                              ["dplc.tilesBias"] = "1",
                                                                              ["dplc.spec"]      = "Sega.Genesis.Sonic2.Dplc"
                                                                          };

    private readonly IDataEditorSpec? _spec;

    public SpriteBindings(IDataEditorSpec? spec = null)
    {
        _spec = spec;
    }

    public string this[string name] => _spec is null ? Defaults[name] : DataEditorSpec.Setting(_spec, name, Defaults.GetValueOrDefault(name, string.Empty));

    private int Int(string name) => int.TryParse(this[name], out int v) ? v : 0;

    public int SizeBias => Int("piece.sizeBias");

    public int TilesBias => Int("dplc.tilesBias");

    // ---- dotted paths into records ------------------------------------------------------------------

    internal static long Get(object? record, string path)
    {
        object? at = record;
        foreach (string part in path.Split('.'))
        {
            if (at is not IDictionary<string, object?> d || !d.TryGetValue(part, out at)) return 0;
        }

        return at switch
               {
                   null                      => 0,
                   long l                    => l,
                   bool b                    => b ? 1 : 0,
                   _                         => Convert.ToInt64(at, System.Globalization.CultureInfo.InvariantCulture)
               };
    }

    internal static void Set(Dictionary<string, object?> record, string path, long value)
    {
        string[] parts = path.Split('.');
        var      at    = record;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!at.TryGetValue(parts[i], out var next) || next is not Dictionary<string, object?> child)
            {
                child       = new Dictionary<string, object?>();
                at[parts[i]] = child;
            }

            at = child;
        }

        at[parts[^1]] = value;
    }

    // ---- values to models and back ------------------------------------------------------------------

    internal SpritePiece ReadPiece(object? record) => new()
                                                     {
                                                         X        = (int)Get(record, this["piece.x"]),
                                                         Y        = (int)Get(record, this["piece.y"]),
                                                         Width    = (int)Get(record, this["piece.width"])  + SizeBias,
                                                         Height   = (int)Get(record, this["piece.height"]) + SizeBias,
                                                         Tile     = (int)Get(record, this["piece.tile"]),
                                                         Palette  = (int)Get(record, this["piece.palette"]),
                                                         XFlip    = Get(record, this["piece.xflip"]) != 0,
                                                         YFlip    = Get(record, this["piece.yflip"]) != 0,
                                                         Priority = Get(record, this["piece.priority"]) != 0
                                                     };

    internal Dictionary<string, object?> WritePiece(SpritePiece p)
    {
        var record = new Dictionary<string, object?>();
        Set(record, this["piece.x"], p.X);
        Set(record, this["piece.y"], p.Y);
        Set(record, this["piece.width"], p.Width   - SizeBias);
        Set(record, this["piece.height"], p.Height - SizeBias);
        Set(record, this["piece.tile"], p.Tile);
        Set(record, this["piece.palette"], p.Palette);
        Set(record, this["piece.xflip"], p.XFlip ? 1 : 0);
        Set(record, this["piece.yflip"], p.YFlip ? 1 : 0);
        Set(record, this["piece.priority"], p.Priority ? 1 : 0);

        // the second player's tile word: the same, with the tile halved (the two-player mode draws tiles in pairs)
        string[] mirror = this["piece.mirror"].Split(':');
        if (mirror.Length == 2 && mirror[0].Length > 0 && mirror[1].Length > 0)
        {
            Set(record, this["piece.tile"].Replace(mirror[0], mirror[1]), p.Tile >> 1);
            Set(record, this["piece.palette"].Replace(mirror[0], mirror[1]), p.Palette);
            Set(record, this["piece.xflip"].Replace(mirror[0], mirror[1]), p.XFlip ? 1 : 0);
            Set(record, this["piece.yflip"].Replace(mirror[0], mirror[1]), p.YFlip ? 1 : 0);
            Set(record, this["piece.priority"].Replace(mirror[0], mirror[1]), p.Priority ? 1 : 0);
        }

        return record;
    }
}

/// <summary>Reads and writes sprite mappings and DPLC files through a binary data specification.</summary>
public static class SpriteCodec
{
    private static CompiledBinarySpec Binary(IDataSpec spec) =>
        spec is ParserDataSpec { Doc.Mode: SpecMode.Binary } parser
            ? parser.CompiledBinary
            : throw new InvalidOperationException($"{spec.Name} is not a binary specification.");

    public static SpriteMappings ReadMappings(IDataSpec spec, byte[] data, SpriteBindings bindings, string? rule = null)
    {
        var r = Binary(spec).Parse(data, rule);
        if (!r.Success) throw new FormatException($"{r.Error} at byte {r.ErrorPosition}");

        var frames = (r.Value as System.Collections.IEnumerable ?? throw new FormatException("The specification does not give a list of frames."))
                     .Cast<object?>()
                     .Select(f => new SpriteFrame
                                  {
                                      Pieces = (f as System.Collections.IEnumerable ?? Array.Empty<object>()).Cast<object?>().Select(bindings.ReadPiece).ToList()
                                  })
                     .ToList();
        return new SpriteMappings { Frames = frames };
    }

    public static byte[] WriteMappings(IDataSpec spec, SpriteMappings mappings, SpriteBindings bindings, string? rule = null)
    {
        object?[] tree = mappings.Frames.Select(f => (object?)f.Pieces.Select(p => (object?)bindings.WritePiece(p)).ToArray()).ToArray();
        return Binary(spec).Write(tree, rule);
    }

    public static DplcFile ReadDplc(IDataSpec spec, byte[] data, SpriteBindings bindings, string? rule = null)
    {
        var r = Binary(spec).Parse(data, rule);
        if (!r.Success) throw new FormatException($"{r.Error} at byte {r.ErrorPosition}");

        var frames = (r.Value as System.Collections.IEnumerable ?? throw new FormatException("The specification does not give a list of frames."))
                     .Cast<object?>()
                     .Select(f => (f as System.Collections.IEnumerable ?? Array.Empty<object>()).Cast<object?>()
                                                                                                .Select(q => new DplcRequest((int)SpriteBindings.Get(q, bindings["dplc.tiles"]) + bindings.TilesBias,
                                                                                                                             (int)SpriteBindings.Get(q, bindings["dplc.offset"])))
                                                                                                .ToList())
                     .ToList();
        return new DplcFile { Frames = frames };
    }

    public static byte[] WriteDplc(IDataSpec spec, DplcFile dplc, SpriteBindings bindings, string? rule = null)
    {
        object?[] tree = dplc.Frames.Select(f => (object?)f.Select(q =>
                                                                  {
                                                                      var record = new Dictionary<string, object?>();
                                                                      SpriteBindings.Set(record, bindings["dplc.tiles"], q.Tiles - bindings.TilesBias);
                                                                      SpriteBindings.Set(record, bindings["dplc.offset"], q.Offset);
                                                                      return (object?)record;
                                                                  }).ToArray()).ToArray();
        return Binary(spec).Write(tree, rule);
    }
}

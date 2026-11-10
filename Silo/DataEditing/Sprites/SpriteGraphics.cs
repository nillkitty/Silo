namespace Silo.DataEditing.Sprites;

/// <summary>
///     The Mega Drive's colours: four palette lines of sixteen 9-bit colours, each a big-endian word <c>0000 BBB0 GGG0 RRR0</c>. Colour 0 of a line
///     is transparent when a sprite is drawn.
/// </summary>
public static class MdPalette
{
    public const int Lines = 4;

    public const int ColorsPerLine = 16;

    // what the VDP's DAC makes of 0..7 (as measured), not a plain 36x
    private static readonly byte[] Levels = [0, 52, 87, 116, 144, 172, 206, 255];

    /// <summary>The colour of a palette word as ARGB.</summary>
    public static uint Color(ushort word) =>
        0xFF000000u | (uint)(Levels[(word >> 1) & 7] << 16) | (uint)(Levels[(word >> 5) & 7] << 8) | Levels[(word >> 9) & 7];

    /// <summary>The 9-bit word nearest an ARGB colour.</summary>
    public static ushort Word(uint argb)
    {
        static int Nearest(int v) => Enumerable.Range(0, 8).OrderBy(i => Math.Abs(Levels[i] - v)).First();

        int r = Nearest((int)((argb >> 16) & 0xFF)), g = Nearest((int)((argb >> 8) & 0xFF)), b = Nearest((int)(argb & 0xFF));
        return (ushort)((b << 9) | (g << 5) | (r << 1));
    }

    /// <summary>
    ///     Reads palette data (words, big-endian; 32 bytes is one line, 128 all four). Shorter data leaves the rest black, and more is ignored.
    ///     The result always has <see cref="Lines" /> x <see cref="ColorsPerLine" /> entries.
    /// </summary>
    public static uint[] Read(ReadOnlySpan<byte> data, int firstLine = 0)
    {
        var colors = new uint[Lines * ColorsPerLine];
        Array.Fill(colors, 0xFF000000u);
        ReadInto(colors, data, firstLine);
        return colors;
    }

    /// <summary>Puts palette data into <paramref name="colors" /> from the first colour of line <paramref name="firstLine" />, leaving the rest as it was.</summary>
    public static void ReadInto(uint[] colors, ReadOnlySpan<byte> data, int firstLine)
    {
        int at = firstLine * ColorsPerLine;
        for (int i = 0; i + 1 < data.Length && at < colors.Length; i += 2, at++)
            colors[at] = Color((ushort)((data[i] << 8) | data[i + 1]));
    }

    /// <summary>A grey ramp for each line: what to draw with when no palette has been opened.</summary>
    public static uint[] Greys()
    {
        var colors = new uint[Lines * ColorsPerLine];
        for (int i = 0; i < colors.Length; i++)
        {
            uint v = (uint)(i % ColorsPerLine * 255 / (ColorsPerLine - 1));
            colors[i] = 0xFF000000u | (v << 16) | (v << 8) | v;
        }

        return colors;
    }
}

/// <summary>Tiles of 8x8 pixels at 4 bits a pixel, 32 bytes each, the left pixel in the high nibble of its byte.</summary>
public sealed class TileSheet(byte[] data)
{
    public const int TileBytes = 32;

    public byte[] Data { get; } = data;

    public int Count => Data.Length / TileBytes;

    /// <summary>The palette index (0-15) of a pixel; 0 for a tile that is not there.</summary>
    public int Pixel(int tile, int x, int y)
    {
        int at = tile * TileBytes + y * 4 + (x >> 1);
        if (tile < 0 || at >= Data.Length) return 0;

        return (x & 1) == 0 ? Data[at] >> 4 : Data[at] & 0xF;
    }
}

/// <summary>One request of a DPLC (dynamic pattern load cue): <see cref="Tiles" /> tiles of art starting at tile <see cref="Offset" /> of the art.</summary>
public sealed record DplcRequest(int Tiles, int Offset);

/// <summary>A hardware sprite of a frame: where its top left is, its size in tiles (1-4 each way) and what it shows.</summary>
public sealed class SpritePiece
{
    public int X { get; set; }

    public int Y { get; set; }

    /// <summary>1-4 tiles.</summary>
    public int Width { get; set; } = 1;

    /// <summary>1-4 tiles.</summary>
    public int Height { get; set; } = 1;

    /// <summary>The tile it starts at (in VRAM terms: with a DPLC, a slot the DPLC fills).</summary>
    public int Tile { get; set; }

    /// <summary>The palette line, 0-3.</summary>
    public int Palette { get; set; }

    public bool XFlip { get; set; }

    public bool YFlip { get; set; }

    public bool Priority { get; set; }

    public int TileCount => Width * Height;

    public int PixelWidth => Width * 8;

    public int PixelHeight => Height * 8;

    public SpritePiece Clone() => (SpritePiece)MemberwiseClone();
}

/// <summary>What a frame looks like drawn: the pixels (ARGB, 0 for transparent) and where their top left is relative to the object's origin.</summary>
public sealed record RenderedFrame(int Left, int Top, int Width, int Height, uint[] Pixels)
{
    public static RenderedFrame Empty { get; } = new(0, 0, 0, 0, []);
}

/// <summary>How a frame loads on the VDP: the total and the busiest scanline, against what the hardware can show.</summary>
/// <param name="Pieces">How many sprites the frame uses (the VDP shows 80 at most in all, 64 in a 256-pixel-wide mode).</param>
/// <param name="MaxPerLine">The most sprites that share one scanline (the VDP draws 20 a line, 16 in a 256-pixel-wide mode).</param>
/// <param name="BusiestLine">A scanline (relative to the origin) where that happens.</param>
/// <param name="Tiles">How many tiles it uses.</param>
public sealed record SpriteStats(int Pieces, int MaxPerLine, int BusiestLine, int Tiles)
{
    public const int MaxSprites = 80;

    public const int MaxSpritesPerLine = 20;

    public bool OverLimit => Pieces > MaxSprites || MaxPerLine > MaxSpritesPerLine;
}

/// <summary>Draws sprite pieces the way the VDP does.</summary>
public static class SpriteComposer
{
    /// <summary>
    ///     A piece's tiles are in columns: down the first column, then down the next. A flip mirrors the whole piece (the order of the columns or
    ///     rows) and every tile in it. Returns, for a pixel (x, y) of the piece, the tile (as the piece's tile number plus its offset) and the
    ///     pixel of that tile.
    /// </summary>
    public static (int TileOffset, int X, int Y) Locate(SpritePiece piece, int x, int y)
    {
        if (piece.XFlip) x = piece.PixelWidth - 1 - x;
        if (piece.YFlip) y = piece.PixelHeight - 1 - y;

        int column = x >> 3, row = y >> 3;
        return (column * piece.Height + row, x & 7, y & 7);
    }

    /// <summary>
    ///     Draws the pieces, the first on top (as the VDP does: the first sprite wins where they overlap; the priority bit only says how a sprite
    ///     stands against the background planes, which are not drawn here). <paramref name="artTile" /> turns a tile number of a piece into a tile of <paramref name="art" /> (a DPLC does this; null
    ///     for the tile number as it is).
    /// </summary>
    public static RenderedFrame Render(IReadOnlyList<SpritePiece> pieces, TileSheet art, uint[] palette, Func<int, int>? artTile = null)
    {
        if (pieces.Count == 0) return RenderedFrame.Empty;

        int left   = pieces.Min(p => p.X), top = pieces.Min(p => p.Y);
        int right  = pieces.Max(p => p.X + p.PixelWidth), bottom = pieces.Max(p => p.Y + p.PixelHeight);
        int width  = right - left, height = bottom - top;
        var pixels = new uint[width * height];
        var depth  = new byte[width * height]; // 1 where a piece has drawn

        // drawn in the order of the list, a later piece only where the earlier ones left nothing
        foreach (var piece in pieces)
        {
            for (int y = 0; y < piece.PixelHeight; y++)
            for (int x = 0; x < piece.PixelWidth; x++)
            {
                var (offset, tx, ty) = Locate(piece, x, y);
                int tile  = piece.Tile + offset;
                int index = art.Pixel(artTile?.Invoke(tile) ?? tile, tx, ty);
                if (index == 0) continue;

                int at = (piece.Y + y - top) * width + piece.X + x - left;
                if (depth[at] != 0) continue;

                depth[at]  = 1;
                pixels[at] = palette[(piece.Palette & 3) * MdPalette.ColorsPerLine + index];
            }
        }

        return new RenderedFrame(left, top, width, height, pixels);
    }

    /// <summary>Counts how the pieces load the VDP.</summary>
    public static SpriteStats Stats(IReadOnlyList<SpritePiece> pieces)
    {
        if (pieces.Count == 0) return new SpriteStats(0, 0, 0, 0);

        int best = 0, bestLine = 0;
        for (int line = pieces.Min(p => p.Y); line < pieces.Max(p => p.Y + p.PixelHeight); line++)
        {
            int n = pieces.Count(p => line >= p.Y && line < p.Y + p.PixelHeight);
            if (n > best)
            {
                best     = n;
                bestLine = line;
            }
        }

        return new SpriteStats(pieces.Count, best, bestLine, pieces.Sum(p => p.TileCount));
    }

    /// <summary>
    ///     What a DPLC does: slot n of the object's VRAM (counting from the first tile of its art) holds the tile of the art that is the
    ///     n-th one the requests ask for, in order.
    /// </summary>
    public static int[] ExpandDplc(IEnumerable<DplcRequest> requests)
    {
        var slots = new List<int>();
        foreach (var r in requests)
            for (int i = 0; i < r.Tiles; i++)
                slots.Add(r.Offset + i);

        return [.. slots];
    }

    /// <summary>A function for <see cref="Render" /> from the slots <see cref="ExpandDplc" /> made: a tile no request fills draws as nothing.</summary>
    public static Func<int, int> ThroughDplc(int[] slots) => tile => tile >= 0 && tile < slots.Length ? slots[tile] : -1;
}

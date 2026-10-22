using System.IO;
using System.IO.Compression;
using System.Text;

namespace Silo.Labs.Qr;

/// <summary>Colors for drawing a code, as bytes (the dark modules, the light ones and the quiet zone, which is light).</summary>
public readonly record struct QrColors(byte DarkR, byte DarkG, byte DarkB, byte LightR, byte LightG, byte LightB)
{
    public static QrColors BlackOnWhite => new(0, 0, 0, 255, 255, 255);

    public string DarkHex  => $"#{DarkR:X2}{DarkG:X2}{DarkB:X2}";
    public string LightHex => $"#{LightR:X2}{LightG:X2}{LightB:X2}";

    /// <summary>Reads #RGB or #RRGGBB.</summary>
    public static bool TryParseHex(string? text, out (byte R, byte G, byte B) color)
    {
        color = default;
        text  = text?.Trim().TrimStart('#');
        if (string.IsNullOrEmpty(text)) return false;
        if (text.Length == 3) text = string.Concat(text.Select(c => $"{c}{c}"));
        if (text.Length != 6 || !text.All(Uri.IsHexDigit)) return false;
        color = (Convert.ToByte(text[..2], 16), Convert.ToByte(text[2..4], 16), Convert.ToByte(text[4..], 16));
        return true;
    }

    public static QrColors From(string? dark, string? light)
    {
        var colors = BlackOnWhite;
        if (TryParseHex(dark, out var d)) colors = colors with { DarkR = d.R, DarkG = d.G, DarkB = d.B };
        if (TryParseHex(light, out var l)) colors = colors with { LightR = l.R, LightG = l.G, LightB = l.B };
        return colors;
    }

    /// <summary>How far apart the two colors are in brightness (0 to 1). A code needs about 0.4 to be read.</summary>
    public double Contrast
    {
        get
        {
            static double Lum(byte r, byte g, byte b) => (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
            return Math.Abs(Lum(LightR, LightG, LightB) - Lum(DarkR, DarkG, DarkB));
        }
    }

    /// <summary>Whether the dark modules are lighter than the light ones, which some readers will not have.</summary>
    public bool IsInverted
    {
        get
        {
            static double Lum(byte r, byte g, byte b) => 0.2126 * r + 0.7152 * g + 0.0722 * b;
            return Lum(DarkR, DarkG, DarkB) > Lum(LightR, LightG, LightB);
        }
    }
}

/// <summary>Ways to put a code on paper, a screen or a page of text.</summary>
public static class QrRender
{
    /// <summary>A vector picture: one path of squares, with the quiet zone <paramref name="border" /> modules wide.</summary>
    public static string Svg(QrCode code, int border = 4, QrColors? colors = null)
    {
        var c    = colors ?? QrColors.BlackOnWhite;
        int full = code.Size + border * 2;
        var sb   = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" version=\"1.1\" viewBox=\"0 0 {full} {full}\" stroke=\"none\" shape-rendering=\"crispEdges\">\n");
        sb.Append($"\t<rect width=\"100%\" height=\"100%\" fill=\"{c.LightHex}\"/>\n");
        sb.Append("\t<path d=\"");
        bool first = true;
        for (int y = 0; y < code.Size; y++)
        {
            // a row's runs of dark modules each become one wide square, to keep the path short
            int x = 0;
            while (x < code.Size)
            {
                if (!code[x, y]) { x++; continue; }
                int start = x;
                while (x < code.Size && code[x, y]) x++;
                if (!first) sb.Append(' ');
                first = false;
                sb.Append($"M{start + border},{y + border}h{x - start}v1h-{x - start}z");
            }
        }

        sb.Append($"\" fill=\"{c.DarkHex}\"/>\n</svg>\n");
        return sb.ToString();
    }

    /// <summary>
    ///     Each module's brightness as a picture of bytes (0 dark, 255 light) <paramref name="scale" /> pixels to a module, with the quiet zone; the picture is as wide and as high as the
    ///     returned size.
    /// </summary>
    public static (byte[] Gray, int Size) Gray(QrCode code, int scale = 1, int border = 4)
    {
        scale = Math.Max(1, scale);
        int size = (code.Size + border * 2) * scale;
        var gray = new byte[size * size];
        Array.Fill(gray, (byte)255);
        for (int y = 0; y < code.Size; y++)
        for (int x = 0; x < code.Size; x++)
        {
            if (!code[x, y]) continue;
            for (int dy = 0; dy < scale; dy++)
            {
                int row = ((y + border) * scale + dy) * size + (x + border) * scale;
                for (int dx = 0; dx < scale; dx++) gray[row + dx] = 0;
            }
        }

        return (gray, size);
    }

    /// <summary>
    ///     Pixels (blue, green, red, alpha, which is what a WPF bitmap wants) of a grid of modules at <paramref name="scale" /> pixels to a module, with the quiet zone. The size is the width
    ///     and the height.
    /// </summary>
    public static (byte[] Bgra, int Size) Pixels(bool[,] modules, int scale, int border, QrColors? colors = null)
    {
        var c    = colors ?? QrColors.BlackOnWhite;
        int n    = modules.GetLength(0);
        scale    = Math.Max(1, scale);
        int size = (n + border * 2) * scale;
        var data = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int mx   = x / scale - border, my = y / scale - border;
            bool on  = mx >= 0 && my >= 0 && mx < n && my < n && modules[my, mx];
            int at   = (y * size + x) * 4;
            data[at]     = on ? c.DarkB : c.LightB;
            data[at + 1] = on ? c.DarkG : c.LightG;
            data[at + 2] = on ? c.DarkR : c.LightR;
            data[at + 3] = 255;
        }

        return (data, size);
    }

    /// <summary>A PNG file of the code (RGB, no extras), written here so that nothing outside the framework is needed.</summary>
    public static byte[] Png(QrCode code, int scale = 8, int border = 4, QrColors? colors = null)
    {
        var c    = colors ?? QrColors.BlackOnWhite;
        scale    = Math.Clamp(scale, 1, 64);
        int size = (code.Size + border * 2) * scale;

        // one scan line: a filter byte (0, none), then RGB for each pixel
        var raw = new byte[size * (1 + size * 3)];
        for (int y = 0; y < size; y++)
        {
            int at = y * (1 + size * 3);
            raw[at++] = 0;
            int my = y / scale - border;
            for (int x = 0; x < size; x++)
            {
                int mx  = x / scale - border;
                bool on = mx >= 0 && my >= 0 && mx < code.Size && my < code.Size && code[mx, my];
                raw[at++] = on ? c.DarkR : c.LightR;
                raw[at++] = on ? c.DarkG : c.LightG;
                raw[at++] = on ? c.DarkB : c.LightB;
            }
        }

        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        WriteInt(header, 0, size);
        WriteInt(header, 4, size);
        header[8] = 8; // bits to a sample
        header[9] = 2; // color type: RGB
        Chunk(output, "IHDR", header);

        using (var compressed = new MemoryStream())
        {
            using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, true)) z.Write(raw);
            Chunk(output, "IDAT", compressed.ToArray());
        }

        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    static void WriteInt(byte[] to, int at, int value)
    {
        to[at]     = (byte)(value >> 24);
        to[at + 1] = (byte)(value >> 16);
        to[at + 2] = (byte)(value >> 8);
        to[at + 3] = (byte)value;
    }

    static void Chunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4];
        WriteInt(length, 0, data.Length);
        output.Write(length);

        var body = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type, 0, 4, body, 0);
        data.CopyTo(body, 4);
        output.Write(body);

        var crc = new byte[4];
        WriteInt(crc, 0, unchecked((int)Crc32(body)));
        output.Write(crc);
    }

    static uint[]? _crcTable;

    /// <summary>The checksum PNG uses for its chunks.</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var table = _crcTable;
        if (table is null)
        {
            table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }

            _crcTable = table;
        }

        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data) crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>
    ///     The code as text two rows of modules to a line (using half blocks), which fits in a terminal or a document; with <paramref name="invert" /> the dark modules are the empty ones,
    ///     which is right for a light-on-dark terminal.
    /// </summary>
    public static string Blocks(QrCode code, int border = 2, bool invert = false)
    {
        var sb = new StringBuilder();
        int lo = -border, hi = code.Size + border;
        bool Dark(int x, int y) => (x >= 0 && y >= 0 && x < code.Size && y < code.Size && code[x, y]) != invert;
        for (int y = lo; y < hi; y += 2)
        {
            for (int x = lo; x < hi; x++)
            {
                bool top = Dark(x, y), bottom = Dark(x, y + 1);
                sb.Append(top ? bottom ? '█' : '▀' : bottom ? '▄' : ' ');
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>The code as lines of the two characters given (dark first), with the quiet zone.</summary>
    public static string Matrix(QrCode code, int border = 0, char dark = '1', char light = '0')
    {
        var sb = new StringBuilder();
        for (int y = -border; y < code.Size + border; y++)
        {
            for (int x = -border; x < code.Size + border; x++) sb.Append(code[x, y] ? dark : light);
            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Reads a grid back from lines of text: <c>1</c>, <c>#</c>, <c>X</c>, <c>█</c> and <c>■</c> are dark, anything else a light module. The block characters made by
    ///     <see cref="Blocks" /> are read as well. Fails if the lines are not a square.
    /// </summary>
    public static bool TryParseMatrix(string text, out bool[,] modules, out string? problem)
    {
        modules = new bool[0, 0];
        problem = null;
        var lines = text.Replace("\r", "").Split('\n').Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (lines.Count == 0) { problem = "There is nothing there."; return false; }

        bool half = lines.Any(l => l.Any(c => c is '█' or '▀' or '▄'));
        var rows  = new List<bool[]>();
        int width = lines.Max(l => l.Length);
        foreach (var line in lines)
        {
            var top    = new bool[width];
            var bottom = new bool[width];
            for (int x = 0; x < line.Length; x++)
            {
                char c = line[x];
                if (half)
                {
                    top[x]    = c is '█' or '▀';
                    bottom[x] = c is '█' or '▄';
                }
                else
                    top[x] = c is '1' or '#' or 'X' or 'x' or '█' or '■';
            }

            rows.Add(top);
            if (half) rows.Add(bottom);
        }

        // trim the quiet zone, which is all light rows and columns around the edge
        int minX = width, maxX = -1, minY = rows.Count, maxY = -1;
        for (int y = 0; y < rows.Count; y++)
        for (int x = 0; x < width; x++)
            if (rows[y][x])
            {
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }

        if (maxX < 0) { problem = "There are no dark modules."; return false; }
        int w = maxX - minX + 1, h = maxY - minY + 1;
        if (w != h) { problem = $"The dark modules fill {w} by {h}, which is not a square."; return false; }
        if (w < 21 || (w - 21) % 4 != 0 || w > 177) { problem = $"A code is 21 modules across, or 25, 29 and so on up to 177; this is {w}."; return false; }

        modules = new bool[h, w];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            modules[y, x] = rows[minY + y][minX + x];
        return true;
    }
}

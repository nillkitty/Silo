using System.Text;

namespace Silo.Labs.Qr;

/// <summary>One segment of the data a code was read to hold.</summary>
public sealed record QrSegmentInfo(QrMode Mode, string Text, int Characters);

/// <summary>What was read from a code.</summary>
/// <param name="Text">All of the text.</param>
/// <param name="Bytes">The bytes of the data, as they were in the segments.</param>
/// <param name="Corrected">How many codewords were wrong and put right.</param>
/// <param name="FormatErrors">How many of the 15 bits of format information were wrong and put right.</param>
/// <param name="BlockCorrections">How many codewords were put right in each block.</param>
public sealed record QrDecoded(string Text, byte[] Bytes, int Version, QrEcc Ecc, int Mask, IReadOnlyList<QrSegmentInfo> Segments, int Corrected, int FormatErrors, IReadOnlyList<int> BlockCorrections);

/// <summary>What reading a code came to: what was read, or why it could not be.</summary>
public sealed record QrDecodeResult(QrDecoded? Value, string? Error)
{
    public bool IsOk => Value is not null;
}

/// <summary>
///     Reads a clean code: a grid of modules (as the encoder makes it, or taken from a picture that is straight on and has no more than a faint blur). There is no search for the
///     code in a photo, no turning and no perspective; what it does do is put right the codewords that are wrong, as far as the code's level of correction allows.
/// </summary>
public static class QrDecoder
{
    public static QrDecodeResult Decode(bool[,] modules)
    {
        int size = modules.GetLength(0);
        if (modules.GetLength(1) != size || size < 21 || size > 177 || (size - 17) % 4 != 0) return new QrDecodeResult(null, $"A grid of {modules.GetLength(1)} by {size} modules is not the size of a code (21 to 177, in steps of 4).");

        int version = (size - 17) / 4;
        bool Get(int x, int y) => modules[y, x];

        // the format information is there twice; it is the one that is nearest to a valid one
        int first = 0, second = 0;
        for (int i = 0; i <= 5; i++) first |= (Get(8, i) ? 1 : 0) << i;
        first |= (Get(8, 7) ? 1 : 0) << 6;
        first |= (Get(8, 8) ? 1 : 0) << 7;
        first |= (Get(7, 8) ? 1 : 0) << 8;
        for (int i = 9; i < 15; i++) first |= (Get(14 - i, 8) ? 1 : 0) << i;
        for (int i = 0; i < 8; i++) second |= (Get(size - 1 - i, 8) ? 1 : 0) << i;
        for (int i = 8; i < 15; i++) second |= (Get(8, size - 15 + i) ? 1 : 0) << i;

        int bestDistance = int.MaxValue;
        QrEcc ecc = QrEcc.Low;
        int mask = 0;
        foreach (var level in new[] { QrEcc.Low, QrEcc.Medium, QrEcc.Quartile, QrEcc.High })
        for (int m = 0; m < 8; m++)
        {
            int valid = QrCode.FormatBits(level, m);
            int d = Math.Min(Bits(valid ^ first), Bits(valid ^ second));
            if (d < bestDistance)
            {
                bestDistance = d;
                ecc          = level;
                mask         = m;
            }
        }

        if (bestDistance > 3) return new QrDecodeResult(null, "The format information cannot be read: too much of it is wrong, so this is not a code, or it is damaged where it matters most.");

        var isFunction = QrCode.FunctionModules(version);
        var data       = (bool[,])modules.Clone();
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
            if (!isFunction[y, x] && QrCode.MaskFlips(mask, x, y))
                data[y, x] = !data[y, x];

        // the codewords, in the order they were put in
        int raw = QrCode.RawModules(version) / 8;
        var all = new byte[raw];
        int bit = 0;
        foreach (var (x, y) in QrCode.Order(size))
        {
            if (isFunction[y, x]) continue;
            if (bit >= raw * 8) break;

            if (data[y, x]) all[bit >> 3] |= (byte)(0x80 >> (bit & 7));
            bit++;
        }

        // take the blocks apart again
        int blocks      = QrCode.BlockCount(version, ecc);
        int blockEcc    = QrCode.EccCodewordsPerBlock(version, ecc);
        int shortBlocks = blocks - raw % blocks;
        int shortLength = raw / blocks;
        var parts       = new List<byte>[blocks];
        for (int j = 0; j < blocks; j++) parts[j] = [];

        int at = 0;
        for (int i = 0; i < shortLength + 1; i++)
        for (int j = 0; j < blocks; j++)
            if (i != shortLength - blockEcc || j >= shortBlocks)
                parts[j].Add(all[at++]);

        var corrections = new List<int>();
        var message     = new List<byte>();
        for (int j = 0; j < blocks; j++)
        {
            var block = parts[j].ToArray();
            int fixedCount = QrReedSolomon.Correct(block, blockEcc);
            if (fixedCount < 0) return new QrDecodeResult(null, $"Block {j + 1} of {blocks} has more wrong codewords than the code's correction ({ecc}, up to {blockEcc / 2} in a block) can put right.");

            corrections.Add(fixedCount);
            message.AddRange(block.Take(block.Length - blockEcc));
        }

        return Parse(message.ToArray(), version, ecc, mask, corrections, bestDistance);
    }

    private static int Bits(int value)
    {
        int n = 0;
        for (; value != 0; value &= value - 1) n++;
        return n;
    }

    private static QrDecodeResult Parse(byte[] data, int version, QrEcc ecc, int mask, List<int> corrections, int formatErrors)
    {
        int pos = 0;
        int Take(int count)
        {
            int v = 0;
            for (int i = 0; i < count; i++, pos++)
            {
                if (pos >= data.Length * 8) throw new FormatException("The data ends in the middle of a segment.");

                v = v << 1 | (data[pos >> 3] >> (7 - (pos & 7)) & 1);
            }

            return v;
        }

        var segments = new List<QrSegmentInfo>();
        var text     = new StringBuilder();
        var bytes    = new List<byte>();
        int eci      = -1;
        try
        {
            while (pos + 4 <= data.Length * 8)
            {
                int mode = Take(4);
                if (mode == 0) break;

                switch (mode)
                {
                    case 1:
                    {
                        int n = Take(QrSegment.CountBits(QrMode.Numeric, version));
                        var s = new StringBuilder();
                        for (; n >= 3; n -= 3) s.Append(Take(10).ToString("000"));
                        if (n == 2) s.Append(Take(7).ToString("00"));
                        else if (n == 1) s.Append(Take(4).ToString("0"));
                        Add(QrMode.Numeric, s.ToString(), s.Length);
                        break;
                    }
                    case 2:
                    {
                        int n = Take(QrSegment.CountBits(QrMode.Alphanumeric, version));
                        var s = new StringBuilder();
                        for (; n >= 2; n -= 2)
                        {
                            int v = Take(11);
                            if (v >= 45 * 45) throw new FormatException("An alphanumeric pair is out of range.");
                            s.Append(QrSegment.AlphanumericCharset[v / 45]).Append(QrSegment.AlphanumericCharset[v % 45]);
                        }

                        if (n == 1)
                        {
                            int v = Take(6);
                            if (v >= 45) throw new FormatException("An alphanumeric character is out of range.");
                            s.Append(QrSegment.AlphanumericCharset[v]);
                        }

                        Add(QrMode.Alphanumeric, s.ToString(), s.Length);
                        break;
                    }
                    case 4:
                    {
                        int n = Take(QrSegment.CountBits(QrMode.Byte, version));
                        var raw = new byte[n];
                        for (int i = 0; i < n; i++) raw[i] = (byte)Take(8);
                        bytes.AddRange(raw);
                        Add(QrMode.Byte, DecodeBytes(raw, eci), n);
                        break;
                    }
                    case 7:
                    {
                        eci = Take(1) == 0 ? Take(7) : Take(1) == 0 ? Take(14) : Take(21);
                        break;
                    }
                    default:
                        return new QrDecodeResult(null, mode == 8 ? "The data has a Kanji segment, which this reader does not read." : $"The data has a segment of a mode this reader does not read (mode {mode}).");
                }
            }
        }
        catch (FormatException ex)
        {
            return new QrDecodeResult(null, ex.Message);
        }

        var allBytes = segments.Count == 0 ? [] : Encoding.UTF8.GetBytes(text.ToString());
        return new QrDecodeResult(new QrDecoded(text.ToString(), bytes.Count > 0 && segments.All(s => s.Mode == QrMode.Byte) ? bytes.ToArray() : allBytes, version, ecc, mask, segments, corrections.Sum(), formatErrors, corrections), null);

        void Add(QrMode mode, string value, int characters)
        {
            segments.Add(new QrSegmentInfo(mode, value, characters));
            text.Append(value);
        }
    }

    /// <summary>
    ///     Reads bytes as text: in the character set the code says (ECI 26 is UTF-8, 3 and 1 are ISO 8859-1), otherwise as UTF-8 if they are valid UTF-8 and as ISO 8859-1 if not
    ///     (which is what readers do, since writers of codes seldom say).
    /// </summary>
    public static string DecodeBytes(byte[] raw, int eci = -1)
    {
        if (eci is 1 or 3) return Encoding.Latin1.GetString(raw);
        if (eci == 26) return Encoding.UTF8.GetString(raw);

        try
        {
            return new UTF8Encoding(false, true).GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(raw);
        }
    }

    // ---- from a picture ---------------------------------------------------------------------

    /// <summary>
    ///     Reads a code from a picture of gray levels (0 dark, 255 light, a row after row). The picture has to show the code straight on and all of it, with any border around it (the
    ///     search is for the box that the dark pixels make), at any size from 2 pixels to a module up; one that is light on dark is tried when dark on light has failed.
    /// </summary>
    public static QrDecodeResult DecodeImage(byte[] gray, int width, int height)
    {
        if (width * height != gray.Length || width < 21 || height < 21) return new QrDecodeResult(null, "The picture is too small to hold a code.");

        QrDecodeResult? last = null;
        foreach (bool inverted in new[] { false, true })
        {
            int threshold = Threshold(gray);
            bool Dark(int x, int y) => inverted ? gray[y * width + x] > threshold : gray[y * width + x] <= threshold;

            int x0 = width, y0 = height, x1 = -1, y1 = -1;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (Dark(x, y))
                {
                    x0 = Math.Min(x0, x);
                    x1 = Math.Max(x1, x);
                    y0 = Math.Min(y0, y);
                    y1 = Math.Max(y1, y);
                }

            if (x1 < 0) continue;

            double w = x1 - x0 + 1, h = y1 - y0 + 1;
            for (int n = 21; n <= 177; n += 4)
            {
                double mx = w / n, my = h / n;
                if (mx < 2 || my < 2 || Math.Abs(mx - my) > Math.Max(mx, my) * 0.25) continue;

                var grid = new bool[n, n];
                for (int r = 0; r < n; r++)
                for (int c = 0; c < n; c++)
                {
                    // the middle of the module, a few pixels of it
                    double cx = x0 + (c + 0.5) * mx, cy = y0 + (r + 0.5) * my;
                    int dark = 0, total = 0;
                    int radius = (int)Math.Max(0, Math.Min(mx, my) / 4);
                    for (int dy = -radius; dy <= radius; dy++)
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int px = (int)cx + dx, py = (int)cy + dy;
                        if (px < 0 || py < 0 || px >= width || py >= height) continue;

                        total++;
                        if (Dark(px, py)) dark++;
                    }

                    grid[r, c] = total > 0 && dark * 2 > total;
                }

                if (!LooksLikeFinders(grid)) continue;

                var result = Decode(grid);
                if (result.IsOk) return result;

                last = result;
            }
        }

        return last ?? new QrDecodeResult(null, "No code was found in the picture (it has to be straight on, and all of it in view).");
    }

    private static bool LooksLikeFinders(bool[,] grid)
    {
        int n = grid.GetLength(0);

        bool Finder(int ox, int oy)
        {
            // the 7 by 7 pattern: dark ring, light ring, dark 3 by 3 in the middle (allowing a couple of modules to be off)
            int wrong = 0;
            for (int dy = 0; dy < 7; dy++)
            for (int dx = 0; dx < 7; dx++)
            {
                bool expected = Math.Max(Math.Abs(dx - 3), Math.Abs(dy - 3)) != 2;
                if (grid[oy + dy, ox + dx] != expected) wrong++;
            }

            return wrong <= 6;
        }

        return Finder(0, 0) && Finder(n - 7, 0) && Finder(0, n - 7);
    }

    /// <summary>The level that tells dark from light (Otsu's: the one that sets the two groups of pixels furthest apart).</summary>
    private static int Threshold(byte[] gray)
    {
        var histogram = new int[256];
        foreach (byte g in gray) histogram[g]++;

        long total = gray.Length, sum = 0;
        for (int i = 0; i < 256; i++) sum += (long)i * histogram[i];

        long weightBackground = 0, sumBackground = 0;
        double best = -1;
        int threshold = 127;
        for (int t = 0; t < 256; t++)
        {
            weightBackground += histogram[t];
            if (weightBackground == 0) continue;

            long weightForeground = total - weightBackground;
            if (weightForeground == 0) break;

            sumBackground += (long)t * histogram[t];
            double meanBackground = (double)sumBackground / weightBackground;
            double meanForeground = (double)(sum - sumBackground) / weightForeground;
            double between = (double)weightBackground * weightForeground * (meanBackground - meanForeground) * (meanBackground - meanForeground);
            if (between > best)
            {
                best      = between;
                threshold = t;
            }
        }

        return threshold;
    }
}

/// <summary>Damages a code to see how much of it can be lost: modules are turned over in a number of codewords.</summary>
public static class QrDamage
{
    /// <summary>
    ///     Turns over some of the modules (one to eight) of <paramref name="codewords" /> different codewords, chosen at random from the data and the check codewords of the code,
    ///     in a copy of the grid. Returns the copy and which codewords were hit (numbered from 0 in the order they are in the code, which interleaves the blocks).
    /// </summary>
    public static (bool[,] Modules, IReadOnlyList<int> Hit) Damage(QrCode code, int codewords, Random random)
    {
        var places = QrCode.DataPlaces(code.Version);
        int total  = QrCode.RawModules(code.Version) / 8;
        codewords  = Math.Clamp(codewords, 0, total);

        var modules = code.ToMatrix();
        var hit     = Enumerable.Range(0, total).OrderBy(_ => random.Next()).Take(codewords).OrderBy(i => i).ToList();
        foreach (int cw in hit)
        {
            // the modules of a codeword are eight in a row in the order the places go
            var bits = Enumerable.Range(0, 8).OrderBy(_ => random.Next()).Take(random.Next(1, 9));
            foreach (int b in bits)
            {
                var (x, y) = places[cw * 8 + b];
                modules[y, x] = !modules[y, x];
            }
        }

        return (modules, hit);
    }

    /// <summary>For each codeword (in the order of the code), the block it belongs to (from 0).</summary>
    public static IReadOnlyList<int> BlockOfCodewords(int version, QrEcc ecc)
    {
        int blocks      = QrCode.BlockCount(version, ecc);
        int blockEcc    = QrCode.EccCodewordsPerBlock(version, ecc);
        int raw         = QrCode.RawModules(version) / 8;
        int shortBlocks = blocks - raw % blocks;
        int shortLength = raw / blocks;
        var result      = new List<int>();
        for (int i = 0; i < shortLength + 1; i++)
        for (int j = 0; j < blocks; j++)
            if (i != shortLength - blockEcc || j >= shortBlocks)
                result.Add(j);
        return result;
    }
}

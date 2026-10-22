using System.Text;

namespace Silo.Labs.Qr;

/// <summary>How much of the code can be wrong and still be read.</summary>
public enum QrEcc
{
    /// <summary>About 7% of the codewords.</summary>
    Low = 0,

    /// <summary>About 15%.</summary>
    Medium = 1,

    /// <summary>About 25%.</summary>
    Quartile = 2,

    /// <summary>About 30%.</summary>
    High = 3
}

/// <summary>How the characters of a segment are packed in bits.</summary>
public enum QrMode
{
    Numeric = 1,
    Alphanumeric = 2,
    Byte = 4,
    Eci = 7
}

/// <summary>What a module of the code is for.</summary>
public enum QrRole
{
    Data,
    Ecc,
    Remainder,
    Finder,
    Separator,
    Timing,
    Alignment,
    Format,
    Version,
    DarkModule
}

/// <summary>Bits, added a few at a time.</summary>
public sealed class QrBits
{
    private readonly List<bool> _bits = [];

    public int Length => _bits.Count;

    public bool this[int index] => _bits[index];

    /// <summary>Adds the low <paramref name="count" /> bits of <paramref name="value" />, the high one first.</summary>
    public void Append(int value, int count)
    {
        if (count is < 0 or > 31 || value >> count != 0) throw new ArgumentOutOfRangeException(nameof(value));

        for (int i = count - 1; i >= 0; i--) _bits.Add((value >> i & 1) != 0);
    }

    public void Append(QrBits other) => _bits.AddRange(other._bits);

    public byte[] ToBytes()
    {
        var bytes = new byte[(_bits.Count + 7) / 8];
        for (int i = 0; i < _bits.Count; i++)
            if (_bits[i])
                bytes[i >> 3] |= (byte)(0x80 >> (i & 7));
        return bytes;
    }
}

/// <summary>A run of the text packed in one mode.</summary>
public sealed class QrSegment
{
    public const string AlphanumericCharset = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    private QrSegment(QrMode mode, int characters, QrBits data)
    {
        Mode       = mode;
        Characters = characters;
        Data       = data;
    }

    public QrMode Mode { get; }

    /// <summary>How many characters (bytes, digits...) the segment has: what its header counts.</summary>
    public int Characters { get; }

    public QrBits Data { get; }

    public static bool IsNumeric(string text) => text.All(c => c is >= '0' and <= '9');

    public static bool IsAlphanumeric(string text) => text.All(c => AlphanumericCharset.Contains(c));

    public static QrSegment FromBytes(ReadOnlySpan<byte> data)
    {
        var bits = new QrBits();
        foreach (byte b in data) bits.Append(b, 8);
        return new QrSegment(QrMode.Byte, data.Length, bits);
    }

    public static QrSegment FromNumeric(string digits)
    {
        if (!IsNumeric(digits)) throw new ArgumentException("A numeric segment has digits only.", nameof(digits));

        var bits = new QrBits();
        for (int i = 0; i < digits.Length;)
        {
            int n = Math.Min(digits.Length - i, 3);
            bits.Append(int.Parse(digits.AsSpan(i, n)), n * 3 + 1);
            i += n;
        }

        return new QrSegment(QrMode.Numeric, digits.Length, bits);
    }

    public static QrSegment FromAlphanumeric(string text)
    {
        if (!IsAlphanumeric(text)) throw new ArgumentException("An alphanumeric segment has the digits, the capital letters and the characters space $ % * + - . / : only.", nameof(text));

        var bits = new QrBits();
        int i;
        for (i = 0; i + 2 <= text.Length; i += 2) bits.Append(AlphanumericCharset.IndexOf(text[i]) * 45 + AlphanumericCharset.IndexOf(text[i + 1]), 11);
        if (i < text.Length) bits.Append(AlphanumericCharset.IndexOf(text[i]), 6);
        return new QrSegment(QrMode.Alphanumeric, text.Length, bits);
    }

    /// <summary>Says which character set the bytes that follow are in (26 is UTF-8).</summary>
    public static QrSegment FromEci(int assignment)
    {
        var bits = new QrBits();
        if (assignment < 0) throw new ArgumentOutOfRangeException(nameof(assignment));
        if (assignment < 1 << 7) bits.Append(assignment, 8);
        else if (assignment < 1 << 14)
        {
            bits.Append(2, 2);
            bits.Append(assignment, 14);
        }
        else if (assignment < 1_000_000)
        {
            bits.Append(6, 3);
            bits.Append(assignment, 21);
        }
        else throw new ArgumentOutOfRangeException(nameof(assignment));

        return new QrSegment(QrMode.Eci, 0, bits);
    }

    /// <summary>The text in the mode that packs it best (digits only: numeric; the alphanumeric set: alphanumeric; otherwise UTF-8 bytes); no segment for no text.</summary>
    public static IReadOnlyList<QrSegment> ForText(string text, QrMode? force = null, bool eci = false)
    {
        if (text.Length == 0) return [];

        var mode = force ?? (IsNumeric(text) ? QrMode.Numeric : IsAlphanumeric(text) ? QrMode.Alphanumeric : QrMode.Byte);
        switch (mode)
        {
            case QrMode.Numeric:
                return [FromNumeric(text)];
            case QrMode.Alphanumeric:
                return [FromAlphanumeric(text)];
            default:
                var bytes = Encoding.UTF8.GetBytes(text);
                return eci ? [FromEci(26), FromBytes(bytes)] : [FromBytes(bytes)];
        }
    }

    /// <summary>How many bits the character count takes, for the version.</summary>
    public static int CountBits(QrMode mode, int version)
    {
        int range = version <= 9 ? 0 : version <= 26 ? 1 : 2;
        return mode switch
               {
                   QrMode.Numeric      => new[] { 10, 12, 14 }[range],
                   QrMode.Alphanumeric => new[] { 9, 11, 13 }[range],
                   QrMode.Byte         => new[] { 8, 16, 16 }[range],
                   _                   => 0
               };
    }

    /// <summary>How many bits the segments take in a code of that version, or -1 when a count does not fit its header.</summary>
    public static long TotalBits(IEnumerable<QrSegment> segments, int version)
    {
        long total = 0;
        foreach (var s in segments)
        {
            int cc = CountBits(s.Mode, version);
            if (s.Mode != QrMode.Eci && s.Characters >= 1 << cc) return -1;

            total += 4 + cc + s.Data.Length;
        }

        return total;
    }
}

/// <summary>Nothing fits: the data is too long for the version and the level of correction asked for.</summary>
public sealed class QrCapacityException(string message) : Exception(message);

/// <summary>A QR code: the grid of modules, and what the encoder chose to make it (version, level, mask).</summary>
public sealed class QrCode
{
    public const int MinVersion = 1;
    public const int MaxVersion = 40;

    private static readonly sbyte[][] EccPerBlock =
    [
        [-1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
        [-1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28],
        [-1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
        [-1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30]
    ];

    private static readonly sbyte[][] Blocks =
    [
        [-1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25],
        [-1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49],
        [-1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68],
        [-1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81]
    ];

    private readonly bool[,]   _modules;
    private readonly QrRole[,] _roles;

    private QrCode(int version, QrEcc ecc, int mask, bool[,] modules, QrRole[,] roles, int dataBits, IReadOnlyList<int> penalties)
    {
        Version      = version;
        Ecc          = ecc;
        Mask         = mask;
        Size         = version * 4 + 17;
        _modules     = modules;
        _roles       = roles;
        DataBits     = dataBits;
        Penalties    = penalties;
        CapacityBits = DataCodewords(version, ecc) * 8;
    }

    public int Version { get; }

    public int Size { get; }

    public QrEcc Ecc { get; }

    /// <summary>The mask pattern (0 to 7) the modules of data were turned with.</summary>
    public int Mask { get; }

    /// <summary>How many bits the data (with its headers) take, before padding.</summary>
    public int DataBits { get; }

    /// <summary>How many bits of data the version holds at this level of correction.</summary>
    public int CapacityBits { get; }

    /// <summary>The penalty score each of the eight masks got (the encoder takes the lowest); only when the mask was chosen by the encoder, else just the one used.</summary>
    public IReadOnlyList<int> Penalties { get; }

    /// <summary>Whether the module is dark; false outside the code (the quiet zone is light).</summary>
    public bool this[int x, int y] => x >= 0 && x < Size && y >= 0 && y < Size && _modules[y, x];

    public QrRole RoleAt(int x, int y) => _roles[y, x];

    public bool[,] ToMatrix() => (bool[,])_modules.Clone();

    // ---- sizes -------------------------------------------------------------------------------

    public static int RawModules(int version)
    {
        CheckVersion(version);
        int result = (16 * version + 128) * version + 64;
        if (version >= 2)
        {
            int align = version / 7 + 2;
            result -= (25 * align - 10) * align - 55;
            if (version >= 7) result -= 36;
        }

        return result;
    }

    public static int DataCodewords(int version, QrEcc ecc)
    {
        CheckVersion(version);
        return RawModules(version) / 8 - EccPerBlock[(int)ecc][version] * Blocks[(int)ecc][version];
    }

    public static int EccCodewordsPerBlock(int version, QrEcc ecc) => EccPerBlock[(int)ecc][version];

    public static int BlockCount(int version, QrEcc ecc) => Blocks[(int)ecc][version];

    /// <summary>How many codewords the code can have wrong and still be read (half the check bytes of every block).</summary>
    public static int Correctable(int version, QrEcc ecc) => EccPerBlock[(int)ecc][version] / 2 * Blocks[(int)ecc][version];

    public static IReadOnlyList<int> AlignmentPositions(int version)
    {
        CheckVersion(version);
        if (version == 1) return [];

        int align = version / 7 + 2;
        int step  = version == 32 ? 26 : (version * 4 + align * 2 + 1) / (align * 2 - 2) * 2;
        var result = new List<int> { 6 };
        for (int i = 0, pos = version * 4 + 17 - 7; i < align - 1; i++, pos -= step) result.Insert(1, pos);
        return result;
    }

    private static void CheckVersion(int version)
    {
        if (version is < MinVersion or > MaxVersion) throw new ArgumentOutOfRangeException(nameof(version), "The version is 1 to 40.");
    }

    // ---- making a code -----------------------------------------------------------------------

    /// <summary>
    ///     Makes the code for the segments: the smallest version from <paramref name="minVersion" /> to <paramref name="maxVersion" /> that holds them at the level of correction asked for
    ///     (or a higher one that fits the same version, when <paramref name="boost" />), with the mask given (0 to 7) or, for -1, the one with the lowest penalty.
    ///     Throws <see cref="QrCapacityException" /> when nothing fits.
    /// </summary>
    public static QrCode Encode(IReadOnlyList<QrSegment> segments, QrEcc ecc, int minVersion = MinVersion, int maxVersion = MaxVersion, int mask = -1, bool boost = true)
    {
        if (minVersion < MinVersion || minVersion > maxVersion || maxVersion > MaxVersion) throw new ArgumentOutOfRangeException(nameof(minVersion));
        if (mask is < -1 or > 7) throw new ArgumentOutOfRangeException(nameof(mask));

        int version = minVersion;
        long used;
        while (true)
        {
            long capacity = DataCodewords(version, ecc) * 8L;
            used = QrSegment.TotalBits(segments, version);
            if (used >= 0 && used <= capacity) break;

            if (version >= maxVersion)
            {
                string detail = used < 0 ? "The text is longer than a segment of this kind can count." : $"The data needs {used:N0} bits and the biggest code allowed ({maxVersion}) holds {capacity:N0} at this level of correction.";
                throw new QrCapacityException("The text is too long for the code. " + detail);
            }

            version++;
        }

        // a better level of correction costs nothing when the version has room for it
        if (boost)
            foreach (var better in new[] { QrEcc.Medium, QrEcc.Quartile, QrEcc.High })
                if (better > ecc && used <= DataCodewords(version, better) * 8L)
                    ecc = better;

        var bits = new QrBits();
        foreach (var s in segments)
        {
            bits.Append((int)s.Mode, 4);
            bits.Append(s.Characters, QrSegment.CountBits(s.Mode, version));
            bits.Append(s.Data);
        }

        int capacityBits = DataCodewords(version, ecc) * 8;
        bits.Append(0, Math.Min(4, capacityBits - bits.Length));
        bits.Append(0, (8 - bits.Length % 8) % 8);
        for (int pad = 0xEC; bits.Length < capacityBits; pad ^= 0xEC ^ 0x11) bits.Append(pad, 8);

        return FromCodewords(version, ecc, bits.ToBytes(), mask, (int)used);
    }

    /// <summary>The code for the text, in the mode that packs it best (or <paramref name="mode" />).</summary>
    public static QrCode EncodeText(string text, QrEcc ecc, QrMode? mode = null, bool eci = false, int minVersion = MinVersion, int maxVersion = MaxVersion, int mask = -1, bool boost = true)
    {
        return Encode(QrSegment.ForText(text, mode, eci), ecc, minVersion, maxVersion, mask, boost);
    }

    /// <summary>The code for data codewords that are made already (headers, padding and all).</summary>
    public static QrCode FromCodewords(int version, QrEcc ecc, byte[] data, int mask = -1, int dataBits = -1)
    {
        CheckVersion(version);
        if (data.Length != DataCodewords(version, ecc)) throw new ArgumentException($"Version {version} at this level holds {DataCodewords(version, ecc)} data codewords, not {data.Length}.", nameof(data));

        int size = version * 4 + 17;
        var modules = new bool[size, size];
        var isFunction = new bool[size, size];
        var roles = new QrRole[size, size];

        DrawFunctionPatterns(version, ecc, modules, isFunction, roles, 0);
        var all = AddEccAndInterleave(version, ecc, data);
        DrawCodewords(modules, isFunction, roles, all, DataCodewords(version, ecc));

        var penalties = new int[8];
        if (mask < 0)
        {
            int best = int.MaxValue;
            for (int m = 0; m < 8; m++)
            {
                ApplyMask(modules, isFunction, m);
                DrawFormatBits(version, ecc, m, modules, isFunction, roles);
                penalties[m] = Penalty(modules);
                if (penalties[m] < best)
                {
                    mask = m;
                    best = penalties[m];
                }

                ApplyMask(modules, isFunction, m);
            }
        }

        ApplyMask(modules, isFunction, mask);
        DrawFormatBits(version, ecc, mask, modules, isFunction, roles);
        penalties[mask] = Penalty(modules);
        return new QrCode(version, ecc, mask, modules, roles, dataBits < 0 ? data.Length * 8 : dataBits, penalties);
    }

    private static byte[] AddEccAndInterleave(int version, QrEcc ecc, byte[] data)
    {
        int blocks      = Blocks[(int)ecc][version];
        int blockEcc    = EccPerBlock[(int)ecc][version];
        int raw         = RawModules(version) / 8;
        int shortBlocks = blocks - raw % blocks;
        int shortLength = raw / blocks;

        var divisor = QrReedSolomon.Divisor(blockEcc);
        var all     = new List<byte[]>();
        for (int i = 0, k = 0; i < blocks; i++)
        {
            int length = shortLength - blockEcc + (i < shortBlocks ? 0 : 1);
            var dat    = data.AsSpan(k, length);
            k += length;
            var check  = QrReedSolomon.Remainder(dat, divisor);

            // a short block gets a place holder so all blocks are the same length when interleaved
            var block = new byte[shortLength + 1];
            dat.CopyTo(block);
            check.CopyTo(block, length + (i < shortBlocks ? 1 : 0));
            all.Add(block);
        }

        var result = new List<byte>();
        for (int i = 0; i < shortLength + 1; i++)
        for (int j = 0; j < blocks; j++)
            if (i != shortLength - blockEcc || j >= shortBlocks)
                result.Add(all[j][i]);
        return result.ToArray();
    }

    // ---- drawing -----------------------------------------------------------------------------

    private static void Set(bool[,] modules, bool[,] isFunction, QrRole[,] roles, int x, int y, bool dark, QrRole role)
    {
        modules[y, x]    = dark;
        isFunction[y, x] = true;
        roles[y, x]      = role;
    }

    private static void DrawFunctionPatterns(int version, QrEcc ecc, bool[,] modules, bool[,] isFunction, QrRole[,] roles, int mask)
    {
        int size = version * 4 + 17;
        for (int i = 0; i < size; i++)
        {
            Set(modules, isFunction, roles, 6, i, i % 2 == 0, QrRole.Timing);
            Set(modules, isFunction, roles, i, 6, i % 2 == 0, QrRole.Timing);
        }

        DrawFinder(modules, isFunction, roles, 3, 3);
        DrawFinder(modules, isFunction, roles, size - 4, 3);
        DrawFinder(modules, isFunction, roles, 3, size - 4);

        var positions = AlignmentPositions(version);
        int count = positions.Count;
        for (int i = 0; i < count; i++)
        for (int j = 0; j < count; j++)
        {
            // none where the finder patterns are
            if ((i == 0 && j == 0) || (i == 0 && j == count - 1) || (i == count - 1 && j == 0)) continue;

            for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                Set(modules, isFunction, roles, positions[i] + dx, positions[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1, QrRole.Alignment);
        }

        DrawFormatBits(version, ecc, mask, modules, isFunction, roles);
        if (version >= 7)
        {
            int rem = version;
            for (int i = 0; i < 12; i++) rem = rem << 1 ^ (rem >> 11) * 0x1F25;
            int bits = version << 12 | rem;
            for (int i = 0; i < 18; i++)
            {
                bool bit = (bits >> i & 1) != 0;
                int a = size - 11 + i % 3;
                int b = i / 3;
                Set(modules, isFunction, roles, a, b, bit, QrRole.Version);
                Set(modules, isFunction, roles, b, a, bit, QrRole.Version);
            }
        }
    }

    private static void DrawFinder(bool[,] modules, bool[,] isFunction, QrRole[,] roles, int cx, int cy)
    {
        int size = modules.GetLength(0);
        for (int dy = -4; dy <= 4; dy++)
        for (int dx = -4; dx <= 4; dx++)
        {
            int x = cx + dx, y = cy + dy;
            if (x < 0 || x >= size || y < 0 || y >= size) continue;

            int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
            Set(modules, isFunction, roles, x, y, dist is not (2 or 4), dist == 4 ? QrRole.Separator : QrRole.Finder);
        }
    }

    /// <summary>The 15 bits that say the level of correction and the mask, protected by a BCH code and masked so they are never all zero.</summary>
    public static int FormatBits(QrEcc ecc, int mask)
    {
        int level = ecc switch { QrEcc.Low => 1, QrEcc.Medium => 0, QrEcc.Quartile => 3, _ => 2 };
        int data  = level << 3 | mask;
        int rem   = data;
        for (int i = 0; i < 10; i++) rem = rem << 1 ^ (rem >> 9) * 0x537;
        return (data << 10 | rem) ^ 0x5412;
    }

    /// <summary>The 18 bits that say the version (7 and up), protected by a BCH code.</summary>
    public static int VersionBits(int version)
    {
        int rem = version;
        for (int i = 0; i < 12; i++) rem = rem << 1 ^ (rem >> 11) * 0x1F25;
        return version << 12 | rem;
    }

    private static void DrawFormatBits(int version, QrEcc ecc, int mask, bool[,] modules, bool[,] isFunction, QrRole[,] roles)
    {
        int size = version * 4 + 17;
        int bits = FormatBits(ecc, mask);
        bool Bit(int i) => (bits >> i & 1) != 0;

        for (int i = 0; i <= 5; i++) Set(modules, isFunction, roles, 8, i, Bit(i), QrRole.Format);
        Set(modules, isFunction, roles, 8, 7, Bit(6), QrRole.Format);
        Set(modules, isFunction, roles, 8, 8, Bit(7), QrRole.Format);
        Set(modules, isFunction, roles, 7, 8, Bit(8), QrRole.Format);
        for (int i = 9; i < 15; i++) Set(modules, isFunction, roles, 14 - i, 8, Bit(i), QrRole.Format);

        for (int i = 0; i < 8; i++) Set(modules, isFunction, roles, size - 1 - i, 8, Bit(i), QrRole.Format);
        for (int i = 8; i < 15; i++) Set(modules, isFunction, roles, 8, size - 15 + i, Bit(i), QrRole.Format);
        Set(modules, isFunction, roles, 8, size - 8, true, QrRole.DarkModule);
    }

    /// <summary>The places the codewords go, in the order they fill them: up and down in pairs of columns from the right, skipping the column of the timing pattern.</summary>
    private static IEnumerable<(int X, int Y)> ZigZag(int size)
    {
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;

            for (int vert = 0; vert < size; vert++)
            for (int j = 0; j < 2; j++)
            {
                int x = right - j;
                bool upward = (right + 1 & 2) == 0;
                int y = upward ? size - 1 - vert : vert;
                yield return (x, y);
            }
        }
    }

    private static void DrawCodewords(bool[,] modules, bool[,] isFunction, QrRole[,] roles, byte[] all, int dataCodewords)
    {
        int size = modules.GetLength(0);
        int i = 0;
        foreach (var (x, y) in ZigZag(size))
        {
            if (isFunction[y, x]) continue;

            if (i < all.Length * 8)
            {
                modules[y, x] = (all[i >> 3] >> (7 - (i & 7)) & 1) != 0;
                roles[y, x]   = i < dataCodewords * 8 ? QrRole.Data : QrRole.Ecc;
                i++;
            }
            else
            {
                // the few modules left over when the codewords do not fill the code (they stay light)
                roles[y, x] = QrRole.Remainder;
            }
        }
    }

    /// <summary>The modules of a code of that version that are not data: the finder, timing and alignment patterns, the format and version information (true where so).</summary>
    public static bool[,] FunctionModules(int version)
    {
        CheckVersion(version);
        int size = version * 4 + 17;
        var isFunction = new bool[size, size];
        DrawFunctionPatterns(version, QrEcc.Low, new bool[size, size], isFunction, new QrRole[size, size], 0);
        return isFunction;
    }

    /// <summary>The modules of a code as the order the codewords go into them: (x, y) of every place that holds data, in order (see also <see cref="DataPlaces" />).</summary>
    internal static IEnumerable<(int X, int Y)> Order(int size) => ZigZag(size);

    /// <summary>The places of the modules of data, in the order the codewords fill them (for decoding, and for showing which codeword a module belongs to).</summary>
    public static IReadOnlyList<(int X, int Y)> DataPlaces(int version)
    {
        int size = version * 4 + 17;
        var modules = new bool[size, size];
        var isFunction = new bool[size, size];
        var roles = new QrRole[size, size];
        DrawFunctionPatterns(version, QrEcc.Low, modules, isFunction, roles, 0);
        return ZigZag(size).Where(p => !isFunction[p.Y, p.X]).ToList();
    }

    private static void ApplyMask(bool[,] modules, bool[,] isFunction, int mask)
    {
        int size = modules.GetLength(0);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
            if (!isFunction[y, x] && MaskFlips(mask, x, y))
                modules[y, x] = !modules[y, x];
    }

    /// <summary>Whether mask pattern <paramref name="mask" /> turns the module at (x, y) over.</summary>
    public static bool MaskFlips(int mask, int x, int y)
    {
        return mask switch
               {
                   0 => (x + y) % 2 == 0,
                   1 => y % 2 == 0,
                   2 => x % 3 == 0,
                   3 => (x + y) % 3 == 0,
                   4 => (x / 3 + y / 2) % 2 == 0,
                   5 => x * y % 2 + x * y % 3 == 0,
                   6 => (x * y % 2 + x * y % 3) % 2 == 0,
                   7 => ((x + y) % 2 + x * y % 3) % 2 == 0,
                   _ => throw new ArgumentOutOfRangeException(nameof(mask))
               };
    }

    // ---- the penalty that decides the mask ------------------------------------------------------

    /// <summary>How bad a pattern of modules is, the way the standard counts it: long runs, blocks of the same color, patterns that look like a finder pattern, and a balance of dark and light that is off.</summary>
    public static int Penalty(bool[,] modules)
    {
        int size   = modules.GetLength(0);
        int result = 0;

        for (int pass = 0; pass < 2; pass++)
        for (int a = 0; a < size; a++)
        {
            bool runColor = false;
            int runLength = 0;
            var history   = new int[7];
            for (int b = 0; b < size; b++)
            {
                bool dark = pass == 0 ? modules[a, b] : modules[b, a];
                if (dark == runColor)
                {
                    runLength++;
                    if (runLength == 5) result += 3;
                    else if (runLength > 5) result++;
                }
                else
                {
                    AddHistory(runLength, history, size);
                    if (!runColor) result += CountPatterns(history) * 40;
                    runColor  = dark;
                    runLength = 1;
                }
            }

            result += TerminateAndCount(runColor, runLength, history, size) * 40;
        }

        for (int y = 0; y < size - 1; y++)
        for (int x = 0; x < size - 1; x++)
        {
            bool c = modules[y, x];
            if (c == modules[y, x + 1] && c == modules[y + 1, x] && c == modules[y + 1, x + 1]) result += 3;
        }

        int dark2 = 0;
        foreach (bool m in modules)
            if (m)
                dark2++;
        int total = size * size;
        int k     = (Math.Abs(dark2 * 20 - total * 10) + total - 1) / total - 1;
        return result + k * 10;
    }

    private static int CountPatterns(int[] history)
    {
        int n    = history[1];
        bool core = n > 0 && history[2] == n && history[3] == n * 3 && history[4] == n && history[5] == n;
        return (core && history[0] >= n * 4 && history[6] >= n ? 1 : 0) + (core && history[6] >= n * 4 && history[0] >= n ? 1 : 0);
    }

    private static int TerminateAndCount(bool runColor, int runLength, int[] history, int size)
    {
        if (runColor)
        {
            AddHistory(runLength, history, size);
            runLength = 0;
        }

        runLength += size;
        AddHistory(runLength, history, size);
        return CountPatterns(history);
    }

    private static void AddHistory(int runLength, int[] history, int size)
    {
        // a run at the edge counts the light border around the code as part of it
        if (history[0] == 0) runLength += size;

        Array.Copy(history, 0, history, 1, history.Length - 1);
        history[0] = runLength;
    }
}

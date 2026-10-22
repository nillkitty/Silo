using System.Text;

namespace Silo.Labs.Streams;

/// <summary>Bytes laid out as a hex dump: the offset, the bytes in hex, the bytes as characters.</summary>
public static class HexDump
{
    public const int Width = 16;

    /// <summary>Where in a line of hex the digits of byte <paramref name="i" /> of the row begin (a gap after every eight bytes).</summary>
    public static int HexColumn(int i) => i * 3 + i / 8;

    /// <summary>The width of the hex of a full row.</summary>
    public static int HexWidth(int width = Width) => HexColumn(width - 1) + 2;

    /// <summary>The character a byte shows as in the right column: itself when it is printable ASCII, a dot when not.</summary>
    public static char Show(byte b) => b is >= 0x20 and < 0x7F ? (char)b : '.';

    public static string Offset(long offset) => offset.ToString("X8");

    /// <summary>The hex of a row of bytes, padded to the width of a full row.</summary>
    public static string Hex(ReadOnlySpan<byte> row, int width = Width)
    {
        var text = new StringBuilder(HexWidth(width));
        for (int i = 0; i < width; i++)
        {
            if (i > 0) text.Append(i % 8 == 0 ? "  " : " ");
            if (i < row.Length) text.Append(row[i].ToString("X2"));
            else text.Append("  ");
        }

        return text.ToString();
    }

    public static string Ascii(ReadOnlySpan<byte> row)
    {
        var text = new StringBuilder(row.Length);
        foreach (byte b in row) text.Append(Show(b));
        return text.ToString();
    }

    /// <summary>The whole dump as text, a line to a row: <c>00000010  48 65 ...  |Hello...|</c>.</summary>
    public static string Format(ReadOnlySpan<byte> data, long firstOffset = 0, int width = Width)
    {
        var text = new StringBuilder();
        for (int at = 0; at < data.Length; at += width)
        {
            var row = data.Slice(at, Math.Min(width, data.Length - at));
            if (text.Length > 0) text.AppendLine();
            text.Append(Offset(firstOffset + at)).Append("  ").Append(Hex(row, width)).Append("  |").Append(Ascii(row)).Append('|');
        }

        return text.ToString();
    }
}

/// <summary>The bytes read or written so far, kept for looking at and cutting from, up to a limit.</summary>
public sealed class ByteStrip(long limit)
{
    private byte[] _data = new byte[256];
    private int    _length;

    public long Limit { get; set; } = limit;

    /// <summary>Changes every time the strip does (what a view made from the strip checks to know that it is out of date).</summary>
    public long Version { get; private set; }

    public int Length => _length;

    /// <summary>Some bytes did not fit under the limit and were not kept.</summary>
    public bool Overflowed { get; private set; }

    /// <summary>Where the last bytes added begin, and how many they are (what the viewer marks as just read).</summary>
    public (int Start, int Length) Last { get; private set; }

    public ReadOnlySpan<byte> Span => _data.AsSpan(0, _length);

    public void Add(ReadOnlySpan<byte> bytes)
    {
        int room = (int)Math.Max(0, Math.Min(Limit, int.MaxValue / 2) - _length);
        if (bytes.Length > room)
        {
            Overflowed = true;
            bytes      = bytes[..room];
        }

        if (_length + bytes.Length > _data.Length) Array.Resize(ref _data, (int)Math.Max(_data.Length * 2L, _length + (long)bytes.Length));
        bytes.CopyTo(_data.AsSpan(_length));
        Last    = (_length, bytes.Length);
        _length += bytes.Length;
        Version++;
    }

    /// <summary>Forgets the last <paramref name="count" /> bytes (a step back).</summary>
    public void Trim(int count)
    {
        _length = Math.Max(0, _length - count);
        Last    = (_length, 0);
        Version++;
    }

    public void Clear()
    {
        _length    = 0;
        Overflowed = false;
        Last       = (0, 0);
        Version++;
    }

    /// <summary>The bytes from <paramref name="from" /> up to (not including) <paramref name="to" />, as far as there are any.</summary>
    public byte[] Slice(long from, long to)
    {
        from = Math.Clamp(from, 0, _length);
        to   = Math.Clamp(to, from, _length);
        return _data.AsSpan((int)from, (int)(to - from)).ToArray();
    }
}

using System.IO;

namespace Silo.DataEditing.Compression;

/// <summary>
///     Kosinski, the LZSS-style compression Sega Mega Drive games (Sonic 2's level blocks, chunks, collision and some art) keep their data in.
///     The stream is groups of up to 16 flag bits (a little-endian word, read from its low bit up, fetched when the last bit is used) with
///     the bytes they describe in between: <c>1</c> a byte as it is; <c>00</c> + two bits of length-2 + a byte of offset, a copy of 2-5
///     bytes from up to 256 back; <c>01</c> + two bytes (offset in the first and the top five bits of the second, length-2 in its low three
///     bits, or a length byte after them when those are 0) a copy of 3-256 bytes from up to 8192 back. A length byte of 0 ends the data.
/// </summary>
public static class Kosinski
{
    private const int MaxFullOffset = 8192;

    private const int MaxInlineOffset = 256;

    /// <summary>Reads Kosinski data; stops at its end mark (bytes after that are ignored, as the games do).</summary>
    /// <exception cref="InvalidDataException">The data ends before its end mark, or a copy reaches back before the start.</exception>
    public static byte[] Decompress(ReadOnlySpan<byte> data) => Decompress(data, out _);

    /// <summary>Reads Kosinski data, and says how many bytes of it were the compressed stream.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> source, out int consumed)
    {
        byte[] data = source.ToArray(); // local functions below cannot capture a span
        var  output = new List<byte>(data.Length * 3);
        int  at     = 0;
        int  bits   = 0, left = 0;

        byte Next() => at < data.Length ? data[at++] : throw new InvalidDataException($"The Kosinski data ends at byte {at} before its end mark.");

        void Refill()
        {
            bits = Next() | (Next() << 8);
            left = 16;
        }

        // as the games' own routine does: the next word is fetched the moment the last bit of this one is taken, before the bytes that bit's
        // operation reads
        bool Bit()
        {
            bool set = (bits & 1) != 0;
            bits >>= 1;
            if (--left == 0) Refill();
            return set;
        }

        Refill();

        void Copy(int offset, int count)
        {
            int from = output.Count - offset;
            if (from < 0) throw new InvalidDataException($"A copy at output byte {output.Count} reaches {offset} bytes back, before the start.");

            for (int i = 0; i < count; i++) output.Add(output[from + i]); // a copy may overlap what it is writing: that repeats it
        }

        while (true)
        {
            if (Bit())
            {
                output.Add(Next());
            }
            else if (Bit())
            {
                int lo = Next(), hi = Next();
                int offset = 0x2000 - (((hi & 0xF8) << 5) | lo) & 0x1FFF;
                if (offset == 0) offset = MaxFullOffset;

                int count = hi & 7;
                if (count == 0)
                {
                    count = Next();
                    if (count == 0) break;     // the end mark
                    if (count == 1) continue;  // a mark that does nothing (it ends a module in some files)

                    count++;
                }
                else
                {
                    count += 2;
                }

                Copy(offset, count);
            }
            else
            {
                int count  = ((Bit() ? 2 : 0) | (Bit() ? 1 : 0)) + 2;
                int offset = 256 - Next();
                if (offset == 0) offset = 256;

                Copy(offset, count);
            }
        }

        consumed = at;
        return output.ToArray();
    }

    /// <summary>
    ///     Compresses with a greedy longest match: not the smallest result a tool such as Flamewing's can make, but valid for every
    ///     decompressor of the format. Reading it back gives <paramref name="data" /> exactly.
    /// </summary>
    public static byte[] Compress(ReadOnlySpan<byte> source)
    {
        byte[] data = source.ToArray(); // local functions below cannot capture a span
        var writer = new Writer();
        var heads  = new Dictionary<int, List<int>>();

        int Key(int i) => data[i] | (data[i + 1] << 8) | (data[i + 2] << 16);

        void Index(int i)
        {
            if (i + 2 >= data.Length) return;

            if (!heads.TryGetValue(Key(i), out var list)) heads[Key(i)] = list = [];
            list.Add(i);
        }

        int pos = 0;
        while (pos < data.Length)
        {
            int bestLen = 0, bestDist = 0;

            // longer matches: candidates with the same first three bytes, nearest first
            if (pos + 2 < data.Length && heads.TryGetValue(Key(pos), out var candidates))
            {
                int tried = 0;
                for (int c = candidates.Count - 1; c >= 0 && tried < 128; c--, tried++)
                {
                    int dist = pos - candidates[c];
                    if (dist > MaxFullOffset) break;

                    int len = 0;
                    while (len < 256 && pos + len < data.Length && data[candidates[c] + len] == data[pos + len]) len++;
                    if (len > bestLen)
                    {
                        bestLen  = len;
                        bestDist = dist;
                        if (len == 256) break;
                    }
                }
            }

            // a two-byte match is only worth it as the short form, which reaches 256 back
            if (bestLen < 2)
                for (int dist = 1; dist <= MaxInlineOffset && dist <= pos; dist++)
                    if (pos + 1 < data.Length && data[pos - dist] == data[pos] && data[pos - dist + 1] == data[pos + 1])
                    {
                        bestLen  = 2;
                        bestDist = dist;
                        break;
                    }

            int take;
            if (bestLen >= 3 && bestDist <= MaxInlineOffset && bestLen <= 5)
            {
                writer.Inline(bestDist, bestLen);
                take = bestLen;
            }
            else if (bestLen >= 3)
            {
                writer.Full(bestDist, bestLen);
                take = bestLen;
            }
            else if (bestLen == 2)
            {
                writer.Inline(bestDist, 2);
                take = 2;
            }
            else
            {
                writer.Literal(data[pos]);
                take = 1;
            }

            for (int i = 0; i < take; i++) Index(pos + i);
            pos += take;
        }

        writer.End();
        return writer.ToArray();
    }

    /// <summary>
    ///     Builds the stream: flag bits go into a word whose place is reserved first; the place of the next word is reserved the moment the
    ///     16th bit of this one is pushed, before that bit's bytes, which is where a reader that fetches the next word at that moment finds it.
    /// </summary>
    private sealed class Writer
    {
        private readonly List<byte> _out = [];
        private int _word, _bits, _value;

        public Writer()
        {
            Reserve();
        }

        private void Reserve()
        {
            _word = _out.Count;
            _out.Add(0);
            _out.Add(0);
            _value = 0;
            _bits  = 0;
        }

        private void Push(bool bit)
        {
            if (bit) _value |= 1 << _bits;
            _bits++;
            if (_bits == 16)
            {
                Flush();
                Reserve();
            }
        }

        private void Flush()
        {
            _out[_word]     = (byte)_value;
            _out[_word + 1] = (byte)(_value >> 8);
        }

        public void Literal(byte b)
        {
            Push(true);
            _out.Add(b);
        }

        public void Inline(int distance, int count)
        {
            Push(false);
            Push(false);
            Push(((count - 2) & 2) != 0);
            Push(((count - 2) & 1) != 0);
            _out.Add((byte)(256 - distance));
        }

        public void Full(int distance, int count)
        {
            Push(false);
            Push(true);
            int offset = (0x2000 - distance) & 0x1FFF;
            int lo = offset & 0xFF, hi = (offset >> 5) & 0xF8;
            if (count <= 9)
            {
                _out.Add((byte)lo);
                _out.Add((byte)(hi | (count - 2)));
            }
            else
            {
                _out.Add((byte)lo);
                _out.Add((byte)hi);
                _out.Add((byte)(count - 1));
            }
        }

        public void End()
        {
            Push(false);
            Push(true);
            _out.Add(0x00);
            _out.Add(0xF0);
            _out.Add(0x00);
            Flush();
        }

        public byte[] ToArray() => _out.ToArray();
    }
}

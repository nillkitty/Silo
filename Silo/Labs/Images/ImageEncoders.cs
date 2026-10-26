using System.IO.Compression;

namespace Silo.Labs.Images;

/// <summary>Writes PNG files (and icons that hold PNG pictures) without help from the platform, so the engine can be tested anywhere.</summary>
public static class ImageEncoders
{
    private static readonly uint[] CrcTable = MakeCrcTable();

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }

    private static uint Crc(byte[] type, byte[] data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in type) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, Crc(t, data));
        s.Write(len);
    }

    /// <summary>The picture as a PNG file (8 bits a channel, with alpha).</summary>
    public static byte[] Png(Raster r)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0), (uint)r.Width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)r.Height);
        header[8]  = 8; // bits
        header[9]  = 6; // color type: RGBA
        header[10] = 0;
        header[11] = 0;
        header[12] = 0;
        Chunk(output, "IHDR", header);

        // each row is a filter byte (0: none) and the row's pixels as red, green, blue, alpha
        var raw = new byte[(r.Width * 4 + 1) * r.Height];
        int at  = 0;
        for (int y = 0; y < r.Height; y++)
        {
            raw[at++] = 0;
            for (int x = 0; x < r.Width; x++)
            {
                int i = (y * r.Width + x) * 4;
                raw[at++] = r.Pixels[i + 2];
                raw[at++] = r.Pixels[i + 1];
                raw[at++] = r.Pixels[i];
                raw[at++] = r.Pixels[i + 3];
            }
        }

        using (var packed = new MemoryStream())
        {
            using (var z = new ZLibStream(packed, CompressionLevel.Optimal, true)) z.Write(raw, 0, raw.Length);

            Chunk(output, "IDAT", packed.ToArray());
        }

        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    /// <summary>
    ///     An icon file with the picture at each of the sizes (each fitted into a square of that size, kept in proportion and centred): a
    ///     PNG picture to a size, which Windows has read since Vista. Sizes are 1 to 256.
    /// </summary>
    public static byte[] Ico(Raster r, IEnumerable<int> sizes)
    {
        var list  = sizes.Where(s => s is >= 1 and <= 256).Distinct().OrderBy(s => s).ToList();
        if (list.Count == 0) list.Add(256);

        var pngs = new List<byte[]>();
        foreach (int size in list)
        {
            double k = Math.Min((double)size / r.Width, (double)size / r.Height);
            int w = Math.Max(1, (int)Math.Round(r.Width * k)), h = Math.Max(1, (int)Math.Round(r.Height * k));
            var scaled = w == r.Width && h == r.Height ? r : r.Resize(w, h);
            var square = new Raster(size, size);
            square.Paste(scaled, (size - w) / 2, (size - h) / 2);
            pngs.Add(Png(square));
        }

        using var output = new MemoryStream();
        using var w16    = new BinaryWriter(output);
        w16.Write((ushort)0); // reserved
        w16.Write((ushort)1); // icon
        w16.Write((ushort)list.Count);
        int offset = 6 + 16 * list.Count;
        for (int i = 0; i < list.Count; i++)
        {
            w16.Write((byte)(list[i] == 256 ? 0 : list[i]));
            w16.Write((byte)(list[i] == 256 ? 0 : list[i]));
            w16.Write((byte)0);   // no palette
            w16.Write((byte)0);   // reserved
            w16.Write((ushort)1); // planes
            w16.Write((ushort)32);
            w16.Write((uint)pngs[i].Length);
            w16.Write((uint)offset);
            offset += pngs[i].Length;
        }

        foreach (var png in pngs) w16.Write(png);
        w16.Flush();
        return output.ToArray();
    }

    /// <summary>The picture over a solid color (for formats that have no transparency, like JPEG and BMP).</summary>
    public static Raster Flatten(Raster r, Rgba background)
    {
        var result = new Raster(r.Width, r.Height);
        result.Fill(new Rgba(background.R, background.G, background.B));
        for (int p = 0; p < r.Width * r.Height; p++)
        {
            int i = p * 4;
            if (r.Pixels[i + 3] == 0) continue;

            result.Over(p % r.Width, p / r.Width, new Rgba(r.Pixels[i + 2], r.Pixels[i + 1], r.Pixels[i], r.Pixels[i + 3]), 1);
        }

        return result;
    }
}

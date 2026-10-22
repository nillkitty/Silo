namespace Silo.Labs.Qr;

/// <summary>Reed-Solomon codes over GF(2^8) with the polynomial x^8 + x^4 + x^3 + x^2 + 1 (0x11D), as QR codes use them.</summary>
public static class QrReedSolomon
{
    private static readonly byte[] Exp = new byte[512];
    private static readonly int[]  Log = new int[256];

    static QrReedSolomon()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = i;
            x <<= 1;
            if (x >= 256) x ^= 0x11D;
        }

        for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
    }

    public static byte Multiply(byte x, byte y)
    {
        return x == 0 || y == 0 ? (byte)0 : Exp[Log[x] + Log[y]];
    }

    private static byte Divide(byte x, byte y)
    {
        if (y == 0) throw new DivideByZeroException();

        return x == 0 ? (byte)0 : Exp[(Log[x] + 255 - Log[y]) % 255];
    }

    /// <summary>The generator polynomial of a code with <paramref name="degree" /> check bytes, without its leading 1 coefficient (highest power first).</summary>
    public static byte[] Divisor(int degree)
    {
        if (degree is < 1 or > 255) throw new ArgumentOutOfRangeException(nameof(degree));

        var result = new byte[degree];
        result[degree - 1] = 1;

        // the product of (x - r^0)(x - r^1)...(x - r^(degree-1)), r being the generator of the field
        byte root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < result.Length; j++)
            {
                result[j] = Multiply(result[j], root);
                if (j + 1 < result.Length) result[j] ^= result[j + 1];
            }

            root = Multiply(root, 2);
        }

        return result;
    }

    /// <summary>The check bytes of <paramref name="data" /> for the given divisor (see <see cref="Divisor" />).</summary>
    public static byte[] Remainder(ReadOnlySpan<byte> data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (byte b in data)
        {
            byte factor = (byte)(b ^ result[0]);
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (int i = 0; i < result.Length; i++) result[i] ^= Multiply(divisor[i], factor);
        }

        return result;
    }

    /// <summary>
    ///     Corrects a received block (the data bytes followed by <paramref name="checkBytes" /> check bytes) in place. Returns how many bytes were wrong and put
    ///     right, or -1 when there are more errors than the code can correct.
    /// </summary>
    public static int Correct(byte[] block, int checkBytes)
    {
        int n = block.Length;

        // the syndromes: the block as a polynomial, evaluated at r^0 ... r^(checkBytes-1)
        var syndromes = new byte[checkBytes];
        bool clean    = true;
        for (int i = 0; i < checkBytes; i++)
        {
            byte s = 0;
            foreach (byte b in block) s = (byte)(Multiply(s, Exp[i]) ^ b);
            syndromes[i] = s;
            if (s != 0) clean = false;
        }

        if (clean) return 0;

        // Berlekamp-Massey: the shortest polynomial that makes the syndromes (lowest power first)
        var locator  = new List<byte> { 1 };
        var previous = new List<byte> { 1 };
        int errors   = 0;
        int shift    = 1;
        byte lastDiscrepancy = 1;
        for (int i = 0; i < checkBytes; i++)
        {
            byte d = syndromes[i];
            for (int j = 1; j <= errors && j < locator.Count; j++) d ^= Multiply(locator[j], syndromes[i - j]);

            if (d == 0)
            {
                shift++;
                continue;
            }

            var before = new List<byte>(locator);
            byte factor = Divide(d, lastDiscrepancy);
            while (locator.Count < previous.Count + shift) locator.Add(0);
            for (int j = 0; j < previous.Count; j++) locator[j + shift] ^= Multiply(factor, previous[j]);

            if (2 * errors <= i)
            {
                errors          = i + 1 - errors;
                previous        = before;
                lastDiscrepancy = d;
                shift           = 1;
            }
            else shift++;
        }

        if (errors == 0 || errors * 2 > checkBytes) return -1;

        // Chien search: the positions where the locator is zero (position p counts from the end of the block)
        var positions = new List<int>();
        for (int p = 0; p < n; p++)
        {
            // the locator evaluated at r^-p
            byte value = 0;
            for (int j = locator.Count - 1; j >= 0; j--) value = (byte)(Multiply(value, Exp[(255 - p % 255) % 255]) ^ locator[j]);
            if (value == 0) positions.Add(p);
        }

        if (positions.Count != errors) return -1;

        // Forney: the size of each error from the error evaluator (syndromes times locator, cut to the number of check bytes)
        var evaluator = new byte[checkBytes];
        for (int i = 0; i < checkBytes; i++)
        for (int j = 0; j < locator.Count && j <= i; j++)
            evaluator[i] ^= Multiply(locator[j], syndromes[i - j]);

        foreach (int p in positions)
        {
            byte xInverse = Exp[(255 - p % 255) % 255];
            byte x        = Exp[p % 255];

            byte numerator = 0;
            for (int j = evaluator.Length - 1; j >= 0; j--) numerator = (byte)(Multiply(numerator, xInverse) ^ evaluator[j]);

            // the derivative of the locator: only the terms of odd power remain
            byte denominator = 0;
            for (int j = 1; j < locator.Count; j += 2) denominator ^= Multiply(locator[j], Exp[(255 - p * (j - 1) % 255) % 255]);

            if (denominator == 0) return -1;

            byte magnitude = Multiply(x, Divide(numerator, denominator));
            block[n - 1 - p] ^= magnitude;
        }

        // the result must be a codeword now
        for (int i = 0; i < checkBytes; i++)
        {
            byte s = 0;
            foreach (byte b in block) s = (byte)(Multiply(s, Exp[i]) ^ b);
            if (s != 0) return -1;
        }

        return positions.Count;
    }
}

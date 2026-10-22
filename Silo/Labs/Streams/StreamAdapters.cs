using System.IO;
using System.Buffers;
using System.Buffers.Text;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Silo.Labs.Streams;

/// <summary>The argument of an adapter is not acceptable (the message says why).</summary>
public sealed class AdapterException(string message) : Exception(message);

/// <summary>
///     A transformation of bytes that is fed a piece at a time, however small: a byte at a time works as well as a megabyte. The
///     <see cref="StreamAdapters" /> built on it can be put on either side of a stream.
/// </summary>
public abstract class ByteTransform
{
    /// <summary>The transform wants nothing more (it took all it was asked to take).</summary>
    public virtual bool IsDone => false;

    /// <summary>What the transform has to say about the data it has seen (a digest, a count), or null.</summary>
    public virtual string? Report => null;

    public abstract void Feed(ReadOnlySpan<byte> input, IBufferWriter<byte> output);

    /// <summary>The data ended: whatever the transform kept back is let out.</summary>
    public virtual void Finish(IBufferWriter<byte> output)
    {
    }
}

// ---- the transforms ------------------------------------------------------------------------------

internal sealed class XorTransform(byte[] key) : ByteTransform
{
    private long _at;

    public override void Feed(ReadOnlySpan<byte> input, IBufferWriter<byte> output)
    {
        var span = output.GetSpan(input.Length);
        for (int i = 0; i < input.Length; i++) span[i] = (byte)(input[i] ^ key[(int)(_at++ % key.Length)]);
        output.Advance(input.Length);
    }
}

internal sealed class MapTransform(Func<byte, byte> map) : ByteTransform
{
    public override void Feed(ReadOnlySpan<byte> input, IBufferWriter<byte> output)
    {
        var span = output.GetSpan(input.Length);
        for (int i = 0; i < input.Length; i++) span[i] = map(input[i]);
        output.Advance(input.Length);
    }
}

internal sealed class HexEncodeTransform(bool upper) : ByteTransform
{
    public override void Feed(ReadOnlySpan<byte> input, IBufferWriter<byte> output)
    {
        var span = output.GetSpan(input.Length * 2);
        string digits = upper ? "0123456789ABCDEF" : "0123456789abcdef";
        for (int i = 0; i < input.Length; i++)
        {
            span[i * 2]     = (byte)digits[input[i] >> 4];
            span[i * 2 + 1] = (byte)digits[input[i] & 0xF];
        }

        output.Advance(input.Length * 2);
    }
}

internal sealed class HexDecodeTransform : ByteTransform
{
    private int  _high = -1;
    private long _at;

    public override void Feed(ReadOnlySpan<byte> input, IBufferWriter<byte> output)
    {
        var span = output.GetSpan(input.Length / 2 + 1);
        int n    = 0;
        foreach (byte b in input)
        {
            _at++;
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') continue;

            int digit = b is >= (byte)'0' and <= (byte)'9' ? b - '0' : b is >= (byte)'a' and <= (byte)'f' ? b - 'a' + 10 : b is >= (byte)'A' and <= (byte)'F' ? b - 'A' + 10 : -1;
            if (digit < 0)
            {
                // what was read before the bad digit is still let out
                output.Advance(n);
                throw new FormatException($"Byte {_at - 1} is not a hex digit.");
            }

            if (_high < 0) _high = digit;
            else
            {
                span[n++] = (byte)(_high << 4 | digit);
                _high     = -1;
            }
        }

        output.Advance(n);
    }

    public override void Finish(IBufferWriter<byte> output)
    {
        if (_high >= 0) throw new FormatException("The hex ends in the middle of a byte.");
    }
}

internal sealed class Base64EncodeTransform : ByteTransform
{
    private readonly byte[] _rest = new byte[2];
    private          int    _held;

    public override void Feed(ReadOnlySpan<byte> input, IBufferWriter<byte> output)
    {
        var all = new byte[_held + input.Length];
        _rest.AsSpan(0, _held).CopyTo(all);
        input.CopyTo(all.AsSpan(_held));
        int whole = all.Length / 3 * 3;
        if (whole > 0)
        {
            var span = output.GetSpan(whole / 3 * 4);
            Base64.EncodeToUtf8(all.AsSpan(0, whole), span, out _, out int written, false);
            output.Advance(written);
        }

        _held = all.Length - whole;
        all.AsSpan(whole).CopyTo(_rest);
    }

    public override void Finish(IBufferWriter<byte> output)
    {
        if (_held == 0) return;

        var span = output.GetSpan(4);
        Base64.EncodeToUtf8(_rest.AsSpan(0, _held), span, out _, out int written, true);
        output.Advance(written);
        _held = 0;
    }
}

internal sealed class Base64DecodeTransform : ByteTransform
{
    private readonly List<byte> _quad = [];
    private          long       _at;

    public override void Feed(ReadOnlySpan<byte> input, IBufferWriter<byte> output)
    {
        foreach (byte b in input)
        {
            _at++;
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') continue;

            bool ok = b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)'+' or (byte)'/' or (byte)'=';
            if (!ok) throw new FormatException($"Byte {_at - 1} is not a Base64 character.");

            _quad.Add(b);
            if (_quad.Count < 4) continue;

            var span = output.GetSpan(3);
            if (Base64.DecodeFromUtf8(_quad.ToArray(), span, out _, out int written) != OperationStatus.Done) throw new FormatException($"The Base64 around byte {_at - 1} is not valid.");

            output.Advance(written);
            _quad.Clear();
        }
    }

    public override void Finish(IBufferWriter<byte> output)
    {
        if (_quad.Count > 0) throw new FormatException("The Base64 ends in the middle of a group of four characters.");
    }
}

/// <summary>Puts what is fed through a stream that writes to memory (a compressor, an encryptor) and lets out what it produced.</summary>
internal sealed class SinkTransform : ByteTransform
{
    private readonly MemoryStream _sink = new();
    private readonly Stream       _writer;

    public SinkTransform(Func<Stream, Stream> wrap)
    {
        _writer = wrap(_sink);
    }

    public override void Feed(ReadOnlySpan<byte> input, IBufferWriter<byte> output)
    {
        _writer.Write(input);
        Drain(output);
    }

    public override void Finish(IBufferWriter<byte> output)
    {
        _writer.Dispose();
        Drain(output);
    }

    private void Drain(IBufferWriter<byte> output)
    {
        if (_sink.Length == 0) return;

        var span = output.GetSpan((int)_sink.Length);
        _sink.Position = 0;
        int n = _sink.Read(span);
        output.Advance(n);
        _sink.SetLength(0);
    }
}

internal sealed class SkipTransform(long count) : ByteTransform
{
    private long _left = count;

    public override void Feed(ReadOnlySpan<byte> input, IBufferWriter<byte> output)
    {
        if (_left >= input.Length)
        {
            _left -= input.Length;
            return;
        }

        var rest = input[(int)_left..];
        _left = 0;
        rest.CopyTo(output.GetSpan(rest.Length));
        output.Advance(rest.Length);
    }
}

internal sealed class TakeTransform(long count) : ByteTransform
{
    private long _left = count;

    public override bool IsDone => _left <= 0;

    public override void Feed(ReadOnlySpan<byte> input, IBufferWriter<byte> output)
    {
        int n = (int)Math.Min(_left, input.Length);
        if (n <= 0) return;

        input[..n].CopyTo(output.GetSpan(n));
        output.Advance(n);
        _left -= n;
    }
}

internal sealed class TranscodeTransform(Encoding fromEncoding, Encoding toEncoding) : ByteTransform
{
    private readonly Decoder _decoder = fromEncoding.GetDecoder();
    private readonly Encoder _encoder = toEncoding.GetEncoder();

    public override void Feed(ReadOnlySpan<byte> input, IBufferWriter<byte> output)
    {
        var chars = new char[fromEncoding.GetMaxCharCount(input.Length) + 2];
        int n     = _decoder.GetChars(input, chars, false);
        Encode(chars.AsSpan(0, n), output, false);
    }

    public override void Finish(IBufferWriter<byte> output)
    {
        var chars = new char[4];
        int n     = _decoder.GetChars(ReadOnlySpan<byte>.Empty, chars, true);
        Encode(chars.AsSpan(0, n), output, true);
    }

    private void Encode(ReadOnlySpan<char> chars, IBufferWriter<byte> output, bool flush)
    {
        var span = output.GetSpan(toEncoding.GetMaxByteCount(chars.Length) + 4);
        int n    = _encoder.GetBytes(chars, span, flush);
        output.Advance(n);
    }
}

internal sealed class LineEndingTransform(bool crlf) : ByteTransform
{
    private bool _heldReturn;

    public override void Feed(ReadOnlySpan<byte> input, IBufferWriter<byte> output)
    {
        var span = output.GetSpan(input.Length * 2 + 1);
        int n    = 0;
        foreach (byte b in input)
        {
            if (crlf)
            {
                // a line feed that is not already after a carriage return gets one
                if (b == '\n' && !_heldReturn) span[n++] = (byte)'\r';
                _heldReturn = b == '\r';
                span[n++]   = b;
            }
            else
            {
                // a carriage return is kept back until it is known whether a line feed follows it
                if (_heldReturn && b != '\n') span[n++] = (byte)'\r';
                _heldReturn = b == '\r';
                if (!_heldReturn) span[n++] = b;
            }
        }

        output.Advance(n);
    }

    public override void Finish(IBufferWriter<byte> output)
    {
        if (!crlf && _heldReturn)
        {
            output.GetSpan(1)[0] = (byte)'\r';
            output.Advance(1);
        }

        _heldReturn = false;
    }
}

/// <summary>Lets everything through and counts it: how many bytes, in how many pieces, how big the pieces were.</summary>
internal sealed class MeterTransform : ByteTransform
{
    private long _bytes;
    private long _pieces;
    private int  _smallest = int.MaxValue;
    private int  _largest;

    public override string? Report => _pieces == 0 ? "nothing has gone through" : $"{_bytes:N0} byte(s) in {_pieces:N0} piece(s), the pieces from {_smallest:N0} to {_largest:N0} byte(s)";

    public override void Feed(ReadOnlySpan<byte> input, IBufferWriter<byte> output)
    {
        if (input.Length == 0) return;

        _bytes += input.Length;
        _pieces++;
        _smallest = Math.Min(_smallest, input.Length);
        _largest  = Math.Max(_largest, input.Length);
        input.CopyTo(output.GetSpan(input.Length));
        output.Advance(input.Length);
    }
}

/// <summary>Lets everything through and works out a digest of it.</summary>
internal sealed class HashTransform(HashAlgorithmName name) : ByteTransform
{
    private readonly IncrementalHash _hash  = IncrementalHash.CreateHash(name);
    private          long            _bytes;

    public override string? Report => $"{name.Name} of {_bytes:N0} byte(s): {Convert.ToHexString(_hash.GetCurrentHash()).ToLowerInvariant()}";

    public override void Feed(ReadOnlySpan<byte> input, IBufferWriter<byte> output)
    {
        _hash.AppendData(input);
        _bytes += input.Length;
        input.CopyTo(output.GetSpan(input.Length));
        output.Advance(input.Length);
    }
}

// ---- streams that apply a transform --------------------------------------------------------------

/// <summary>What is written is passed through a transform before it is written to the stream under it (which is left open).</summary>
internal sealed class PushWriteStream(Stream inner, ByteTransform transform) : Stream
{
    private readonly ArrayBufferWriter<byte> _out = new();
    private          bool                    _finished;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => !_finished;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (transform.IsDone) return;

        transform.Feed(buffer, _out);
        Pass();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (transform.IsDone) return;

        transform.Feed(buffer.Span, _out);
        await PassAsync(cancellationToken).ConfigureAwait(false);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>Tells the transform the data is over, passes on what it kept back, and flushes the stream under it.</summary>
    public async Task FinishAsync(CancellationToken cancellationToken = default)
    {
        if (_finished) return;

        _finished = true;
        transform.Finish(_out);
        await PassAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_finished)
        {
            _finished = true;
            transform.Finish(_out);
            Pass();
        }

        base.Dispose(disposing);
    }

    private void Pass()
    {
        if (_out.WrittenCount == 0) return;

        inner.Write(_out.WrittenSpan);
        _out.ResetWrittenCount();
    }

    private async Task PassAsync(CancellationToken cancellationToken)
    {
        if (_out.WrittenCount == 0) return;

        await inner.WriteAsync(_out.WrittenMemory, cancellationToken).ConfigureAwait(false);
        _out.ResetWrittenCount();
    }
}

/// <summary>
///     What is read is what a transform makes of what the stream under it gives: the stream is read only as far as it has to be for the
///     transform to let something out (a compressor holds on to what it has until it has a block), and not at all once the transform is done.
/// </summary>
internal sealed class PumpReadStream(Stream inner, ByteTransform transform, int maxPull = 4096) : Stream
{
    private readonly ArrayBufferWriter<byte> _ready = new();
    private          int                     _taken;
    private          bool                    _finished;
    private          Exception?              _failure;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override int Read(Span<byte> buffer)
    {
        var bytes = new byte[buffer.Length];
        int n     = Read(bytes, 0, bytes.Length);
        bytes.AsSpan(0, n).CopyTo(buffer);
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return 0;

        var pull = new byte[Math.Clamp(buffer.Length, 1, maxPull)];
        while (_ready.WrittenCount - _taken == 0)
        {
            if (_failure is not null)
            {
                // the transform failed in the middle of a piece: what it let out before that was given first, and now the failure is
                var failure = _failure;
                _failure  = null;
                _finished = true;
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }

            if (_finished) return 0;

            if (_taken > 0)
            {
                _ready.ResetWrittenCount();
                _taken = 0;
            }

            if (transform.IsDone)
            {
                _finished = true;
                transform.Finish(_ready);
                continue;
            }

            int n = await inner.ReadAsync(pull, cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                _finished = true;
                transform.Finish(_ready);
            }
            else
            {
                try
                {
                    transform.Feed(pull.AsSpan(0, n), _ready);
                }
                catch (Exception ex) when (_ready.WrittenCount - _taken > 0)
                {
                    _failure = ex;
                }
            }
        }

        int give = Math.Min(buffer.Length, _ready.WrittenCount - _taken);
        _ready.WrittenSpan.Slice(_taken, give).CopyTo(buffer.Span);
        _taken += give;
        return give;
    }
}

/// <summary>Passes everything through and keeps a copy of what went by since it was last emptied.</summary>
public sealed class TapStream(Stream inner) : Stream
{
    private readonly List<byte> _kept = [];

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>How many times the stream was asked for bytes (or given some).</summary>
    public int Calls { get; private set; }

    /// <summary>How many bytes went by, ever.</summary>
    public long Total { get; private set; }

    /// <summary>The bytes that went by since <see cref="Take" /> was last called.</summary>
    public int Waiting => _kept.Count;

    /// <summary>The bytes that went by since the last time, which are forgotten.</summary>
    public byte[] Take()
    {
        var bytes = _kept.ToArray();
        _kept.Clear();
        return bytes;
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = inner.Read(buffer, offset, count);
        Note(buffer.AsSpan(offset, n));
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int n = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Note(buffer.Span[..n]);
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        Note(buffer.AsSpan(offset, count));
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        Note(buffer.Span);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    private void Note(ReadOnlySpan<byte> bytes)
    {
        Calls++;
        Total += bytes.Length;
        _kept.AddRange(bytes.ToArray());
    }
}

/// <summary>Gives at most so many bytes to a read and takes at most so many in a write: the way to find out how code copes with a stream that never gives more than a byte.</summary>
internal sealed class TrickleStream(Stream inner, int most, bool reading) : Stream
{
    public override bool CanRead => reading && inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => !reading && inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, most));

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer[..Math.Min(buffer.Length, most)], cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
    {
        for (int at = 0; at < count; at += most)
        {
            inner.Write(buffer, offset + at, Math.Min(most, count - at));
            inner.Flush();
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        for (int at = 0; at < buffer.Length; at += most)
        {
            await inner.WriteAsync(buffer.Slice(at, Math.Min(most, buffer.Length - at)), cancellationToken).ConfigureAwait(false);
            await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}

// ---- the catalog ---------------------------------------------------------------------------------

/// <summary>An adapter ready to be part of a chain: the stream to read from or write to, and the transform inside it when it has one (to ask for its report).</summary>
public sealed record AdapterLayer(Stream Stream, ByteTransform? Transform = null);

/// <summary>A kind of adapter: what it is called, what its argument is, and how to put it on either side of a stream.</summary>
public sealed class AdapterDef
{
    internal AdapterDef(string key, string title, string description, string argHint = "", string argDefault = "")
    {
        Key         = key;
        Title       = title;
        Description = description;
        ArgHint     = argHint;
        ArgDefault  = argDefault;
    }

    public string Key { get; }

    public string Title { get; }

    public string Description { get; }

    /// <summary>What the argument is, for the hint in the box; empty when the adapter has none.</summary>
    public string ArgHint { get; }

    public string ArgDefault { get; }

    internal Func<string, ByteTransform>? Push { get; init; }

    internal Func<Stream, string, Stream>? Pull { get; init; }

    internal Func<Stream, string, Stream>? PullWrite { get; init; }

    public bool CanRead => Push is not null || Pull is not null;

    public bool CanWrite => Push is not null || PullWrite is not null;

    /// <summary>Puts the adapter on a stream that is read: reading the result gives what the adapter makes of what the stream gives.</summary>
    public AdapterLayer MakeReader(Stream inner, string arg)
    {
        if (Pull is not null) return new AdapterLayer(Pull(inner, arg));
        if (Push is null) throw new AdapterException($"{Title} cannot be put on what is read.");

        var transform = Push(arg);
        return new AdapterLayer(new PumpReadStream(inner, transform), transform);
    }

    /// <summary>Puts the adapter on a stream that is written: what is written to the result reaches the stream changed.</summary>
    public AdapterLayer MakeWriter(Stream inner, string arg)
    {
        if (PullWrite is not null) return new AdapterLayer(PullWrite(inner, arg));
        if (Push is null) throw new AdapterException($"{Title} cannot be put on what is written.");

        var transform = Push(arg);
        return new AdapterLayer(new PushWriteStream(inner, transform), transform);
    }

    /// <summary>Checks the argument (builds the adapter on nothing); returns what is wrong with it, or null.</summary>
    public string? Check(string arg)
    {
        try
        {
            using var probe = new MemoryStream();
            var layer = CanRead ? MakeReader(probe, arg) : MakeWriter(probe, arg);
            layer.Stream.Dispose();
            return null;
        }
        catch (AdapterException ex)
        {
            return ex.Message;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or CryptographicException or InvalidDataException)
        {
            return ex.Message;
        }
    }
}

/// <summary>The adapters the Stream Lab knows.</summary>
public static class StreamAdapters
{
    public static IReadOnlyList<AdapterDef> All { get; } = Build();

    public static AdapterDef? Find(string? key)
    {
        return All.FirstOrDefault(a => string.Equals(a.Key, key?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static List<AdapterDef> Build()
    {
        var all = new List<AdapterDef>
                  {
                      new("gzip", "GZip compress", "Compresses to the GZip format. On a read it compresses what the stream gives.", "level: fastest, optimal, smallest, none", "optimal")
                      {
                          Push = arg => new SinkTransform(s => new GZipStream(s, Level(arg), true))
                      },
                      new("gunzip", "GZip decompress", "Reads GZip data into what it was. Only on a read.") { Pull = (s, _) => new GZipStream(s, CompressionMode.Decompress, true) },
                      new("deflate", "Deflate compress", "Compresses to raw Deflate.", "level: fastest, optimal, smallest, none", "optimal")
                      {
                          Push = arg => new SinkTransform(s => new DeflateStream(s, Level(arg), true))
                      },
                      new("inflate", "Deflate decompress", "Reads raw Deflate data into what it was. Only on a read.") { Pull = (s, _) => new DeflateStream(s, CompressionMode.Decompress, true) },
                      new("zlib", "ZLib compress", "Compresses to the ZLib format.", "level: fastest, optimal, smallest, none", "optimal")
                      {
                          Push = arg => new SinkTransform(s => new ZLibStream(s, Level(arg), true))
                      },
                      new("unzlib", "ZLib decompress", "Reads ZLib data into what it was. Only on a read.") { Pull = (s, _) => new ZLibStream(s, CompressionMode.Decompress, true) },
                      new("brotli", "Brotli compress", "Compresses to the Brotli format.", "level: fastest, optimal, smallest, none", "optimal")
                      {
                          Push = arg => new SinkTransform(s => new BrotliStream(s, Level(arg), true))
                      },
                      new("unbrotli", "Brotli decompress", "Reads Brotli data into what it was. Only on a read.") { Pull = (s, _) => new BrotliStream(s, CompressionMode.Decompress, true) },
                      new("base64", "Base64 encode", "Writes the bytes as Base64 text.") { Push = _ => new Base64EncodeTransform() },
                      new("unbase64", "Base64 decode", "Reads Base64 text into the bytes it stands for (white space is skipped).") { Push = _ => new Base64DecodeTransform() },
                      new("hex", "Hex encode", "Writes the bytes as hex digits.", "upper or lower", "lower") { Push = arg => new HexEncodeTransform(arg.Trim().Equals("upper", StringComparison.OrdinalIgnoreCase)) },
                      new("unhex", "Hex decode", "Reads hex digits into the bytes they stand for (white space is skipped).") { Push = _ => new HexDecodeTransform() },
                      new("xor", "XOR with a key", "Exclusive-or with a key that goes round and round. The same adapter undoes itself.", "the key: hex (0xA5 or A5FF) or text", "0xFF")
                      {
                          Push = arg => new XorTransform(Key(arg))
                      },
                      new("not", "Invert bits", "Turns every bit around.") { Push = _ => new MapTransform(b => (byte)~b) },
                      new("upper", "Upper case (ASCII)", "Turns a-z into A-Z; every other byte is left alone.") { Push = _ => new MapTransform(b => b is >= (byte)'a' and <= (byte)'z' ? (byte)(b - 32) : b) },
                      new("lower", "Lower case (ASCII)", "Turns A-Z into a-z; every other byte is left alone.") { Push = _ => new MapTransform(b => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b) },
                      new("crlf", "Line ends to CR LF", "Puts a carriage return before every line feed that has none.") { Push = _ => new LineEndingTransform(true) },
                      new("lf", "Line ends to LF", "Drops the carriage return of every CR LF.") { Push = _ => new LineEndingTransform(false) },
                      new("transcode", "Change text encoding", "Reads the bytes as text in one encoding and writes it in another; a character split between two pieces is not broken.", "from>to, such as utf-8>utf-16", "utf-8>utf-16")
                      {
                          Push = arg => Transcode(arg)
                      },
                      new("skip", "Skip the first bytes", "Drops the first bytes that go through.", "how many", "16") { Push = arg => new SkipTransform(Count(arg)) },
                      new("take", "Take only the first bytes", "Lets the first bytes through and then ends the data (a read stops asking the stream).", "how many", "16") { Push = arg => new TakeTransform(Count(arg)) },
                      new("aes", "AES encrypt", "Encrypts with AES-CBC and PKCS7 padding. For experiments: the key is not derived from anything.", "key hex (32, 48 or 64 digits) and optionally :iv hex (32 digits)", "000102030405060708090A0B0C0D0E0F")
                      {
                          Push = arg => new SinkTransform(s => new CryptoStream(s, MakeAes(arg).CreateEncryptor(), CryptoStreamMode.Write, true))
                      },
                      new("unaes", "AES decrypt", "Decrypts AES-CBC data with PKCS7 padding. Only on a read.", "key hex (32, 48 or 64 digits) and optionally :iv hex (32 digits)", "000102030405060708090A0B0C0D0E0F")
                      {
                          Pull = (s, arg) => new CryptoStream(s, MakeAes(arg).CreateDecryptor(), CryptoStreamMode.Read, true)
                      },
                      new("trickle", "Trickle: a few bytes at a time", "Gives a read, and takes a write, only so many bytes at a time: the way to see how code copes with a stream that is slow to give.", "most bytes at once", "1")
                      {
                          Pull      = (s, arg) => new TrickleStream(s, Math.Max(1, (int)Count(arg)), true),
                          PullWrite = (s, arg) => new TrickleStream(s, Math.Max(1, (int)Count(arg)), false)
                      },
                      new("meter", "Meter", "Lets everything through and counts it: how many bytes, in how many pieces.") { Push = _ => new MeterTransform() },
                      new("hash", "Digest", "Lets everything through and works out its digest.", "sha256, sha1, sha384, sha512 or md5", "sha256") { Push = arg => new HashTransform(Hash(arg)) }
                  };
        return all;
    }

    private static CompressionLevel Level(string arg)
    {
        return arg.Trim().ToLowerInvariant() switch
               {
                   "" or "optimal"  => CompressionLevel.Optimal,
                   "fastest"        => CompressionLevel.Fastest,
                   "smallest"       => CompressionLevel.SmallestSize,
                   "none" or "store" => CompressionLevel.NoCompression,
                   _                => throw new AdapterException("The level is fastest, optimal, smallest or none.")
               };
    }

    private static long Count(string arg)
    {
        string text = arg.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out long hex) && hex >= 0) return hex;
        if (long.TryParse(text, out long n) && n >= 0) return n;

        throw new AdapterException("Give a number of bytes (0 or more).");
    }

    private static byte[] Key(string arg)
    {
        string text = arg.Trim();
        if (text.Length == 0) throw new AdapterException("Give the key.");

        // 0xA5FF and A5FF are hex when they only have hex digits; anything else is the bytes of the text
        bool looksHex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || (text.Length % 2 == 0 && text.All(Uri.IsHexDigit) && text.Length >= 2 && text.Any(char.IsDigit));
        if (looksHex && StreamPayload.TryParseHex(text, out var bytes) && bytes.Length > 0) return bytes;

        return Encoding.UTF8.GetBytes(text);
    }

    private static ByteTransform Transcode(string arg)
    {
        var parts = arg.Split('>', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2) throw new AdapterException("Say it as from>to, such as utf-8>utf-16.");

        try
        {
            return new TranscodeTransform(Encoding.GetEncoding(parts[0]), Encoding.GetEncoding(parts[1]));
        }
        catch (ArgumentException)
        {
            throw new AdapterException($"There is no such encoding as '{parts[0]}' or '{parts[1]}'.");
        }
    }

    private static HashAlgorithmName Hash(string arg)
    {
        return arg.Trim().ToLowerInvariant() switch
               {
                   "" or "sha256" => HashAlgorithmName.SHA256,
                   "sha1"         => HashAlgorithmName.SHA1,
                   "sha384"       => HashAlgorithmName.SHA384,
                   "sha512"       => HashAlgorithmName.SHA512,
                   "md5"          => HashAlgorithmName.MD5,
                   _              => throw new AdapterException("The algorithm is sha256, sha1, sha384, sha512 or md5.")
               };
    }

    private static Aes MakeAes(string arg)
    {
        string[] parts = arg.Split(':', 2, StringSplitOptions.TrimEntries);
        if (!StreamPayload.TryParseHex(parts[0], out var key) || key.Length is not (16 or 24 or 32)) throw new AdapterException("The key is 32, 48 or 64 hex digits (16, 24 or 32 bytes).");

        byte[] iv = new byte[16];
        if (parts.Length == 2 && (!StreamPayload.TryParseHex(parts[1], out iv) || iv.Length != 16)) throw new AdapterException("The IV is 32 hex digits (16 bytes).");

        var aes = System.Security.Cryptography.Aes.Create();
        aes.Key     = key;
        aes.IV      = iv;
        aes.Mode    = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        return aes;
    }
}

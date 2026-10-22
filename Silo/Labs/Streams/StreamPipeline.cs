using System.IO;
using System.Diagnostics;

namespace Silo.Labs.Streams;

/// <summary>What one adapter of a chain has to say after the data went through it.</summary>
public sealed record AdapterReport(AdapterSpec Spec, AdapterDef Def, string? Text);

/// <summary>How a read asks the stream for its bytes.</summary>
public enum ReadMode
{
    /// <summary>One call to read, for up to so many bytes: what a program usually does, and it may get fewer.</summary>
    Once,

    /// <summary>Calls to read until it has so many bytes, or the data ends.</summary>
    Exactly,

    /// <summary>Calls to read until the data ends (or so many bytes have been read).</summary>
    ToEnd
}

/// <summary>What a read came to.</summary>
/// <param name="Output">What came out of the end of the chain.</param>
/// <param name="Input">What was read from the stream itself to make it.</param>
/// <param name="Calls">How many times the stream was asked.</param>
/// <param name="Ended">The data ended (a read gave nothing).</param>
/// <param name="Error">What went wrong, if something did; <paramref name="Output" /> is what came before.</param>
public sealed record ReadStep(byte[] Output, byte[] Input, int Calls, bool Ended, TimeSpan Elapsed, string? Error);

/// <summary>What a write came to.</summary>
/// <param name="Written">How many bytes were given to the chain.</param>
/// <param name="OnStream">What reached the stream.</param>
public sealed record WriteStep(int Written, byte[] OnStream, TimeSpan Elapsed, string? Error, IReadOnlyList<AdapterReport> Reports);

/// <summary>A stream with a chain of adapters on it, read from: what comes out of the end of the chain is what the adapters made of what the stream gave.</summary>
public sealed class ReadPipeline : IDisposable
{
    private readonly TapStream                                    _tap;
    private readonly List<(AdapterSpec Spec, AdapterDef Def, AdapterLayer Layer)> _layers = [];
    private readonly Stream                                       _tail;

    private ReadPipeline(Stream source, IEnumerable<AdapterSpec>? specs)
    {
        _tap = new TapStream(source);
        Stream current = _tap;
        foreach (var spec in (specs ?? []).Where(s => s.Enabled && s.Side != AdapterSide.Write))
        {
            var def = StreamAdapters.Find(spec.Key) ?? throw new AdapterException($"There is no adapter called '{spec.Key}'.");
            if (!def.CanRead) continue;

            AdapterLayer layer;
            try
            {
                layer = def.MakeReader(current, spec.Arg);
            }
            catch (Exception ex) when (ex is not AdapterException)
            {
                throw new AdapterException($"{def.Title}: {ex.Message}");
            }

            _layers.Add((spec, def, layer));
            current = layer.Stream;
        }

        _tail = current;
    }

    /// <summary>The data ended.</summary>
    public bool Ended { get; private set; }

    /// <summary>How many bytes were read from the stream itself.</summary>
    public long InputTotal => _tap.Total;

    /// <summary>How many bytes came out.</summary>
    public long OutputTotal { get; private set; }

    /// <summary>The adapters that apply to reads.</summary>
    public int Length => _layers.Count;

    /// <summary>Puts the adapters on the stream. Throws <see cref="AdapterException" /> when one cannot be made (an argument that is not valid).</summary>
    public static ReadPipeline Build(Stream source, IEnumerable<AdapterSpec>? specs = null)
    {
        return new ReadPipeline(source, specs);
    }

    public async Task<ReadStep> ReadAsync(int count, ReadMode mode, CancellationToken cancellationToken = default)
    {
        var watch  = Stopwatch.StartNew();
        int calls0 = _tap.Calls;
        var output = new MemoryStream();
        var buffer = new byte[Math.Clamp(count, 1, 1 << 20)];
        string? error = null;

        try
        {
            int wanted = Math.Max(count, 1);
            while (!Ended && (mode == ReadMode.ToEnd || output.Length < wanted))
            {
                int ask = (int)Math.Min(buffer.Length, mode == ReadMode.ToEnd ? buffer.Length : wanted - output.Length);
                int n   = await _tail.ReadAsync(buffer.AsMemory(0, ask), cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    Ended = true;
                    break;
                }

                output.Write(buffer, 0, n);
                if (mode == ReadMode.Once) break;
                if (mode == ReadMode.ToEnd && output.Length >= wanted) break;
            }
        }
        catch (OperationCanceledException)
        {
            error = "Stopped.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or System.Security.Cryptography.CryptographicException or TimeoutException or ObjectDisposedException
                                       or InvalidOperationException or NotSupportedException or System.Net.Sockets.SocketException)
        {
            error = ex.InnerException is { } inner && ex is IOException ? $"{ex.Message} ({inner.Message})" : ex.Message;
        }

        watch.Stop();
        OutputTotal += output.Length;
        return new ReadStep(output.ToArray(), _tap.Take(), _tap.Calls - calls0, Ended, watch.Elapsed, error);
    }

    public IReadOnlyList<AdapterReport> Reports()
    {
        return _layers.Select(l => new AdapterReport(l.Spec, l.Def, l.Layer.Transform?.Report)).ToList();
    }

    public void Dispose()
    {
        for (int i = _layers.Count - 1; i >= 0; i--)
        {
            try
            {
                _layers[i].Layer.Stream.Dispose();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or System.Security.Cryptography.CryptographicException or ObjectDisposedException)
            {
                // an adapter that was left in the middle of its data
            }
        }
    }
}

/// <summary>Writes through a chain of adapters to a stream.</summary>
public static class WritePipeline
{
    /// <summary>
    ///     Writes <paramref name="payload" /> to <paramref name="target" /> through the adapters that apply to writes (the data goes through the first adapter first),
    ///     lets each adapter finish (a compressor writes what it kept back), and flushes. Throws <see cref="AdapterException" /> when an adapter cannot be made.
    /// </summary>
    public static async Task<WriteStep> WriteAsync(Stream target, IEnumerable<AdapterSpec> specs, byte[] payload, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        var tap   = new TapStream(target);
        var chain = new List<(AdapterSpec Spec, AdapterDef Def, AdapterLayer Layer)>();

        // the last adapter is next to the stream, so the chain is made from the end
        Stream current = tap;
        foreach (var spec in specs.Where(s => s.Enabled && s.Side != AdapterSide.Read).Reverse())
        {
            var def = StreamAdapters.Find(spec.Key) ?? throw new AdapterException($"There is no adapter called '{spec.Key}'.");
            if (!def.CanWrite) continue;

            AdapterLayer layer;
            try
            {
                layer = def.MakeWriter(current, spec.Arg);
            }
            catch (Exception ex) when (ex is not AdapterException)
            {
                throw new AdapterException($"{def.Title}: {ex.Message}");
            }

            chain.Insert(0, (spec, def, layer));
            current = layer.Stream;
        }

        string? error = null;
        int written   = 0;
        try
        {
            await current.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            written = payload.Length;

            // from the first adapter on, each lets out what it kept back
            foreach (var (_, _, layer) in chain)
            {
                if (layer.Stream is PushWriteStream push) await push.FinishAsync(cancellationToken).ConfigureAwait(false);
                else await layer.Stream.DisposeAsync().ConfigureAwait(false);
            }

            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            error = "Stopped.";
        }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidDataException or System.Security.Cryptography.CryptographicException or TimeoutException or ObjectDisposedException
                                       or InvalidOperationException or NotSupportedException or System.Net.Sockets.SocketException)
        {
            error = ex.InnerException is { } inner && ex is IOException ? $"{ex.Message} ({inner.Message})" : ex.Message;
        }

        watch.Stop();
        var reports = chain.Select(l => new AdapterReport(l.Spec, l.Def, l.Layer.Transform?.Report)).ToList();
        return new WriteStep(written, tap.Take(), watch.Elapsed, error, reports);
    }
}

/// <summary>Post-processing: adapters put on bytes that are already recorded, rather than on the stream.</summary>
public static class StreamPost
{
    /// <summary>What the adapters (the ones that apply to a read) make of the bytes. When one fails, the bytes before the failure are returned with what went wrong.</summary>
    public static (byte[] Bytes, string? Problem) Apply(ReadOnlySpan<byte> input, IEnumerable<AdapterSpec> specs)
    {
        var chain = specs.Where(s => s.Enabled && s.Side != AdapterSide.Write).ToList();
        if (chain.Count == 0) return (input.ToArray(), null);

        using var source = new MemoryStream(input.ToArray());
        ReadPipeline pipeline;
        try
        {
            pipeline = ReadPipeline.Build(source, chain);
        }
        catch (AdapterException ex)
        {
            return (input.ToArray(), ex.Message);
        }

        using (pipeline)
        {
            var step = pipeline.ReadAsync(int.MaxValue / 2, ReadMode.ToEnd).GetAwaiter().GetResult();
            return (step.Output, step.Error);
        }
    }
}

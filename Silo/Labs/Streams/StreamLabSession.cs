using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace Silo.Labs.Streams;

public enum ReelState
{
    Closed,
    Opening,
    Open,
    Failed
}

/// <summary>Which of the strips is looked at.</summary>
public enum StripSide
{
    /// <summary>What came out of the end of the adapters.</summary>
    Output,

    /// <summary>What was read from the stream itself.</summary>
    Input,

    /// <summary>What reached the stream when something was written.</summary>
    Written,

    /// <summary>What the post-processing adapters make of the output strip.</summary>
    Processed
}

/// <summary>A piece cut out of a strip.</summary>
public sealed class StreamCut
{
    public string Label { get; set; } = string.Empty;

    public string Origin { get; set; } = string.Empty;

    public byte[] Data { get; set; } = [];
}

/// <summary>One Stream Lab window: the reel, the chain of adapters on it, the strips of what was read and written, the marks and the cuts.</summary>
public sealed class StreamLabSession : LabSession<StreamSetup>, ILabLive
{
    private static readonly LabSaved<StreamSetup> Saved = new("stream");

    private readonly List<LabInspector> _inspectors;
    private readonly List<StreamCut>    _cuts = [];
    private readonly List<string>       _log  = [];
    private          StreamSetup        _setup = new();
    private          Reel?              _reel;
    private          ReadPipeline?      _read;
    private          string?            _adapterProblem;
    private          IReadOnlyList<AdapterReport> _writeReports = [];
    private          ByteStrip          _out, _in, _wire;
    private          CancellationTokenSource? _cts;
    private          bool               _busy;
    private          bool               _stopped;
    private          bool               _playing;
    private          bool               _disposed;
    private          bool               _dirty;
    private          bool               _started;
    private          bool               _rebuildPending;

    public StreamLabSession(StreamLab lab) : base(lab)
    {
        _out = new ByteStrip(lab.BufferLimit);
        _in  = new ByteStrip(lab.BufferLimit);
        _wire = new ByteStrip(lab.BufferLimit);
        _inspectors =
        [
            new StreamPropertiesInspector(this),
            new StreamAdaptersInspector(this),
            new StreamTextInspector(this),
            new StreamCutsInspector(this),
            new StreamLogInspector(this)
        ];
    }

    private StreamLab Lab_ => (StreamLab)LabOf;

    public StreamSetup Setup => _setup;

    /// <summary>Says something in the status line (what the views do when they have something to report).</summary>
    public void Say(string text) => Status = text;

    public Reel? Reel => _reel;

    public ReelState State { get; private set; }

    /// <summary>The reason the reel could not be opened, when it could not.</summary>
    public string? OpenError { get; private set; }

    public bool IsOpen => State == ReelState.Open && _reel is not null;

    public bool IsBusy => _busy;

    public bool IsPlaying => _playing;

    public bool IsDirty => _dirty;

    /// <summary>What the adapters make of what is read, or null when they cannot be put on the stream (see <see cref="AdapterProblem" />).</summary>
    public ReadPipeline? Pipeline => _read;

    public string? AdapterProblem => _adapterProblem;

    public IReadOnlyList<StreamCut> Cuts => _cuts;

    public IReadOnlyList<string> Log => _log;

    public StripSide ViewSide { get; private set; } = StripSide.Output;

    public long? MarkIn { get; private set; }

    public long? MarkOut { get; private set; }

    /// <summary>The Lab's own view of the strip (what the hex view shows).</summary>
    public ByteStrip StripOf(StripSide side) => side switch { StripSide.Input => _in, StripSide.Written => _wire, StripSide.Processed => ProcessedStrip(), _ => _out };

    /// <summary>What went wrong putting the post-processing adapters on the output strip, or null.</summary>
    public string? PostProblem { get; private set; }

    private ByteStrip? _processed;
    private string     _processedKey = string.Empty;

    /// <summary>The output strip after the post-processing adapters (made again when the output or the adapters have changed).</summary>
    private ByteStrip ProcessedStrip()
    {
        string key = $"{_out.Version}|{string.Join(';', _setup.Post.Select(p => p.ToString()))}";
        if (_processed is not null && key == _processedKey) return _processed;

        _processedKey = key;
        _processed    = new ByteStrip(BufferLimit);
        var (bytes, problem) = StreamPost.Apply(_out.Span, _setup.Post);
        PostProblem = problem;
        _processed.Add(bytes);
        return _processed;
    }

    private long BufferLimit => ((StreamLab)LabOf).BufferLimit;

    public ByteStrip Strip => StripOf(ViewSide);

    public event EventHandler? Changed;

    /// <summary>A different reel was opened: the boxes show it afresh.</summary>
    public event EventHandler? Loaded;

    /// <summary>The adapters were added, removed, moved or changed.</summary>
    public event EventHandler? AdaptersChanged;

    public override StreamSetup? CurrentValue => _setup;

    public override string ValueText => _setup.Source.ToString();

    public override string Title => $"{LabOf.Name} - {(string.IsNullOrWhiteSpace(_setup.Name) ? _setup.Source.Label : _setup.Name)}{(_dirty ? " *" : string.Empty)}";

    /// <summary>2: the Library, Recent, Presets and Problems panes are the window's own now; windows saved with the old arrangement start afresh.</summary>
    public override int LayoutVersion => 2;

    public override IReadOnlyList<LabInspector> Inspectors => _inspectors;

    // ---- what the reel is like -------------------------------------------------------------------

    public bool CanRead => IsOpen && _reel!.Stream.CanRead;

    public bool CanWrite => IsOpen && _reel!.Stream.CanWrite;

    public bool CanSeek => IsOpen && _reel!.Stream.CanSeek;

    /// <summary>Where the playhead is: the stream's position when it has one.</summary>
    public long? Position
    {
        get
        {
            try
            {
                return CanSeek ? _reel!.Stream.Position : null;
            }
            catch (Exception ex) when (ex is NotSupportedException or ObjectDisposedException or IOException)
            {
                return null;
            }
        }
    }

    public long? Length
    {
        get
        {
            try
            {
                return CanSeek ? _reel!.Stream.Length : null;
            }
            catch (Exception ex) when (ex is NotSupportedException or ObjectDisposedException or IOException)
            {
                return null;
            }
        }
    }

    /// <summary>How the playhead is described: the position of a stream that has one, otherwise how much has been read.</summary>
    public string PositionText
    {
        get
        {
            if (!IsOpen) return State == ReelState.Opening ? "Opening..." : "Not open";
            if (Position is { } p) return $"Position {p:N0}{(Length is { } l ? $" of {l:N0}" : string.Empty)}";

            return $"Position not known (the stream cannot seek); {_read?.InputTotal ?? 0:N0} byte(s) read so far";
        }
    }

    public IReadOnlyList<AdapterReport> ReadReports => _read?.Reports() ?? [];

    public IReadOnlyList<AdapterReport> WriteReports => _writeReports;

    /// <summary>What the adapter has to say: what it counted or worked out on a read, and on the last write.</summary>
    public string? ReportOf(AdapterSpec spec)
    {
        string? read  = ReadReports.FirstOrDefault(r => ReferenceEquals(r.Spec, spec))?.Text;
        string? write = _writeReports.FirstOrDefault(r => ReferenceEquals(r.Spec, spec))?.Text;
        if (read is null && write is null) return null;

        return string.Join("\n", new[] { read is null ? null : "reads: " + read, write is null ? null : "writes: " + write }.Where(t => t is not null));
    }

    // ---- opening ---------------------------------------------------------------------------------

    protected override async Task OpenValueAsync(StreamSetup value)
    {
        await Replace(value.Clone(), false);
        if (value.Step < 1) _setup.Step = Lab_.DefaultStep;

        // opening has no effect on anything for these; a connection, or a file that is created or emptied, is opened when the person says so
        var s = _setup.Source;
        bool safe = s.Kind is StreamKind.Memory or StreamKind.Existing || (s.Kind == StreamKind.File && s.Mode == FileMode.Open && s.Access == FileAccess.Read);
        if (safe) await OpenReelAsync();
    }

    protected override async Task OpenDefaultAsync()
    {
        if (_started) return;

        await Replace(new StreamSetup { Name = string.Empty, Source = StreamSource.ForMemory(), Step = Lab_.DefaultStep }, false);
        await OpenReelAsync();
    }

    protected override void OnAttached()
    {
        Lab_.Parameters.Changed += OnParametersChanged;
        if (!_started) _ = OpenDefaultAsync();
    }

    private void OnParametersChanged(LabParameter? _)
    {
        _out.Limit  = Lab_.BufferLimit;
        _in.Limit   = Lab_.BufferLimit;
        _wire.Limit = Lab_.BufferLimit;
        Application.Current?.Dispatcher.InvokeAsync(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    protected override void OnDispose()
    {
        _disposed = true;
        _playing  = false;
        Lab_.Parameters.Changed -= OnParametersChanged;
        _cts?.Cancel();
        DropReel();
    }

    /// <summary>Starts again from another setup (the reel that was open is closed).</summary>
    public async Task Replace(StreamSetup setup, bool dirty)
    {
        _started = true;
        _playing = false;
        _cts?.Cancel();
        DropReel();
        _setup = setup;
        _dirty = dirty;
        _cuts.Clear();
        _log.Clear();
        _writeReports = [];
        ClearStrips();
        MarkIn = MarkOut = null;
        State = ReelState.Closed;
        OpenError = null;
        _adapterProblem = null;
        Loaded?.Invoke(this, EventArgs.Empty);
        AdaptersChanged?.Invoke(this, EventArgs.Empty);
        Touch();
        await Task.CompletedTask;
    }

    private void ClearStrips()
    {
        _out.Clear();
        _in.Clear();
        _wire.Clear();
    }

    private void DropReel()
    {
        _read?.Dispose();
        _read = null;
        if (_reel is not null)
        {
            var reel = _reel;
            _reel = null;
            _ = reel.DisposeAsync().AsTask();
        }
    }

    /// <summary>Opens the reel the setup describes (the one that was open is closed first).</summary>
    public async Task OpenReelAsync()
    {
        if (_busy)
        {
            Status = "Busy: wait for the " + _busyWhat + " to finish, or stop it.";
            return;
        }

        if (_setup.Source.Problem() is { } problem)
        {
            OpenError = problem;
            State     = ReelState.Failed;
            Status    = problem;
            Touch();
            return;
        }

        _playing = false;
        DropReel();
        State     = ReelState.Opening;
        OpenError = null;
        Touch();

        await Run("opening", async ct =>
                             {
                                 try
                                 {
                                     var reel = await Reel.OpenAsync(_setup.Source.Clone(), Lab_.TimeLimit ?? TimeSpan.FromHours(1), ct).ConfigureAwait(true);
                                     if (_disposed)
                                     {
                                         await reel.DisposeAsync();
                                         return;
                                     }

                                     _reel = reel;
                                     State = ReelState.Open;
                                     Note($"opened {_setup.Source}");
                                     ApplyTimeout();
                                     RebuildRead(true);
                                     Status = $"Opened {_setup.Source.Label}";
                                 }
                                 catch (Exception ex) when (ex is not OperationCanceledException && IsOpenFailure(ex))
                                 {
                                     State     = ReelState.Failed;
                                     OpenError = Describe(ex);
                                     Note("could not open: " + OpenError);
                                     Status = OpenError;
                                 }
                                 catch (OperationCanceledException)
                                 {
                                     State     = ReelState.Failed;
                                     OpenError = "Stopped.";
                                     Status    = "Stopped opening the reel";
                                 }
                             });
    }

    private static bool IsOpenFailure(Exception ex)
    {
        return ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Net.Sockets.SocketException or TimeoutException or System.Security.Authentication.AuthenticationException
            or ArgumentException or NotSupportedException;
    }

    private static string Describe(Exception ex)
    {
        return ex.InnerException is { } inner && ex.Message.Length < 80 ? $"{ex.Message} ({inner.Message})" : ex.Message;
    }

    public void CloseReel()
    {
        if (_busy) _cts?.Cancel();
        _playing = false;
        DropReel();
        State = ReelState.Closed;
        Note("closed");
        Status = "Closed the reel";
        Touch();
    }

    private void ApplyTimeout()
    {
        // a read on a connection that has nothing to give waits for as long as the step may take
        if (_reel is null || Lab_.TimeLimit is not { } limit) return;

        _reel.TrySetTimeout(true, (int)limit.TotalMilliseconds);
        _reel.TrySetTimeout(false, (int)limit.TotalMilliseconds);
    }

    /// <summary>Puts the adapters on the stream again, as they are now, so what is read from here on starts afresh at the position of the stream.</summary>
    private void RebuildRead(bool clearStrips)
    {
        _read?.Dispose();
        _read           = null;
        _adapterProblem = null;
        if (clearStrips)
        {
            _out.Clear();
            _in.Clear();
        }

        if (_reel is null || !_reel.Stream.CanRead) return;

        try
        {
            _read = ReadPipeline.Build(_reel.Stream, _setup.Adapters);
        }
        catch (AdapterException ex)
        {
            _adapterProblem = ex.Message;
        }
    }

    // ---- running a step --------------------------------------------------------------------------

    private string _busyWhat = string.Empty;

    private async Task Run(string what, Func<CancellationToken, Task> body)
    {
        if (_busy)
        {
            Status = "Busy: wait for the " + _busyWhat + " to finish, or stop it.";
            return;
        }

        _busy     = true;
        _busyWhat = what;
        _stopped  = false;
        _cts      = new CancellationTokenSource();
        if (what != "opening" && Lab_.TimeLimit is { } limit) _cts.CancelAfter(limit);
        Touch();
        try
        {
            await body(_cts.Token);
        }
        finally
        {
            _busy = false;
            _cts?.Dispose();
            _cts = null;
            if (_rebuildPending && IsOpen)
            {
                // the adapters were changed while the step was under way
                _rebuildPending = false;
                RebuildRead(false);
            }

            if (!_disposed) Touch();
        }
    }

    /// <summary>Stops what is going on (a read that waits for a connection, Play).</summary>
    public void Stop()
    {
        _playing = false;
        _stopped = true;
        _cts?.Cancel();
        Touch();
    }

    /// <summary>Makes the stream pass on what it has been given and is holding back (a file's buffer, a connection's). Nothing is lost by it, and nothing happens to the position.</summary>
    public void Flush()
    {
        if (!IsOpen || !_reel!.Stream.CanWrite)
        {
            Status = "Nothing to flush: the stream is not open for writing.";
            return;
        }

        try
        {
            _reel.Stream.Flush();
            Note("flushed");
            Status = "Flushed";
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ObjectDisposedException)
        {
            Status = ex.Message;
        }
    }

    /// <summary>Puts the adapters on the stream afresh at its present position, as they are after a seek: what an adapter has half done (a gunzip in the middle) is forgotten.</summary>
    public void Resync()
    {
        if (!IsOpen || _busy)
        {
            Status = !IsOpen ? "The reel is not open." : "Busy: wait for the " + _busyWhat + " to finish, or stop it.";
            return;
        }

        RebuildRead(false);
        Note("adapters started afresh");
        Status = "The adapters start afresh from here";
        Touch();
    }

    private void Touch()
    {
        Changed?.Invoke(this, EventArgs.Empty);
        RaiseValueChanged();
        RaiseActionsChanged();
    }

    private void Note(string line)
    {
        _log.Add($"{DateTime.Now:HH:mm:ss.fff}  {line}");
        if (_log.Count > 1000) _log.RemoveRange(0, 200);
    }

    private string StoppedText(string? error)
    {
        if (error != "Stopped.") return error ?? string.Empty;

        return _stopped ? "Stopped." : $"Gave up after {Lab_.TimeLimit?.TotalMilliseconds:0} ms (the time limit of a step).";
    }

    // ---- reading ---------------------------------------------------------------------------------

    private bool ReadReady(out string why)
    {
        why = string.Empty;
        if (!IsOpen) why = "Open the reel first.";
        else if (!_reel!.Stream.CanRead) why = "This stream cannot be read.";
        else if (_read is null) why = _adapterProblem ?? "The adapters cannot be put on the stream.";

        if (why.Length > 0) Status = why;
        return why.Length == 0;
    }

    /// <summary>Reads from the reel, through the adapters, and adds what came to the strips.</summary>
    public async Task ReadAsync(int count, ReadMode mode)
    {
        if (!ReadReady(out _)) return;

        count = Math.Max(1, count);
        await Run("read", async ct => await ReadCore(count, mode, ct));
    }

    private async Task ReadCore(int count, ReadMode mode, CancellationToken ct)
    {
        if (_read is null) return;

        int ask = mode == ReadMode.ToEnd ? (int)Math.Clamp(_out.Limit - _out.Length, 1, 64 << 20) : count;
        var step = await _read.ReadAsync(ask, mode, ct).ConfigureAwait(true);
        if (_disposed) return;

        _out.Add(step.Output);
        _in.Add(step.Input);
        string how = mode switch { ReadMode.Once => $"read up to {count}", ReadMode.Exactly => $"read exactly {count}", _ => "read all" };
        string line = $"{how}: {step.Output.Length:N0} byte(s) out, {step.Input.Length:N0} from the stream in {step.Calls} call(s), {step.Elapsed.TotalMilliseconds:0.#} ms";
        if (step.Error is not null) line += "; " + StoppedText(step.Error);
        if (step.Ended) line += "; the data ended";
        Note(line);

        Status = step.Error is not null ? StoppedText(step.Error) : step.Ended ? $"End of the data after {_read.OutputTotal:N0} byte(s)" : $"Read {step.Output.Length:N0} byte(s)";
    }

    /// <summary>One byte: a single read that asks for one byte.</summary>
    public Task StepAsync() => ReadAsync(1, ReadMode.Once);

    public Task ReadAllAsync() => ReadAsync(int.MaxValue / 2, ReadMode.ToEnd);

    /// <summary>Reads a step at a time, a moment apart, until the data ends or Play is pressed again.</summary>
    public async Task PlayAsync()
    {
        if (_playing)
        {
            _playing = false;
            Touch();
            return;
        }

        if (!ReadReady(out _)) return;

        _playing = true;
        Touch();
        try
        {
            while (_playing && !_disposed && _read is { Ended: false })
            {
                await Run("read", ct => ReadCore(_setup.Step, ReadMode.Once, ct));
                if (_read?.Ended == true || OpenError is not null) break;

                await Task.Delay(Lab_.PlayInterval);
            }
        }
        finally
        {
            _playing = false;
            if (!_disposed) Touch();
        }
    }

    // ---- position --------------------------------------------------------------------------------

    /// <summary>Moves the stream's position (the adapters start afresh from there).</summary>
    public void SeekTo(long position)
    {
        if (!CanSeek)
        {
            Status = "This stream cannot seek: the position can only go forward, by reading.";
            return;
        }

        if (_busy)
        {
            Status = "Busy: wait for the " + _busyWhat + " to finish, or stop it.";
            return;
        }

        try
        {
            long old = _reel!.Stream.Position;
            position = Math.Max(0, position);
            _reel.Stream.Position = position;
            if (Lab_.ClearOnSeek) ClearStrips();
            RebuildRead(false);
            Note($"position {old:N0} -> {position:N0}");
            Status = $"Position {position:N0}";
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
        {
            Status = ex.Message;
        }

        Touch();
    }

    public void SeekBy(long delta)
    {
        if (Position is { } p) SeekTo(p + delta);
        else SeekTo(0);
    }

    public void Rewind() => SeekTo(0);

    public void WindToEnd()
    {
        if (Length is { } l) SeekTo(l);
        else Status = "This stream cannot seek.";
    }

    /// <summary>
    ///     A step back: the position goes back by so many bytes, and when nothing is changing what is read the strip forgets the same bytes, as
    ///     though the film was wound back; with adapters on the reel the strip is emptied (see the setting).
    /// </summary>
    public void StepBack(int count)
    {
        if (Position is not { } p)
        {
            Status = "This stream cannot seek.";
            return;
        }

        long to = Math.Max(0, p - count);
        bool plain = !_setup.Adapters.Any(a => a.Enabled && a.Side != AdapterSide.Write && StreamAdapters.Find(a.Key)?.CanRead == true);
        SeekTo(to);
        if (plain && !Lab_.ClearOnSeek)
        {
            _out.Trim((int)(p - to));
            _in.Trim((int)(p - to));
            Touch();
        }
    }

    public void SetLength(long length)
    {
        if (!IsOpen || !_reel!.Stream.CanSeek || !_reel.Stream.CanWrite)
        {
            Status = "The length can only be set on a stream that can seek and be written.";
            return;
        }

        try
        {
            _reel.Stream.SetLength(Math.Max(0, length));
            Note($"length set to {length:N0}");
            Status = $"Length is now {length:N0}";
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
        {
            Status = ex.Message;
        }

        Touch();
    }

    public void SetTimeout(bool read, int ms)
    {
        Status = IsOpen && _reel!.TrySetTimeout(read, ms) ? $"{(read ? "Read" : "Write")} timeout is {(ms <= 0 ? "none" : ms + " ms")}" : "This stream has no time limits.";
        Touch();
    }

    public void ShutdownSend()
    {
        Status = IsOpen && _reel!.TryShutdownSend() ? "Stopped sending: the other side sees the end of what it reads." : "There is no connection to stop sending on.";
        Note(Status);
        Touch();
    }

    // ---- writing ---------------------------------------------------------------------------------

    /// <summary>Writes through the adapters that apply to writes, at the stream's position.</summary>
    public async Task WriteAsync(byte[] payload, string what)
    {
        if (!IsOpen)
        {
            Status = "Open the reel first.";
            return;
        }

        if (!_reel!.Stream.CanWrite)
        {
            Status = "This stream cannot be written.";
            return;
        }

        await Run("write", async ct =>
                           {
                               WriteStep step;
                               try
                               {
                                   step = await WritePipeline.WriteAsync(_reel.Stream, _setup.Adapters, payload, ct).ConfigureAwait(true);
                               }
                               catch (AdapterException ex)
                               {
                                   Status = ex.Message;
                                   Note("write refused: " + ex.Message);
                                   return;
                               }

                               if (_disposed) return;

                               _writeReports = step.Reports;
                               _wire.Add(step.OnStream);
                               string line = $"wrote {what}: {step.Written:N0} byte(s) given, {step.OnStream.Length:N0} reached the stream, {step.Elapsed.TotalMilliseconds:0.#} ms";
                               if (step.Error is not null) line += "; " + StoppedText(step.Error);
                               Note(line);
                               Status = step.Error is not null ? StoppedText(step.Error) : $"Wrote {step.OnStream.Length:N0} byte(s) to the stream";

                               // the position moved, so what is read next starts from the new one
                               if (_reel.Stream.CanSeek) RebuildRead(false);
                           });
    }

    // ---- marks and cuts --------------------------------------------------------------------------

    public void ShowStrip(StripSide side)
    {
        ViewSide = side;
        Touch();
    }

    public void SetMarks(long? markIn, long? markOut)
    {
        MarkIn  = markIn;
        MarkOut = markOut;
        Touch();
    }

    public void ClearMarks() => SetMarks(null, null);

    /// <summary>The marked stretch of the strip being looked at (the whole strip when only one mark, or none, is set).</summary>
    public (long From, long To) MarkedRange()
    {
        long length = Strip.Length;
        long from   = Math.Clamp(MarkIn ?? 0, 0, length);
        long to     = Math.Clamp(MarkOut ?? length, from, length);
        return (from, to);
    }

    public bool HasMarks => MarkIn is not null || MarkOut is not null;

    /// <summary>Takes the marked stretch of the strip as a cut.</summary>
    public StreamCut? Cut()
    {
        var (from, to) = MarkedRange();
        if (to <= from)
        {
            Status = "Nothing is marked: set In and Out first.";
            return null;
        }

        var cut = new StreamCut { Data = Strip.Slice(from, to), Origin = $"{ViewSide} {from:N0}..{to:N0} of {_setup.Source.Label}", Label = $"Cut {_cuts.Count + 1}" };
        _cuts.Add(cut);
        Note($"cut {cut.Data.Length:N0} byte(s): {cut.Origin}");
        Status = $"Cut {cut.Data.Length:N0} byte(s) ({_cuts.Count} cut(s) in all)";
        Touch();
        return cut;
    }

    public void RemoveCut(StreamCut cut)
    {
        _cuts.Remove(cut);
        Touch();
    }

    public void MoveCut(StreamCut cut, int delta)
    {
        int at = _cuts.IndexOf(cut);
        int to = Math.Clamp(at + delta, 0, _cuts.Count - 1);
        if (at < 0 || at == to) return;

        _cuts.RemoveAt(at);
        _cuts.Insert(to, cut);
        Touch();
    }

    public byte[] Spliced(IEnumerable<StreamCut>? cuts = null) => (cuts ?? _cuts).SelectMany(c => c.Data).ToArray();

    /// <summary>Opens the cuts, joined, as a new reel in a window of their own.</summary>
    public void SpliceToNewReel(IEnumerable<StreamCut>? cuts = null)
    {
        var data = Spliced(cuts);
        if (data.Length == 0)
        {
            Status = "There is nothing cut yet.";
            return;
        }

        LabHost.Open(LabOf, new StreamSetup { Name = "Splice", Source = StreamSource.ForMemory(data), Step = _setup.Step });
        Status = $"Opened {data.Length:N0} byte(s) as a new reel";
    }

    public void ClearStrip()
    {
        // the processed strip is made from the output, so clearing it clears that
        (ViewSide == StripSide.Processed ? _out : Strip).Clear();
        MarkIn = MarkOut = null;
        Touch();
    }

    // ---- setup -----------------------------------------------------------------------------------

    public void EditSetup(Action<StreamSetup> change)
    {
        change(_setup);
        _dirty = true;
        Touch();
    }

    public void AddAdapter(AdapterSpec spec)
    {
        _setup.Adapters.Add(spec);
        AdaptersEdited($"adapter added: {spec}");
    }

    public void RemoveAdapter(AdapterSpec spec)
    {
        _setup.Adapters.Remove(spec);
        AdaptersEdited($"adapter removed: {spec}");
    }

    public void MoveAdapter(AdapterSpec spec, int delta)
    {
        int at = _setup.Adapters.IndexOf(spec);
        int to = Math.Clamp(at + delta, 0, _setup.Adapters.Count - 1);
        if (at < 0 || at == to) return;

        _setup.Adapters.RemoveAt(at);
        _setup.Adapters.Insert(to, spec);
        AdaptersEdited("adapters reordered");
    }

    public void ChangeAdapter(AdapterSpec spec, Action<AdapterSpec> change)
    {
        change(spec);
        AdaptersEdited($"adapter changed: {spec}");
    }

    public void AddPost(AdapterSpec spec)
    {
        _setup.Post.Add(spec);
        PostEdited($"post-processing added: {spec}");
    }

    public void RemovePost(AdapterSpec spec)
    {
        _setup.Post.Remove(spec);
        PostEdited($"post-processing removed: {spec}");
    }

    public void MovePost(AdapterSpec spec, int delta)
    {
        int at = _setup.Post.IndexOf(spec);
        int to = Math.Clamp(at + delta, 0, _setup.Post.Count - 1);
        if (at < 0 || at == to) return;

        _setup.Post.RemoveAt(at);
        _setup.Post.Insert(to, spec);
        PostEdited("post-processing reordered");
    }

    public void ChangePost(AdapterSpec spec, Action<AdapterSpec> change)
    {
        change(spec);
        PostEdited($"post-processing changed: {spec}");
    }

    private void PostEdited(string note)
    {
        _dirty = true;
        Note(note);
        AdaptersChanged?.Invoke(this, EventArgs.Empty);
        Touch();
    }

    /// <summary>What is wrong with the argument of an adapter, or null.</summary>
    public static string? ProblemOf(AdapterSpec spec)
    {
        var def = StreamAdapters.Find(spec.Key);
        return def is null ? $"There is no adapter called '{spec.Key}'." : def.Check(spec.Arg);
    }

    private void AdaptersEdited(string note)
    {
        _dirty = true;
        Note(note);
        // the adapters start afresh at the position of the stream
        if (IsOpen && !_busy) RebuildRead(false);
        else if (IsOpen) _rebuildPending = true;
        AdaptersChanged?.Invoke(this, EventArgs.Empty);
        Touch();
    }

    // ---- actions ---------------------------------------------------------------------------------

    public override IEnumerable<ProvidedCommand> GlobalActions
    {
        get
        {
            yield return ProvidedCommand.From("new-reel", "_New Reel", NewReel, "TbNew", null, "Start with an empty scratch reel in memory");
            yield return ProvidedCommand.From("open-file", "Open _File...", OpenFile, "TbOpen", null, "Choose a file to open as a reel");
            yield return ProvidedCommand.From("connect", "_Connect...", Connect, "Link", null, "Type tcp://host:port, tls://host:port or https://host to open a connection as a reel");
            yield return ProvidedCommand.From("show-reels", "Show _Reels", () => Host?.ShowInspector("reels"), "Folder17", null, "Show the reels the Lab comes with and the ones you kept");
            yield return ProvidedCommand.From("settings", "Open _Settings", () => Host?.ShowInspector(LabParametersInspector.InspectorId), "Settings", null,
                                              "Show the Lab's parameters: the time limit, the play speed, the size of the strip");
        }
    }

    public override IEnumerable<ProvidedCommand> ContextActions
    {
        get
        {
            yield return ProvidedCommand.From("open-reel", IsOpen ? "Re_open" : "_Open Reel", () => _ = OpenReelAsync(), "Refresh", () => !_busy, "Open the reel described at the top (again, from the start)");
            yield return ProvidedCommand.From("close-reel", "_Close Reel", CloseReel, "TbDelete", () => IsOpen || State == ReelState.Failed, "Close the stream");
            yield return ProvidedCommand.From("step", "_Step", () => _ = StepAsync(), "TbRedo", () => CanRead && !_busy, "Read one byte") with { Gesture = "F10" };
            yield return ProvidedCommand.From("read", "_Read", () => _ = ReadAsync(_setup.Step, ReadMode.Once), "Transfer1", () => CanRead && !_busy, "Read up to the number of bytes in the Bytes box, in one call") with { Gesture = "F5" };
            yield return ProvidedCommand.From("read-exactly", "Read E_xactly", () => _ = ReadAsync(_setup.Step, ReadMode.Exactly), null, () => CanRead && !_busy, "Read until that many bytes have come, or the data ends");
            yield return ProvidedCommand.From("read-all", "Read _All", () => _ = ReadAllAsync(), "Transfer1", () => CanRead && !_busy, "Read to the end of the data");
            yield return ProvidedCommand.From("play", _playing ? "_Pause" : "_Play", () => _ = PlayAsync(), "TbRedo", () => CanRead && (!_busy || _playing), "Read a step after step, a moment apart, until the data ends") with { Gesture = "F9" };
            yield return ProvidedCommand.From("stop", "S_top", Stop, "TbDelete", () => _busy || _playing, "Stop what is going on") with { Gesture = "Escape" };
            yield return ProvidedCommand.From("rewind", "Re_wind", Rewind, "TbOpen", () => CanSeek && !_busy, "Go back to the start of the stream");
            yield return ProvidedCommand.From("cut", "Cu_t", () => Cut(), "TbCopy", () => HasMarks, "Cut the stretch between In and Out of the strip") with { Gesture = "Ctrl+Shift+X" };
        }
    }

    public override IEnumerable<ProvidedCommand> ValueActions
    {
        get
        {
            yield return ProvidedCommand.From("save-reel", "_Save Reel...", SaveReel, "TbSave", () => Saved.IsAvailable, "Keep the source, the adapters and the text to write in the Silo") with { Gesture = "Ctrl+S" };
            yield return ProvidedCommand.From("copy-hex", "Copy as _Hex Dump", () => CopyStrip(s => HexDump.Format(s)), "TbCopy", () => Strip.Length > 0, "Put the strip (or what is marked of it) on the clipboard as a hex dump");
            yield return ProvidedCommand.From("copy-hex-plain", "Copy as Hex _Digits", () => CopyStrip(s => Convert.ToHexString(s)), "TbCopy", () => Strip.Length > 0, "Put the strip (or what is marked of it) on the clipboard as hex digits");
            yield return ProvidedCommand.From("copy-base64", "Copy as _Base64", () => CopyStrip(s => Convert.ToBase64String(s)), "TbCopy", () => Strip.Length > 0, "Put the strip (or what is marked of it) on the clipboard as Base64");
            yield return ProvidedCommand.From("copy-text", "Copy as _Text", () => CopyStrip(s => Encoding.UTF8.GetString(s)), "TbCopy", () => Strip.Length > 0, "Put the strip (or what is marked of it) on the clipboard as UTF-8 text");
            yield return ProvidedCommand.From("save-strip", "Save Strip to _File...", SaveStrip, "TbExport", () => Strip.Length > 0, "Write the strip (or what is marked of it) to a file");
            yield return ProvidedCommand.From("clear-strip", "Clear _Strip", ClearStrip, "TbDelete", () => Strip.Length > 0, "Forget what is in the strip being looked at");
            yield return ProvidedCommand.From("delete-saved", "_Delete Saved Reel", DeleteSaved, "TbDelete", () => Saved.IsAvailable && _setup.Name.Length > 0 && Saved.Exists(_setup.Name), "Remove the reel from the Silo");
        }
    }

    private byte[] MarkedBytes()
    {
        var (from, to) = HasMarks ? MarkedRange() : (0L, (long)Strip.Length);
        return Strip.Slice(from, to);
    }

    private void CopyStrip(Func<byte[], string> format)
    {
        var bytes = MarkedBytes();
        LabUi.Copy(format(bytes));
        Status = $"Copied {bytes.Length:N0} byte(s)";
    }

    private void SaveStrip()
    {
        var bytes  = MarkedBytes();
        var dialog = new SaveFileDialog { Title = "Save the strip", FileName = "strip.bin" };
        if (dialog.ShowDialog() != true) return;

        File.WriteAllBytes(dialog.FileName, bytes);
        Status = $"Saved {bytes.Length:N0} byte(s) to {Path.GetFileName(dialog.FileName)}";
    }

    private async void NewReel()
    {
        if (_dirty && !LabUi.Confirm("This reel has changes that are not kept in the Silo. Start a new one anyway?", "Stream Lab")) return;

        _started = false;
        await OpenDefaultAsync();
    }

    private void OpenFile()
    {
        var dialog = new OpenFileDialog { Title = "Open a file as a reel" };
        if (dialog.ShowDialog() != true) return;

        _ = OpenSource(StreamSource.ForFile(dialog.FileName));
    }

    private void Connect()
    {
        string? text = LabUi.Ask("Connect", "Where to? tcp://host:port, tls://host:port or https://host", _setup.Source.Kind is StreamKind.Tcp or StreamKind.Tls ? _setup.Source.ToString() : "tls://example.com:443");
        if (string.IsNullOrWhiteSpace(text)) return;

        if (!StreamSource.TryParse(text, out var source) || source!.Kind is not (StreamKind.Tcp or StreamKind.Tls))
        {
            Status = "That is not tcp://host:port, tls://host:port or https://host.";
            return;
        }

        _ = OpenSource(source, openNow: true);
    }

    /// <summary>Makes the source the reel (the adapters stay) and opens it.</summary>
    public async Task OpenSource(StreamSource source, bool openNow = true)
    {
        var setup = _setup.Clone();
        setup.Source = source;
        await Replace(setup, true);
        if (openNow) await OpenReelAsync();
    }

    private void SaveReel()
    {
        string? name = LabUi.Ask("Save reel", "What is it called?", string.IsNullOrWhiteSpace(_setup.Name) ? _setup.Source.Label : _setup.Name);
        if (string.IsNullOrWhiteSpace(name)) return;

        _setup.Name = name.Trim();
        Status      = SaveNow();
        Touch();
    }

    private string SaveNow()
    {
        try
        {
            if (_setup.Source.Kind == StreamKind.Existing) return "A stream somebody else has open cannot be kept; open a file or a connection instead.";

            Saved.Save(_setup.Name, _setup);
            _dirty = false;
            return $"Kept {_setup.Name} in the Silo";
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
    }

    private void DeleteSaved()
    {
        if (!LabUi.Confirm($"Remove '{_setup.Name}' from the Silo?", "Stream Lab")) return;

        Status = Saved.Delete(_setup.Name) ? $"Removed {_setup.Name} from the Silo" : $"{_setup.Name} was not kept";
        Touch();
    }

    // ---- terminal --------------------------------------------------------------------------------

    public override IReadOnlyList<LabTerminalCommand> TerminalCommands =>
    [
        new("open", "open <file | tcp://host:port | tls://host:port | mem:hex:..>   make that the reel and open it", async call => await OpenCommand(call.Rest)),
        new("close", "close                    close the reel", _ => { CloseReel(); return Task.FromResult<string?>("Closed."); }),
        new("props", "props                    the stream's properties as they are now", _ => Task.FromResult<string?>(PropsText())),
        new("read", "read [n] [exactly]        read up to n bytes (exactly n with 'exactly'); the step size when n is left out", async call => await ReadCommand(call.Args)),
        new("step", "step                      read one byte", async _ => await AfterAsync(StepAsync)),
        new("all", "all                       read to the end of the data", async _ => await AfterAsync(ReadAllAsync)),
        new("seek", "seek <n | +n | -n | start | end>   move the position", call => Task.FromResult<string?>(SeekCommand(call.Rest))),
        new("write", "write <text | hex:.. | b64:..>   write through the adapters", async call => await WriteCommand(call.Rest)),
        new("pattern", "pattern <kind> <n>       write n made-up bytes: " + string.Join(", ", StreamPayload.Patterns), async call => await PatternCommand(call.Args)),
        new("adapt", "adapt [add <key> [arg] | remove <n> | side <n> read|write|both | off <n> | on <n> | clear]   the adapter chain", call => Task.FromResult<string?>(AdaptCommand(call.Args))),
        new("hex", "hex [in|out|written]      the strip as a hex dump", call => Task.FromResult<string?>(StripCommand(call.Args, true))),
        new("text", "text [in|out|written]     the strip as text", call => Task.FromResult<string?>(StripCommand(call.Args, false))),
        new("mark", "mark <in> <out>           mark a stretch of the strip (offsets; 'mark' alone clears them)", call => Task.FromResult<string?>(MarkCommand(call.Args))),
        new("cut", "cut                       cut the marked stretch", _ => Task.FromResult<string?>(Cut() is { } c ? $"Cut {c.Data.Length:N0} byte(s): {c.Origin}" : Status)),
        new("splice", "splice                    open the cuts, joined, as a new reel", _ => { SpliceToNewReel(); return Task.FromResult<string?>(Status); }),
        new("log", "log                       what was done so far", _ => Task.FromResult<string?>(string.Join('\n', _log))),
        new("save", "save [name]               keep the reel in the Silo", call => Task.FromResult<string?>(SaveCommand(call.Rest))),
        new("json", "json                      the setup as JSON", _ => Task.FromResult<string?>(JsonConvert.SerializeObject(_setup, Formatting.Indented)))
    ];

    private async Task<string?> AfterAsync(Func<Task> action)
    {
        await action();
        return Status;
    }

    private async Task<string?> OpenCommand(string text)
    {
        if (text.Length == 0) return IsOpen ? "Open: " + _setup.Source : "Which one? open <file | tcp://host:port | tls://host:port>";
        if (!StreamSource.TryParse(text, out var source)) return $"'{text}' is not a file or a stream address.";

        await OpenSource(source!);
        return OpenError ?? Status;
    }

    private string PropsText()
    {
        if (!IsOpen) return "The reel is not open.";

        return string.Join('\n', _reel!.Facts().Select(f => $"  {f.Group,-11} {f.Name,-24} {f.Value}")) + "\n" + PositionText;
    }

    private async Task<string?> ReadCommand(IReadOnlyList<string> args)
    {
        int n = _setup.Step;
        var mode = ReadMode.Once;
        foreach (string a in args)
        {
            if (a.Equals("exactly", StringComparison.OrdinalIgnoreCase)) mode = ReadMode.Exactly;
            else if (int.TryParse(a, out int parsed) && parsed > 0) n = parsed;
            else return "read [n] [exactly]";
        }

        int at = _out.Length;
        await ReadAsync(n, mode);
        var got = _out.Slice(at, _out.Length);
        return got.Length == 0 ? Status : Status + "\n" + HexDump.Format(got, at);
    }

    private string SeekCommand(string text)
    {
        string t = text.Trim().ToLowerInvariant();
        if (t.Length == 0) return PositionText;
        if (t == "start") Rewind();
        else if (t == "end") WindToEnd();
        else if ((t[0] is '+' or '-') && long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long delta)) SeekBy(delta);
        else if (TryNumber(t, out long to)) SeekTo(to);
        else return "seek <n | +n | -n | start | end>";

        return Status + ". " + PositionText;
    }

    /// <summary>Reads a number the way a person writes it: 1234, 0x4D2.</summary>
    public static bool TryNumber(string text, out long value)
    {
        text = text.Trim().Replace("_", string.Empty).Replace(",", string.Empty);
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return long.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value) && value >= 0;

        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private async Task<string?> WriteCommand(string text)
    {
        byte[] bytes;
        try
        {
            bytes = StreamPayload.FromTyped(text);
        }
        catch (FormatException ex)
        {
            return ex.Message;
        }

        await WriteAsync(bytes, "typed");
        return Status;
    }

    private async Task<string?> PatternCommand(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || !int.TryParse(args[^1], out int n) || n < 0 || n > 64 << 20) return "pattern <kind> <count>, with the kind one of: " + string.Join(", ", StreamPayload.Patterns);

        string kind = string.Join(' ', args.Take(args.Count - 1));
        string? found = StreamPayload.Patterns.FirstOrDefault(p => p.StartsWith(kind, StringComparison.OrdinalIgnoreCase));
        if (found is null) return "The kinds are: " + string.Join(", ", StreamPayload.Patterns);

        await WriteAsync(StreamPayload.Pattern(found, n), $"{n:N0} byte(s) of {found}");
        return Status;
    }

    private string AdaptCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            string chain = _setup.Adapters.Count == 0
                ? "No adapters. Add one: adapt add <key> [arg]"
                : string.Join('\n', _setup.Adapters.Select((a, i) => $"  {i + 1}. {a}"));
            return chain + "\nThe adapters: " + string.Join(", ", StreamAdapters.All.Select(a => a.Key));
        }

        string verb = args[0].ToLowerInvariant();
        switch (verb)
        {
            case "add" when args.Count >= 2:
            {
                var def = StreamAdapters.Find(args[1]);
                if (def is null) return $"There is no adapter called '{args[1]}'. " + string.Join(", ", StreamAdapters.All.Select(a => a.Key));

                var spec = new AdapterSpec { Key = def.Key, Arg = args.Count > 2 ? string.Join(' ', args.Skip(2)) : def.ArgDefault };
                if (ProblemOf(spec) is { } problem) return problem;

                AddAdapter(spec);
                return $"Added {spec}. {_adapterProblem}".Trim();
            }
            case "clear":
                foreach (var spec in _setup.Adapters.ToList()) _setup.Adapters.Remove(spec);
                AdaptersEdited("adapters cleared");
                return "No adapters.";
            case "remove" or "off" or "on" or "side" when args.Count >= 2 && int.TryParse(args[1], out int n) && n >= 1 && n <= _setup.Adapters.Count:
            {
                var spec = _setup.Adapters[n - 1];
                if (verb == "remove") RemoveAdapter(spec);
                else if (verb == "off") ChangeAdapter(spec, a => a.Enabled = false);
                else if (verb == "on") ChangeAdapter(spec, a => a.Enabled = true);
                else if (args.Count >= 3 && Enum.TryParse<AdapterSide>(args[2], true, out var side)) ChangeAdapter(spec, a => a.Side = side);
                else return "adapt side <n> read|write|both";

                return $"Done. {_adapterProblem}".Trim();
            }
            default:
                return "adapt [add <key> [arg] | remove <n> | side <n> read|write|both | off <n> | on <n> | clear]";
        }
    }

    private string StripCommand(IReadOnlyList<string> args, bool hex)
    {
        var side = ViewSide;
        if (args.Count > 0)
        {
            side = args[0].ToLowerInvariant() switch { "in" or "input" => StripSide.Input, "out" or "output" => StripSide.Output, "written" or "wire" => StripSide.Written, _ => side };
        }

        var strip = StripOf(side);
        if (strip.Length == 0) return $"The {side} strip is empty.";

        var data = strip.Span.Length > 4096 ? strip.Span[^4096..].ToArray() : strip.Span.ToArray();
        long first = strip.Length - data.Length;
        return (first > 0 ? $"(the last {data.Length:N0} of {strip.Length:N0} bytes)\n" : string.Empty) + (hex ? HexDump.Format(data, first) : Encoding.UTF8.GetString(data));
    }

    private string MarkCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            ClearMarks();
            return "Marks cleared.";
        }

        if (args.Count != 2 || !TryNumber(args[0], out long a) || !TryNumber(args[1], out long b)) return "mark <in> <out>, offsets in the strip (123 or 0x7B)";

        SetMarks(Math.Min(a, b), Math.Max(a, b));
        return $"In {MarkIn:N0}, Out {MarkOut:N0} ({MarkOut - MarkIn:N0} byte(s) of the {ViewSide} strip)";
    }

    private string SaveCommand(string name)
    {
        if (name.Length > 0) _setup.Name = name;
        if (string.IsNullOrWhiteSpace(_setup.Name)) return "Give it a name: save <name>";

        string result = SaveNow();
        Touch();
        return result;
    }

    public override UIElement BuildMainView()
    {
        return new StreamView(this);
    }

    /// <summary>The reels kept in the Silo, and what the Lab comes with, for the Reels inspector.</summary>
    internal static LabSaved<StreamSetup> Kept => Saved;

    // ---- library, recent and problems (the panes every Lab window has) -------------------------------

    protected override bool RecentOnOpen => false;

    public override bool LibraryOpensWithWindow => true;

    public override Task OpenRecentAsync(string text)
    {
        if (Saved.Load(text) is not { } setup) throw new InvalidOperationException($"There is no reel called {text} in the Silo.");

        return OpenLibrarySetup(setup, text);
    }

    private async Task OpenLibrarySetup(StreamSetup setup, string? keptAs)
    {
        if (_dirty && !LabUi.Confirm("This reel has changes that are not kept in the Silo. Open another anyway?", "Stream Lab")) return;

        await Replace(setup, false);
        if (keptAs is not null) RememberRecent(keptAs);

        // a connection is opened when the person says so
        if (setup.Source.Kind is StreamKind.Memory) await OpenReelAsync();
    }

    private async void OpenLibraryEntry(StreamSetup setup, string? keptAs)
    {
        try
        {
            await OpenLibrarySetup(setup, keptAs);
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    public override IReadOnlyList<LabLibrarySection> LibrarySections(string filter)
    {
        bool Wanted(string text) => filter.Length == 0 || text.Contains(filter, StringComparison.OrdinalIgnoreCase);

        var kept = Saved.Names().Where(Wanted)
                        .Select(name => new LabLibraryEntry(name, "Reel", LabUi.Shorten(Saved.Load(name)?.Source.ToString() ?? string.Empty, 40), "Reel",
                                                            () => { if (Saved.Load(name) is { } setup) OpenLibraryEntry(setup, name); }, name == _setup.Name, ToolTip: "Double-click to open", Delete: () => Saved.Delete(name)))
                        .ToList();
        var templates = StreamLibrary.Entries.Where(e => Wanted(e.Name) || Wanted(e.Description))
                                     .Select(e => new LabLibraryEntry(e.Name, "Reel", LabUi.Shorten(e.Description, 50), "Reel", () => OpenLibraryEntry(e.Make(), null), IsTemplate: true, ToolTip: e.Description))
                                     .ToList();
        return [new("In the Silo", "Database5", kept, Note: Saved.IsAvailable ? null : "No Silo is open"), new("Templates", null, templates, true)];
    }

    protected override IEnumerable<LabProblem> Validate()
    {
        if (!string.IsNullOrEmpty(OpenError)) yield return new LabProblem(LabProblemSeverity.Error, "Reel", OpenError);
        if (!string.IsNullOrEmpty(_adapterProblem)) yield return new LabProblem(LabProblemSeverity.Warning, "Adapters", _adapterProblem);
    }

    internal int ViewRows => Lab_.ViewRows;
}

using System.IO;
using Telefrag.DI;

namespace Silo.Labs.Streams;

public static class StreamLabParameters
{
    public const string BufferKb     = "bufferKb";
    public const string TimeLimitMs  = "timeLimitMs";
    public const string PlayMs       = "playMs";
    public const string ViewRows     = "viewRows";
    public const string Step         = "step";
    public const string ClearOnSeek  = "clearOnSeek";
}

/// <summary>
///     Works with the streams of the Silo's world as though cutting film on a moviola: a stream is a reel that is opened, stepped through a few bytes at a
///     time, wound back and forth, written to, and cut; adapters can be put on it to change what comes out of it or goes into it.
/// </summary>
[Singleton(typeof(ILab))]
public class StreamLab : LabBase<StreamSetup>
{
    public override string Description => "Open a file, a connection (TCP or TLS) or a stream in memory as a reel: see its properties, read and write as many bytes as you choose, move the position, " +
                                          "put adapters on it, step through it a byte at a time, and cut pieces out of it.";

    public override string IconKey => "TransferData";

    public override string Id => "stream";

    public override string Name => "Stream Lab";

    public override int Order => 60;

    protected override IEnumerable<LabParameter> DefineParameters()
    {
        yield return new LabParameter(StreamLabParameters.TimeLimitMs, "Time limit of a step (ms)", LabParameterKind.Text, "10000",
                                      "How long opening a connection, or a read or write, may take before it is given up on (0: no limit; the Stop button always works).", "Running", hint: "10000");
        yield return new LabParameter(StreamLabParameters.PlayMs, "Play: ms between steps", LabParameterKind.Text, "250", "How long Play waits between two steps.", "Running", hint: "250");
        yield return new LabParameter(StreamLabParameters.Step, "Bytes in a step of a new reel", LabParameterKind.Text, "16", "How many bytes a read takes when a reel is new.", "Running", hint: "16");
        yield return new LabParameter(StreamLabParameters.BufferKb, "Strip size limit (KB)", LabParameterKind.Text, "8192",
                                      "How much of what is read or written is kept in the strip for looking at and cutting from; reading goes on past it.", "Strip", hint: "8192");
        yield return new LabParameter(StreamLabParameters.ViewRows, "Rows in the hex view", LabParameterKind.Text, "400", "How many lines of 16 bytes the hex view shows at a time.", "Strip", hint: "400");
        yield return new LabParameter(StreamLabParameters.ClearOnSeek, "Forget the strip when the position moves", LabParameterKind.Bool, true,
                                      "Moving the position starts a new stretch of film: the strip is emptied (marks and cuts are kept).", "Strip");
    }

    public override LabSession<StreamSetup> CreateSession()
    {
        return new StreamLabSession(this);
    }

    protected override bool TryConvert(object? candidate, out StreamSetup? value)
    {
        switch (candidate)
        {
            case StreamSetup setup:
                value = setup;
                return true;
            case StreamSource source:
                value = new StreamSetup { Source = source };
                return true;
            case Stream stream:
                value = new StreamSetup { Source = StreamSource.ForStream(stream) };
                return true;
            case FileInfo file:
                value = new StreamSetup { Source = StreamSource.ForFile(file.FullName) };
                return true;
            case byte[] bytes:
                value = new StreamSetup { Source = StreamSource.ForMemory(bytes) };
                return true;
            // asked of every node when a menu is built: only what plainly names a stream is taken (no disk is asked about)
            case Uri uri when uri.Scheme is "tcp" or "tls" or "ssl" && StreamSource.TryParse(uri.OriginalString, out var fromUri):
                value = new StreamSetup { Source = fromUri! };
                return true;
            case string text when StreamSource.LooksLikeSource(text) && StreamSource.TryParse(text, out var fromText):
                value = new StreamSetup { Source = fromText! };
                return true;
            default:
                value = null;
                return false;
        }
    }

    public override bool TryParse(string text, out StreamSetup? value)
    {
        if (StreamSource.TryParse(text, out var source))
        {
            value = new StreamSetup { Source = source!, Step = DefaultStep };
            return true;
        }

        value = null;
        return false;
    }

    internal int DefaultStep => Number(StreamLabParameters.Step, 16, 1, 1 << 20);

    internal long BufferLimit => Number(StreamLabParameters.BufferKb, 8192, 1, 1 << 20) * 1024L;

    internal int ViewRows => Number(StreamLabParameters.ViewRows, 400, 20, 5000);

    internal TimeSpan PlayInterval => TimeSpan.FromMilliseconds(Number(StreamLabParameters.PlayMs, 250, 0, 60_000));

    /// <summary>How long a step may take; null for no limit.</summary>
    internal TimeSpan? TimeLimit => Number(StreamLabParameters.TimeLimitMs, 10_000, 0, 3_600_000) is var ms and > 0 ? TimeSpan.FromMilliseconds(ms) : null;

    internal bool ClearOnSeek => Parameters[StreamLabParameters.ClearOnSeek].Bool;

    private int Number(string name, int fallback, int min, int max)
    {
        return int.TryParse(Parameters[name].Text, out int n) ? Math.Clamp(n, min, max) : fallback;
    }
}

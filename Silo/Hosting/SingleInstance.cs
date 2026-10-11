using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using Serilog;

namespace Silo.Hosting;

/// <summary>
///     "Is a stock instance running?" and "tell it to open this" -- the single-instance machinery from the startup flow.
///     <para>
///         The <b>stock instance</b> is the one multi-document Silo process per Windows user. It owns a named mutex
///         (<see cref="TryBecomeStock" />) and listens on a named pipe (<see cref="StartServer" />). Solo and Remote
///         processes never take the mutex, so any number of them can run next to it.
///     </para>
///     <para>
///         Wire protocol, one UTF-8 line per connection: <c>ACTIVATE</c>, or <c>OPEN&lt;TAB&gt;full path</c>. The pipe is
///         current-user-only. The server treats the line as data: only existing local files are ever opened.
///     </para>
/// </summary>
public static class SingleInstance
{
    private const string OpenVerb     = "OPEN";
    private const string ActivateVerb = "ACTIVATE";

    private static Mutex?                   _mutex;
    private static CancellationTokenSource? _cts;

    private static string Suffix
    {
        get
        {
            try
            {
                return WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            }
            catch
            {
                return Environment.UserName;
            }
        }
    }

    private static string MutexName => $@"Local\Silo.Stock.{Suffix}";
    private static string PipeName  => $"Silo.Stock.{Suffix}";

    /// <summary>Takes the stock role if nobody has it. Returns false when a stock instance is already running.</summary>
    public static bool TryBecomeStock()
    {
        try
        {
            var mutex = new Mutex(true, MutexName, out var created);
            if (!created)
            {
                mutex.Dispose();
                return false;
            }

            _mutex = mutex; // held for the life of the process (and abandoned by the OS if we die)
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Single-instance mutex failed; running as an independent instance");
            return true;
        }
    }

    /// <summary>Sends a request to the stock instance. Returns false if it could not be reached.</summary>
    public static bool Send(string? openPath, int timeoutMs = 5000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);

            // The stock instance is about to be asked to come to the front; let it.
            NativeMethods.AllowSetForegroundWindow(NativeMethods.AsfwAny);

            var line = openPath is null ? ActivateVerb : $"{OpenVerb}\t{openPath}";
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(line);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not reach the stock Silo instance");
            return false;
        }
    }

    /// <summary>
    ///     Starts listening for requests from later launches. <paramref name="onRequest" /> gets the full path to open (null
    ///     for "just come to the front") on a thread-pool thread.
    /// </summary>
    public static void StartServer(Action<string?> onRequest)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _ = Task.Run(async () =>
                     {
                         while (!ct.IsCancellationRequested)
                             try
                             {
                                 await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In,
                                                                                    NamedPipeServerStream.MaxAllowedServerInstances,
                                                                                    PipeTransmissionMode.Byte,
                                                                                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                                 await server.WaitForConnectionAsync(ct);

                                 using var reader = new StreamReader(server, Encoding.UTF8);
                                 var       line   = await reader.ReadLineAsync(ct);
                                 Handle(line, onRequest);
                             }
                             catch (OperationCanceledException)
                             {
                                 return;
                             }
                             catch (Exception ex)
                             {
                                 Log.Warning(ex, "Single-instance pipe error");
                                 await Task.Delay(500, CancellationToken.None);
                             }
                     }, ct);
    }

    public static void StopServer()
    {
        _cts?.Cancel();
    }

    private static void Handle(string? line, Action<string?> onRequest)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        if (line.Equals(ActivateVerb, StringComparison.Ordinal))
        {
            onRequest(null);
            return;
        }

        if (line.StartsWith(OpenVerb + "\t", StringComparison.Ordinal))
        {
            var path = line[(OpenVerb.Length + 1)..];
            if (System.IO.File.Exists(path))
                onRequest(path);
            else
                Log.Warning("Ignoring request to open '{Path}': not an existing file", path);
        }
    }
}

using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using Serilog;
using Silo.Actions;

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
    ///     Runs an action in the stock instance (see <see cref="ActionProtocol" />) and returns its one-line JSON reply, or null
    ///     if the stock instance could not be reached. Waits as long as the action takes.
    /// </summary>
    public static string? SendAction(string requestLine, int connectTimeoutMs = 5000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect(connectTimeoutMs);

            using var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
            writer.WriteLine(requestLine);
            using var reader = new StreamReader(client, Encoding.UTF8, false, 1024, true);
            return reader.ReadLine();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not reach the stock Silo instance");
            return null;
        }
    }

    /// <summary>
    ///     Starts listening for requests from later launches. <paramref name="onRequest" /> gets the full path to open (null
    ///     for "just come to the front") on a thread-pool thread. <paramref name="onAction" /> answers action requests (it is
    ///     given the request line and returns the reply line); each runs on its own, so a long action does not hold up the rest.
    /// </summary>
    public static void StartServer(Action<string?> onRequest, Func<string, Task<string>>? onAction = null)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _ = Task.Run(async () =>
                     {
                         while (!ct.IsCancellationRequested)
                             try
                             {
                                 var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut,
                                                                        NamedPipeServerStream.MaxAllowedServerInstances,
                                                                        PipeTransmissionMode.Byte,
                                                                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                                 try
                                 {
                                     await server.WaitForConnectionAsync(ct);

                                     var reader = new StreamReader(server, Encoding.UTF8, false, 1024, true);
                                     var line   = await reader.ReadLineAsync(ct);
                                     if (ActionProtocol.IsActionLine(line) && onAction is not null)
                                     {
                                         _ = AnswerAsync(server, line!, onAction); // takes over (and disposes) the connection
                                         continue;
                                     }

                                     Handle(line, onRequest);
                                 }
                                 catch
                                 {
                                     await server.DisposeAsync();
                                     throw;
                                 }

                                 await server.DisposeAsync();
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

    private static async Task AnswerAsync(NamedPipeServerStream server, string line, Func<string, Task<string>> onAction)
    {
        await using (server)
            try
            {
                var reply = await onAction(line);
                await using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                await writer.WriteLineAsync(reply);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not answer an action request");
            }
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

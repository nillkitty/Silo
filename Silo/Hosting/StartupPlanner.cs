using System.IO;
using Serilog;
using Silo.DbModel.Sqlite;
using Silo.Remote;

namespace Silo.Hosting;

/// <summary>Which kind of process this is (see the startup flowchart in Hosting/STARTUP-FLOW.md).</summary>
public enum LaunchMode
{
    /// <summary>The multi-document instance: one per user, receives "open this" requests from later launches.</summary>
    Stock,

    /// <summary>A single-document process for a Solo Silo (it carries .NET assemblies) or one opened with <c>/P</c>.</summary>
    Solo,

    /// <summary>A single-document process for a Silo that comes from a URL.</summary>
    Remote
}

/// <summary>What this process should do after startup.</summary>
/// <param name="Mode">The kind of process this is.</param>
/// <param name="Target">The path or URL to open once the window is up (null: start empty).</param>
/// <param name="ServeRequests">Listen for open/activate requests from later launches (stock instance only).</param>
public sealed record LaunchPlan(LaunchMode Mode, string? Target, bool ServeRequests);

/// <summary>
///     The startup decision, before any WPF is created. Implements the flowchart:
///     <code>
///     Start -> args?
///       none -> stock instance running? yes: activate it, exit | no: become the stock instance
///       URL  -> open as Remote (own process)
///       path -> Solo Silo, or /P?   -> open as Solo (own process)
///               normal              -> stock instance running? yes: ask it to open the path, exit
///                                                              no:  open here, become the stock instance
///     </code>
/// </summary>
public static class StartupPlanner
{
    /// <summary>Returns the plan, or null when another instance took over the request and this process should exit.</summary>
    public static LaunchPlan? Plan(string[] args)
    {
        var a = LaunchArgs.Parse(args);

        if (a.Target is null)
        {
            if (SingleInstance.TryBecomeStock()) return new LaunchPlan(LaunchMode.Stock, null, true);
            if (SingleInstance.Send(null)) return null;

            Log.Warning("A stock instance holds the lock but did not answer; starting a separate window");
            return new LaunchPlan(LaunchMode.Stock, null, false);
        }

        if (a.IsUrl) return new LaunchPlan(LaunchMode.Remote, a.Target, false);

        var full  = Path.GetFullPath(a.Target);
        var probe = SiloProbe.Probe(full);
        if (probe.IsSolo || a.Solo) return new LaunchPlan(LaunchMode.Solo, full, false);

        if (SingleInstance.TryBecomeStock()) return new LaunchPlan(LaunchMode.Stock, full, true);
        if (SingleInstance.Send(full)) return null;

        Log.Warning("A stock instance holds the lock but did not answer; opening '{Path}' in this process", full);
        return new LaunchPlan(LaunchMode.Stock, full, false);
    }
}

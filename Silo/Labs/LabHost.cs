using Serilog;
using Silo.Commanding;
using Silo.Providers.Action;
using Silo.ViewModel;

namespace Silo.Labs;

/// <summary>
///     Finds the Labs and opens values in them: the one place the Analysis menu, the "Open in Lab" command and the rest of Silo go
///     through. The Labs themselves are found in the container, so adding one needs no change here.
/// </summary>
public static class LabHost
{
    private static readonly List<LabWindow> OpenWindows = [];

    /// <summary>Every Lab the container knows, in menu order.</summary>
    public static IReadOnlyList<ILab> Labs
    {
        get
        {
            try
            {
                return (App.Resolve<IEnumerable<ILab>>() ?? []).OrderBy(l => l.Order).ThenBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Looking for the Labs failed");
                return [];
            }
        }
    }

    /// <summary>The Lab windows that are open.</summary>
    public static IReadOnlyList<LabWindow> Windows => OpenWindows.ToList();

    /// <summary>
    ///     Opens a new window of <paramref name="lab" /> studying <paramref name="value" /> (the Lab's default when null). Each value
    ///     gets a window of its own. The window is shown at once; the value loads in it.
    /// </summary>
    public static LabWindow Open(ILab lab, object? value = null)
    {
        var session = lab.CreateSession();
        var window  = new LabWindow(session);

        OpenWindows.Add(window);
        window.Closed += (_, _) => OpenWindows.Remove(window);
        window.Show();
        window.Activate();

        _ = StudyAsync(session, value);
        return window;
    }

    /// <summary>The standard "Open in Lab" command: opens the value in the one Lab that takes it, or the first of those that do. False when no Lab takes it.</summary>
    public static bool OpenInLab(object? candidate)
    {
        foreach (var lab in Labs)
        {
            if (!TryAccept(lab, candidate, out var value)) continue;

            Open(lab, value);
            return true;
        }

        return false;
    }

    /// <summary>The Labs that can study <paramref name="candidate" /> (and the value each would be given).</summary>
    public static IEnumerable<(ILab Lab, object Value)> LabsAccepting(object? candidate)
    {
        foreach (var lab in Labs)
            if (TryAccept(lab, candidate, out var value))
                yield return (lab, value!);
    }

    /// <summary>
    ///     Whether <paramref name="lab" /> takes <paramref name="candidate" />: the object itself, or -- for a node's model that describes
    ///     itself (<see cref="INodeDetails" />) -- its address.
    /// </summary>
    public static bool TryAccept(ILab lab, object? candidate, out object? value)
    {
        value = null;
        if (candidate is null) return false;

        try
        {
            if (lab.TryAccept(candidate, out value)) return true;
            if (candidate is INodeDetails { Uri: { } uri } && lab.TryAccept(uri, out value)) return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "{Lab} failed to say whether it takes {Candidate}", lab.Name, candidate.GetType().Name);
        }

        value = null;
        return false;
    }

    private static async Task StudyAsync(ILabSession session, object? value)
    {
        try
        {
            await session.OpenAsync(value);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Opening a value in {Lab} failed", session.Lab.Name);
            if (session is ILabReporter reporter) reporter.Report("Could not open it: " + ex.Message);
        }
    }
}

/// <summary>What a session implements to be told about a problem outside it (a value that would not open): it shows it on its status line.</summary>
internal interface ILabReporter
{
    void Report(string message);
}

/// <summary>
///     The standard "Open in Lab" command: on any node whose model (or the address it describes) a Lab can study, an
///     "Open in &lt;Lab&gt;" entry in the Node menu and the Explorer's right-click menu.
/// </summary>
public sealed class OpenInLabActions : IActionProvider<object>
{
    public IEnumerable<ProvidedCommand> GetCommands(object node)
    {
        foreach (var (lab, value) in LabHost.LabsAccepting(node))
        {
            var l = lab;
            var v = value;
            yield return ProvidedCommand.From("lab.open." + l.Id, $"Open in {l.Name}", () => LabHost.Open(l, v), l.IconKey, null, l.Description, "Analysis");
        }
    }
}

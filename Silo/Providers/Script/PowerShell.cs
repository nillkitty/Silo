using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Silo.Providers.Reflection;
using Telefrag.DI;

namespace Silo.Providers.Script;

/// <summary>
///     In-process PowerShell (Microsoft.PowerShell.SDK). One runspace per context, so variables and functions
///     persist between <c>RunAsync</c> calls. Progress/Warning/Error streams map to the matching events;
///     strings and Information (Write-Host) go to TextOutput, other pipeline objects to ObjectOutput.
/// </summary>
[Transient(typeof(IScriptProvider))]
public partial class PowerShell : IScriptProvider, IInProcScriptHost, ILaunchable
{
    public string? DisplayName   => "PowerShell";
    public string  FileExtension => "ps1";
    public string  ContentType   => "text/x-powershell";

    public string? ArgTemplate       => "-File \"%1\"";
    public string? FilePath          => @"%windir%\System32\WindowsPowerShell\v1.0\powershell.exe";
    public bool    UseShellExecute   { get; set; }
    public string? InlineArgTemplate => "-NoProfile -Command \"%1\"";
    public string? EditorLanguage => "PowerShell";

    public IScriptContext CreateContext()
    {
        return new PowerShellContext();
    }

    private sealed class PowerShellContext : ScriptContextBase
    {
        private readonly Runspace _runspace;

        public PowerShellContext()
        {
            var state = InitialSessionState.CreateDefault2();
            state.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            _runspace             = RunspaceFactory.CreateRunspace(state);
            _runspace.Open();
        }

        public override bool AddVariable(string name, object value)
        {
            name = name.TrimStart('$');
            if (_runspace.SessionStateProxy.PSVariable.GetValue(name) != null)
            {
                return false;
            }

            _runspace.SessionStateProxy.PSVariable.Set(name, value);
            return true;
        }

        protected override async Task<ScriptResult> ExecuteAsync(string script, CancellationToken cancel)
        {
            using var ps = System.Management.Automation.PowerShell.Create(_runspace);
            ps.AddScript(script);

            ps.Streams.Progress.DataAdded += (s, e) => RaiseProgress(((PSDataCollection<ProgressRecord>)s!)[e.Index]);
            ps.Streams.Warning.DataAdded  += (s, e) => RaiseWarning(((PSDataCollection<WarningRecord>)s!)[e.Index]);
            ps.Streams.Error.DataAdded    += (s, e) => RaiseError(((PSDataCollection<ErrorRecord>)s!)[e.Index]);
            ps.Streams.Information.DataAdded += (s, e) =>
                RaiseText(((PSDataCollection<InformationRecord>)s!)[e.Index].MessageData?.ToString() ?? "");
            ps.Streams.Verbose.DataAdded += (s, e) => RaiseText("VERBOSE: " + ((PSDataCollection<VerboseRecord>)s!)[e.Index].Message);
            ps.Streams.Debug.DataAdded   += (s, e) => RaiseText("DEBUG: " + ((PSDataCollection<DebugRecord>)s!)[e.Index].Message);

            var output = new PSDataCollection<PSObject>();
            output.DataAdded += (s, e) =>
            {
                var o = ((PSDataCollection<PSObject>)s!)[e.Index]?.BaseObject;
                switch (o)
                {
                    case null:
                        break;
                    case string text:
                        RaiseText(text);
                        break;
                    default:
                        RaiseObject(o);
                        break;
                }
            };

            var input = new PSDataCollection<PSObject>();
            input.Complete();

            using var _ = cancel.Register(() => ps.Stop());
            try
            {
                await ps.InvokeAsync(input, output);
            }
            catch (Exception ex) when (ex is RuntimeException or PipelineStoppedException)
            {
                if (cancel.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancel);
                }

                // Parse errors and terminating errors; non-terminating ones have already been raised from the stream.
                RaiseError(ex is RuntimeException { ErrorRecord: { } rec } ? rec : ex);
                return ScriptResult.Failed(ex, ex.Message);
            }

            var errors = ps.Streams.Error.Select(e => e.ToString()).ToArray();
            return ps.HadErrors
                ? ScriptResult.Failed(null, errors)
                : ScriptResult.Ok(output.Count == 0 ? null : output.Select(o => o?.BaseObject).LastOrDefault());
        }

        public override void Dispose()
        {
            base.Dispose();
            _runspace.Dispose();
        }
    }
}

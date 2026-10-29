using FSharp.Compiler.Diagnostics;
using Fsi = FSharp.Compiler.Interactive.Shell;
using Microsoft.FSharp.Core;
using Telefrag.DI;

namespace Silo.Providers.Script;

/// <summary>
///     In-process F# Interactive. Scripts see <c>host</c> (a <see cref="ScriptHost" />):
///     <c>host.Progress "x"</c>, <c>host.Warning "x"</c>, <c>host.Error "x"</c>, <c>host.WriteLine "x"</c>, <c>host.Output x</c>.
/// </summary>
[Transient(typeof(IScriptProvider))]
public class FSharp : IScriptProvider, IInProcScriptHost
{
    public string? DisplayName       => "F#";
    public string  FileExtension     => "fsx";
    public string  ContentType       => "text/x-fsharp";
    public string? InlineArgTemplate => null;
    public string? EditorLanguage => "FSharp";

    public IScriptContext CreateContext()
    {
        return new FSharpContext();
    }

    private sealed class FSharpContext : ScriptContextBase
    {
        private readonly HashSet<string>                  _names = [];
        private readonly Fsi.FsiEvaluationSession       _session;

        public FSharpContext()
        {
            var config = Fsi.FsiEvaluationSession.GetDefaultConfiguration();
            _session = Fsi.FsiEvaluationSession.Create(
                config,
                ["fsi.exe", "--noninteractive", "--nologo", "--gui-"],
                TextReader.Null,
                new LineWriter(RaiseText),
                new LineWriter(s => RaiseError(s)),
                FSharpOption<bool>.Some(true),
                null);
            _session.AddBoundValue("host", Host);
            _names.Add("host");
        }

        public override bool AddVariable(string name, object value)
        {
            if (!_names.Add(name))
            {
                return false;
            }

            _session.AddBoundValue(name, value);
            return true;
        }

        protected override Task<ScriptResult> ExecuteAsync(string script, CancellationToken cancel)
        {
            var (outcome, diagnostics) = _session.EvalInteractionNonThrowing(script, FSharpOption<CancellationToken>.Some(cancel));

            var errors = new List<string>();
            foreach (var d in diagnostics)
            {
                var text = $"({d.StartLine},{d.StartColumn}) {d.ErrorNumberText}: {d.Message}";
                switch (d.Severity)
                {
                    case var s when s.IsError:
                        errors.Add(text);
                        RaiseError(text);
                        break;
                    case var s when s.IsWarning:
                        RaiseWarning(text);
                        break;
                }
            }

            if (outcome is FSharpChoice<FSharpOption<Fsi.FsiValue>, Exception>.Choice2Of2 { Item: var ex })
            {
                if (errors.Count == 0)
                {
                    RaiseError(ex);
                    errors.Add(ex.Message);
                }

                return Task.FromResult(ScriptResult.Failed(ex, errors.ToArray()));
            }

            var value = outcome is FSharpChoice<FSharpOption<Fsi.FsiValue>, Exception>.Choice1Of2 { Item: { } v }
                ? v.Value.ReflectionValue
                : null;
            return Task.FromResult(errors.Count > 0 ? ScriptResult.Failed(null, errors.ToArray()) : ScriptResult.Ok(value));
        }

        public override void Dispose()
        {
            base.Dispose();
            (_session as IDisposable)?.Dispose();
        }
    }
}

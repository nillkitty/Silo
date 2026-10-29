using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Telefrag.DI;

namespace Silo.Providers.Script;

/// <summary>
///     In-process C# scripting (Roslyn). Scripts see <see cref="ScriptHost" /> members at top level:
///     <c>Progress(x)</c>, <c>Warning(x)</c>, <c>Error(x)</c>, <c>WriteLine(x)</c>, <c>Output(x)</c>.
/// </summary>
[Transient(typeof(IScriptProvider))]
public class CSharp : IScriptProvider, IInProcScriptHost
{
    public string? DisplayName       => "C# Script";
    public string  FileExtension     => "csx";
    public string  ContentType       => "text/x-csharp";
    public string? InlineArgTemplate => null;
    public string? EditorLanguage => "CSharp";

    public IScriptContext CreateContext()
    {
        return new CSharpContext();
    }

    private sealed class CSharpContext : ScriptContextBase
    {
        private static readonly ScriptOptions BaseOptions = ScriptOptions.Default
            .WithReferences(GetReferences())
            .WithImports("System", "System.IO", "System.Linq", "System.Collections.Generic", "System.Threading.Tasks");

        private readonly Dictionary<string, Type> _pending = new();
        private          ScriptState<object>?     _state;

        public override bool AddVariable(string name, object value)
        {
            if (Host.Vars.ContainsKey(name))
            {
                return false;
            }

            Host.Vars[name] = value;
            _pending[name]  = value.GetType();
            return true;
        }

        protected override async Task<ScriptResult> ExecuteAsync(string script, CancellationToken cancel)
        {
            if (_pending.Count > 0)
            {
                var declarations = string.Join("\n", _pending.Select(p =>
                    $"var {p.Key} = ({TypeName(p.Value)})Vars[\"{p.Key}\"];"));
                _pending.Clear();

                var declared = await StepAsync(declarations, cancel);
                if (declared.IsFailed)
                {
                    return declared;
                }
            }

            return await StepAsync(script, cancel);
        }

        private async Task<ScriptResult> StepAsync(string code, CancellationToken cancel)
        {
            var next = _state == null
                ? CSharpScript.Create(code, BaseOptions, typeof(ScriptHost))
                : _state.Script.ContinueWith(code);

            var errors = new List<string>();
            foreach (var d in next.Compile(cancel))
            {
                switch (d.Severity)
                {
                    case DiagnosticSeverity.Error:
                        errors.Add(d.ToString());
                        RaiseError(d.ToString());
                        break;
                    case DiagnosticSeverity.Warning:
                        RaiseWarning(d.ToString());
                        break;
                }
            }

            if (errors.Count > 0)
            {
                return ScriptResult.Failed(null, errors.ToArray());
            }

            var state = _state == null
                ? await next.RunAsync(Host, _ => true, cancel)
                : await next.RunFromAsync(_state, _ => true, cancel);
            _state = state;

            if (state.Exception != null)
            {
                RaiseError(state.Exception);
                return ScriptResult.Failed(state.Exception, state.Exception.Message);
            }

            if (state.ReturnValue != null)
            {
                RaiseObject(state.ReturnValue);
            }

            return ScriptResult.Ok(state.ReturnValue);
        }

        private static string TypeName(Type t)
        {
            if (!t.IsVisible || t.IsGenericParameter)
            {
                return "dynamic";
            }

            if (t.IsArray)
            {
                return TypeName(t.GetElementType()!) + "[" + new string(',', t.GetArrayRank() - 1) + "]";
            }

            if (!t.IsGenericType)
            {
                return "global::" + t.FullName!.Replace('+', '.');
            }

            var name = t.GetGenericTypeDefinition().FullName!;
            name = name[..name.IndexOf('`')].Replace('+', '.');
            return $"global::{name}<{string.Join(", ", t.GetGenericArguments().Select(TypeName))}>";
        }

        private static IEnumerable<MetadataReference> GetReferences()
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Append(typeof(Microsoft.CSharp.RuntimeBinder.Binder).Assembly)
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .DistinctBy(a => a.Location)
                .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location));
        }
    }
}

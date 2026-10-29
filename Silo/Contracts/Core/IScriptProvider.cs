using Silo.Providers.Reflection;

namespace Silo.Contracts.Core;

[RequireRegistration]
[OptionallyImplement(typeof(ILaunchable))]
[OptionallyImplement(typeof(IInProcScriptHost))]
public interface IScriptProvider : IFileFormat
{
    /// <summary>
    ///     Gets a string which is either 1) null, if script provider is in-proc or file-input
    ///     only;  2) a string describing the order and format used to provide an inline script
    ///     to the executable (replacing %1)
    /// </summary>
    string? InlineArgTemplate { get; }

    /// <summary>
    ///     The name of the <c>TextLanguage</c> the text editor uses to highlight this provider's scripts (for example
    ///     "PowerShell"); null when the provider has no highlighting of its own.
    /// </summary>
    string? EditorLanguage => null;
}

[AttributeUsage(AttributeTargets.Interface, AllowMultiple = true)]
public class OptionallyImplementAttribute(Type interfaceType) : Attribute
{
    public Type InterfaceType { get; set; } = interfaceType;
}

public interface IInProcScriptHost
{
    /// <summary>
    ///     Creates a new script execution context (blank slate for variables & functions)
    /// </summary>
    IScriptContext CreateContext();
}

public interface IScriptContext
{
    /// <summary>
    ///     Adds a variable to the context
    /// </summary>
    /// <param name="name">The variable name, inclusive of any required prefixes</param>
    /// <param name="value">The variable value</param>
    /// <returns>
    ///     Returns true if added or false if the variable exists or the call is
    ///     redundant
    /// </returns>
    bool AddVariable(string name, object value);

    /// <summary>
    ///     Executes a snippet of code in the context
    /// </summary>
    /// <param name="script">Code to execute</param>
    /// <returns>A <see cref="ScriptResult" /> object describing the cumulative result</returns>
    Task<IScriptResult> RunAsync(string script);

    /// <summary>
    ///     Event which fires when a progress event was received from the script runtime
    /// </summary>
    event EventHandler<object> ProgressEvent;

    /// <summary>
    ///     Event which fires when a warning event was received from the script runtime
    /// </summary>
    event EventHandler<object> WarningEvent;

    /// <summary>
    ///     Event which fires when a error event was received from the script runtime
    /// </summary>
    event EventHandler<object> ErrorEvent;

    /// <summary>
    ///     Event which fires when a line of standard output text is received
    /// </summary>
    event EventHandler<string> TextOutput;

    /// <summary>
    ///     Event which fires when an output object was received from the script runtime
    /// </summary>
    event EventHandler<object> ObjectOutput;

    /// <summary>
    ///     Event which fires when a script begins processing
    /// </summary>
    event EventHandler<string> ScriptStarting;

    /// <summary>
    ///     Event which fires when a script completes
    /// </summary>
    event EventHandler<IScriptResult> ScriptCompleted;
}

public interface IScriptResult
{
    bool IsFailed     { get; }
    bool IsSuccessful { get; }
    bool IsTimeout    { get; }
}
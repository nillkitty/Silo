using Serilog;
using Serilog.Events;

namespace Silo.Contracts.Core;

/// <summary>
///     Same as LogsSelf but the <see cref="Logger" /> is public.
/// </summary>
public class ContextBase : LogsSelf
{
    public new ILogger Logger => base.Logger;
}

/// <summary>
///     Base class which adds a Logger to log for the same type.
/// </summary>
public class LogsSelf() : Logs(null);

/// <summary>
///     Base class which adds a Logger to log for <typeparamref name="TLogContext" />
/// </summary>
/// <typeparam name="TLogContext"></typeparam>
public class Logs<TLogContext>() : Logs(typeof(TLogContext));

/// <summary>
///     Base class which adds a Logger and log functions.
/// </summary>
public class Logs
{
    protected ILogger Logger { get; }
    // ILogger ILoggable.Logger => Logger;

    protected void Write(LogEventLevel level, string messageTemplate, params object[] args)
    {
        Logger.Write(level, messageTemplate, args);
    }

    protected void Verbose(string messageTemplate, params object[] args)
    {
        Logger.Write(LogEventLevel.Verbose, messageTemplate, args);
    }

    protected void Debug(string messageTemplate, params object[] args)
    {
        Logger.Write(LogEventLevel.Debug, messageTemplate, args);
    }

    protected void Information(string messageTemplate, params object[] args)
    {
        Logger.Write(LogEventLevel.Information, messageTemplate, args);
    }

    protected void Warning(string messageTemplate, params object[] args)
    {
        Logger.Write(LogEventLevel.Warning, messageTemplate, args);
    }

    protected void Error(string messageTemplate, params object[] args)
    {
        Logger.Write(LogEventLevel.Error, messageTemplate, args);
    }

    protected void Fatal(string messageTemplate, params object[] args)
    {
        Logger.Write(LogEventLevel.Fatal, messageTemplate, args);
    }

    protected void Write(Exception ex, LogEventLevel level, string messageTemplate, params object[] args)
    {
        Logger.Write(level, ex, messageTemplate, args);
    }

    protected void Verbose(Exception ex, string messageTemplate, params object[] args)
    {
        Logger.Write(LogEventLevel.Verbose, ex, messageTemplate, args);
    }

    protected void Debug(Exception ex, string messageTemplate, params object[] args)
    {
        Logger.Write(LogEventLevel.Debug, ex, messageTemplate, args);
    }

    protected void Information(Exception ex, string messageTemplate, params object[] args)
    {
        Logger.Write(LogEventLevel.Information, ex, messageTemplate, args);
    }

    protected void Warning(Exception ex, string messageTemplate, params object[] args)
    {
        Logger.Write(LogEventLevel.Warning, ex, messageTemplate, args);
    }

    protected void Error(Exception ex, string messageTemplate, params object[] args)
    {
        Logger.Write(LogEventLevel.Error, ex, messageTemplate, args);
    }

    protected void Fatal(Exception ex, string messageTemplate, params object[] args)
    {
        Logger.Write(LogEventLevel.Fatal, ex, messageTemplate, args);
    }

    /// <summary>
    ///     Base class which adds a Logger and log functions.
    /// </summary>
    public Logs(Type? logContextType)
    {
        Logger = Log.ForContext(logContextType ?? GetType());
    }
}
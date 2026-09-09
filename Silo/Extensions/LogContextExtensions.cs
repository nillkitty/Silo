using System.Diagnostics.CodeAnalysis;
using Serilog.Events;
using Silo.Extensions;
using Silo.Model;

namespace Silo.Extensions;

public static class LogContextExtensions
{
    // =========================================================================
    // Verbose
    // =========================================================================

    public static void Verbose(this               ILogContext context,
                               [ConstantExpected] string messageTemplate) =>
        context.Logger.Verbose(messageTemplate);

    public static void Verbose<T>(this               ILogContext context,
                                  [ConstantExpected] string messageTemplate,
                                  T propertyValue) =>
        context.Logger.Verbose(messageTemplate, propertyValue);

    public static void Verbose<T0, T1>(this ILogContext context,
                                       [ConstantExpected]
                                       string messageTemplate,
                                       T0 propertyValue0, T1 propertyValue1) =>
        context.Logger.Verbose(messageTemplate, propertyValue0, propertyValue1);

    public static void Verbose<T0, T1, T2>(this ILogContext context,
                                           [ConstantExpected]
                                           string messageTemplate,
                                           T0 propertyValue0, T1 propertyValue1,
                                           T2 propertyValue2) =>
        context.Logger.Verbose(messageTemplate, propertyValue0, propertyValue1,
                               propertyValue2);

    public static void Verbose(this               ILogContext context,
                               [ConstantExpected] string      messageTemplate,
                               params             object?[]?  propertyValues) =>
        context.Logger.Verbose(messageTemplate, propertyValues);

    public static void Verbose(this ILogContext context, Exception? exception,
                               [ConstantExpected] string messageTemplate) =>
        context.Logger.Verbose(exception, messageTemplate);

    public static void Verbose<T>(this ILogContext          context,
                                  Exception?                exception,
                                  [ConstantExpected] string messageTemplate,
                                  T                         propertyValue) =>
        context.Logger.Verbose(exception, messageTemplate, propertyValue);

    public static void Verbose<T0, T1>(this ILogContext context,
                                       Exception?       exception,
                                       [ConstantExpected]
                                       string messageTemplate,
                                       T0 propertyValue0, T1 propertyValue1) =>
        context.Logger.Verbose(exception, messageTemplate, propertyValue0,
                               propertyValue1);

    public static void Verbose<T0, T1, T2>(this ILogContext context,
                                           Exception?       exception,
                                           [ConstantExpected]
                                           string messageTemplate,
                                           T0 propertyValue0, T1 propertyValue1,
                                           T2 propertyValue2) =>
        context.Logger.Verbose(exception, messageTemplate, propertyValue0,
                               propertyValue1, propertyValue2);

    public static void Verbose(this ILogContext context, Exception? exception,
                               [ConstantExpected] string messageTemplate,
                               params object?[]? propertyValues) =>
        context.Logger.Verbose(exception, messageTemplate, propertyValues);

    // =========================================================================
    // Debug
    // =========================================================================

    public static void Debug(this               ILogContext context,
                             [ConstantExpected] string      messageTemplate) =>
        context.Logger.Debug(messageTemplate);

    public static void Debug<T>(this               ILogContext context,
                                [ConstantExpected] string      messageTemplate,
                                T                              propertyValue) =>
        context.Logger.Debug(messageTemplate, propertyValue);

    public static void Debug<T0, T1>(this               ILogContext context,
                                     [ConstantExpected] string messageTemplate,
                                     T0 propertyValue0, T1 propertyValue1) =>
        context.Logger.Debug(messageTemplate, propertyValue0, propertyValue1);

    public static void Debug<T0, T1, T2>(this ILogContext context,
                                         [ConstantExpected]
                                         string messageTemplate,
                                         T0 propertyValue0, T1 propertyValue1,
                                         T2 propertyValue2) =>
        context.Logger.Debug(messageTemplate, propertyValue0, propertyValue1,
                             propertyValue2);

    public static void Debug(this               ILogContext context,
                             [ConstantExpected] string      messageTemplate,
                             params             object?[]?  propertyValues) =>
        context.Logger.Debug(messageTemplate, propertyValues);

    public static void Debug(this ILogContext context, Exception? exception,
                             [ConstantExpected] string messageTemplate) =>
        context.Logger.Debug(exception, messageTemplate);

    public static void Debug<T>(this ILogContext context, Exception? exception,
                                [ConstantExpected] string messageTemplate,
                                T propertyValue) =>
        context.Logger.Debug(exception, messageTemplate, propertyValue);

    public static void Debug<T0, T1>(this ILogContext context,
                                     Exception? exception,
                                     [ConstantExpected] string messageTemplate,
                                     T0 propertyValue0, T1 propertyValue1) =>
        context.Logger.Debug(exception, messageTemplate, propertyValue0,
                             propertyValue1);

    public static void Debug<T0, T1, T2>(this ILogContext context,
                                         Exception?       exception,
                                         [ConstantExpected]
                                         string messageTemplate,
                                         T0 propertyValue0, T1 propertyValue1,
                                         T2 propertyValue2) =>
        context.Logger.Debug(exception, messageTemplate, propertyValue0,
                             propertyValue1, propertyValue2);

    public static void Debug(this ILogContext context, Exception? exception,
                             [ConstantExpected] string messageTemplate,
                             params object?[]? propertyValues) =>
        context.Logger.Debug(exception, messageTemplate, propertyValues);

    // =========================================================================
    // Information
    // =========================================================================

    public static void Information(this               ILogContext context,
                                   [ConstantExpected] string messageTemplate) =>
        context.Logger.Information(messageTemplate);

    public static void Information<T>(this               ILogContext context,
                                      [ConstantExpected] string messageTemplate,
                                      T propertyValue) =>
        context.Logger.Information(messageTemplate, propertyValue);

    public static void Information<T0, T1>(this ILogContext context,
                                           [ConstantExpected]
                                           string messageTemplate,
                                           T0 propertyValue0,
                                           T1 propertyValue1) =>
        context.Logger.Information(messageTemplate, propertyValue0,
                                   propertyValue1);

    public static void Information<T0, T1, T2>(this ILogContext context,
                                               [ConstantExpected]
                                               string messageTemplate,
                                               T0 propertyValue0,
                                               T1 propertyValue1,
                                               T2 propertyValue2) =>
        context.Logger.Information(messageTemplate, propertyValue0,
                                   propertyValue1, propertyValue2);

    public static void Information(this ILogContext context,
                                   [ConstantExpected] string messageTemplate,
                                   params object?[]? propertyValues) =>
        context.Logger.Information(messageTemplate, propertyValues);

    public static void Information(this ILogContext          context,
                                   Exception?                exception,
                                   [ConstantExpected] string messageTemplate) =>
        context.Logger.Information(exception, messageTemplate);

    public static void Information<T>(this ILogContext context,
                                      Exception? exception,
                                      [ConstantExpected] string messageTemplate,
                                      T propertyValue) =>
        context.Logger.Information(exception, messageTemplate, propertyValue);

    public static void Information<T0, T1>(this ILogContext context,
                                           Exception?       exception,
                                           [ConstantExpected]
                                           string messageTemplate,
                                           T0 propertyValue0,
                                           T1 propertyValue1) =>
        context.Logger.Information(exception, messageTemplate, propertyValue0,
                                   propertyValue1);

    public static void Information<T0, T1, T2>(this ILogContext context,
                                               Exception?       exception,
                                               [ConstantExpected]
                                               string messageTemplate,
                                               T0 propertyValue0,
                                               T1 propertyValue1,
                                               T2 propertyValue2) =>
        context.Logger.Information(exception, messageTemplate, propertyValue0,
                                   propertyValue1, propertyValue2);

    public static void Information(this ILogContext context,
                                   Exception? exception,
                                   [ConstantExpected] string messageTemplate,
                                   params object?[]? propertyValues) =>
        context.Logger.Information(exception, messageTemplate, propertyValues);

    // =========================================================================
    // Warning
    // =========================================================================

    public static void Warning(this               ILogContext context,
                               [ConstantExpected] string messageTemplate) =>
        context.Logger.Warning(messageTemplate);

    public static void Warning<T>(this               ILogContext context,
                                  [ConstantExpected] string messageTemplate,
                                  T propertyValue) =>
        context.Logger.Warning(messageTemplate, propertyValue);

    public static void Warning<T0, T1>(this ILogContext context,
                                       [ConstantExpected]
                                       string messageTemplate,
                                       T0 propertyValue0, T1 propertyValue1) =>
        context.Logger.Warning(messageTemplate, propertyValue0, propertyValue1);

    public static void Warning<T0, T1, T2>(this ILogContext context,
                                           [ConstantExpected]
                                           string messageTemplate,
                                           T0 propertyValue0, T1 propertyValue1,
                                           T2 propertyValue2) =>
        context.Logger.Warning(messageTemplate, propertyValue0, propertyValue1,
                               propertyValue2);

    public static void Warning(this               ILogContext context,
                               [ConstantExpected] string      messageTemplate,
                               params             object?[]?  propertyValues) =>
        context.Logger.Warning(messageTemplate, propertyValues);

    public static void Warning(this ILogContext context, Exception? exception,
                               [ConstantExpected] string messageTemplate) =>
        context.Logger.Warning(exception, messageTemplate);

    public static void Warning<T>(this ILogContext          context,
                                  Exception?                exception,
                                  [ConstantExpected] string messageTemplate,
                                  T                         propertyValue) =>
        context.Logger.Warning(exception, messageTemplate, propertyValue);

    public static void Warning<T0, T1>(this ILogContext context,
                                       Exception?       exception,
                                       [ConstantExpected]
                                       string messageTemplate,
                                       T0 propertyValue0, T1 propertyValue1) =>
        context.Logger.Warning(exception, messageTemplate, propertyValue0,
                               propertyValue1);

    public static void Warning<T0, T1, T2>(this ILogContext context,
                                           Exception?       exception,
                                           [ConstantExpected]
                                           string messageTemplate,
                                           T0 propertyValue0, T1 propertyValue1,
                                           T2 propertyValue2) =>
        context.Logger.Warning(exception, messageTemplate, propertyValue0,
                               propertyValue1, propertyValue2);

    public static void Warning(this ILogContext context, Exception? exception,
                               [ConstantExpected] string messageTemplate,
                               params object?[]? propertyValues) =>
        context.Logger.Warning(exception, messageTemplate, propertyValues);

    // =========================================================================
    // Error
    // =========================================================================

    public static void Error(this               ILogContext context,
                             [ConstantExpected] string      messageTemplate) =>
        context.Logger.Error(messageTemplate);

    public static void Error<T>(this               ILogContext context,
                                [ConstantExpected] string      messageTemplate,
                                T                              propertyValue) =>
        context.Logger.Error(messageTemplate, propertyValue);

    public static void Error<T0, T1>(this               ILogContext context,
                                     [ConstantExpected] string messageTemplate,
                                     T0 propertyValue0, T1 propertyValue1) =>
        context.Logger.Error(messageTemplate, propertyValue0, propertyValue1);

    public static void Error<T0, T1, T2>(this ILogContext context,
                                         [ConstantExpected]
                                         string messageTemplate,
                                         T0 propertyValue0, T1 propertyValue1,
                                         T2 propertyValue2) =>
        context.Logger.Error(messageTemplate, propertyValue0, propertyValue1,
                             propertyValue2);

    public static void Error(this               ILogContext context,
                             [ConstantExpected] string      messageTemplate,
                             params             object?[]?  propertyValues) =>
        context.Logger.Error(messageTemplate, propertyValues);

    public static void Error(this ILogContext context, Exception? exception,
                             [ConstantExpected] string messageTemplate) =>
        context.Logger.Error(exception, messageTemplate);

    public static void Error<T>(this ILogContext context, Exception? exception,
                                [ConstantExpected] string messageTemplate,
                                T propertyValue) =>
        context.Logger.Error(exception, messageTemplate, propertyValue);

    public static void Error<T0, T1>(this ILogContext context,
                                     Exception? exception,
                                     [ConstantExpected] string messageTemplate,
                                     T0 propertyValue0, T1 propertyValue1) =>
        context.Logger.Error(exception, messageTemplate, propertyValue0,
                             propertyValue1);

    public static void Error<T0, T1, T2>(this ILogContext context,
                                         Exception?       exception,
                                         [ConstantExpected]
                                         string messageTemplate,
                                         T0 propertyValue0, T1 propertyValue1,
                                         T2 propertyValue2) =>
        context.Logger.Error(exception, messageTemplate, propertyValue0,
                             propertyValue1, propertyValue2);

    public static void Error(this ILogContext context, Exception? exception,
                             [ConstantExpected] string messageTemplate,
                             params object?[]? propertyValues) =>
        context.Logger.Error(exception, messageTemplate, propertyValues);

    // =========================================================================
    // Fatal
    // =========================================================================

    public static void Fatal(this               ILogContext context,
                             [ConstantExpected] string      messageTemplate) =>
        context.Logger.Fatal(messageTemplate);

    public static void Fatal<T>(this               ILogContext context,
                                [ConstantExpected] string      messageTemplate,
                                T                              propertyValue) =>
        context.Logger.Fatal(messageTemplate, propertyValue);

    public static void Fatal<T0, T1>(this               ILogContext context,
                                     [ConstantExpected] string messageTemplate,
                                     T0 propertyValue0, T1 propertyValue1) =>
        context.Logger.Fatal(messageTemplate, propertyValue0, propertyValue1);

    public static void Fatal<T0, T1, T2>(this ILogContext context,
                                         [ConstantExpected]
                                         string messageTemplate,
                                         T0 propertyValue0, T1 propertyValue1,
                                         T2 propertyValue2) =>
        context.Logger.Fatal(messageTemplate, propertyValue0, propertyValue1,
                             propertyValue2);

    public static void Fatal(this               ILogContext context,
                             [ConstantExpected] string      messageTemplate,
                             params             object?[]?  propertyValues) =>
        context.Logger.Fatal(messageTemplate, propertyValues);

    public static void Fatal(this ILogContext context, Exception? exception,
                             [ConstantExpected] string messageTemplate) =>
        context.Logger.Fatal(exception, messageTemplate);

    public static void Fatal<T>(this ILogContext context, Exception? exception,
                                [ConstantExpected] string messageTemplate,
                                T propertyValue) =>
        context.Logger.Fatal(exception, messageTemplate, propertyValue);

    public static void Fatal<T0, T1>(this ILogContext context,
                                     Exception? exception,
                                     [ConstantExpected] string messageTemplate,
                                     T0 propertyValue0, T1 propertyValue1) =>
        context.Logger.Fatal(exception, messageTemplate, propertyValue0,
                             propertyValue1);

    public static void Fatal<T0, T1, T2>(this ILogContext context,
                                         Exception?       exception,
                                         [ConstantExpected]
                                         string messageTemplate,
                                         T0 propertyValue0, T1 propertyValue1,
                                         T2 propertyValue2) =>
        context.Logger.Fatal(exception, messageTemplate, propertyValue0,
                             propertyValue1, propertyValue2);

    public static void Fatal(this ILogContext context, Exception? exception,
                             [ConstantExpected] string messageTemplate,
                             params object?[]? propertyValues) =>
        context.Logger.Fatal(exception, messageTemplate, propertyValues);

    // =========================================================================
    // Level & Context Utilities
    // =========================================================================

    public static bool
        IsEnabled(this ILogContext context, LogEventLevel level) =>
        context.Logger.IsEnabled(level);

    public static ILogger ForContext<TSource>(this ILogContext context) =>
        context.Logger.ForContext<TSource>();

    public static ILogger ForContext(this ILogContext context, Type source) =>
        context.Logger.ForContext(source);

    public static ILogger ForContext(this ILogContext context,
                                     string propertyName, object? value,
                                     bool destructureObjects = false) =>
        context.Logger.ForContext(propertyName, value, destructureObjects);
}
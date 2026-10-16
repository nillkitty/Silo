using System.Windows.Controls;
using Silo.Diagnostics;

namespace Silo.Contracts;

/// <summary>
///     Defines debugging surfaces for type <typeparamref name="TType" />
/// </summary>
/// <remarks>
///     When in debug mode (or when entering debug mode), Silo will interrogate the following types of
///     objects for debug surfaces:
///     <list type="bullet">
///         <item>Connections</item>
///         <item>Nodes</item>
///         <item>Other Node Models</item>
///         <item>Any other item(s) returned from DI</item>
///     </list>
///     The interrogation that occurs is:
///     <list type="number">
///         <item>If the target object is IDebug{Self}, it is interrogated directly.</item>
///         <item>
///             Otherwise IDebug{TargetType} is resolved via DI.   Any registered instances
///             returned are interogated via <see cref="GetSurfaces" />.
///         </item>
///     </list>
/// </remarks>
/// <typeparam name="TType">The type which this object attaches debug surfaces to</typeparam>
public interface IDebug<TType>
{
    IEnumerable<IDebugSurface> GetSurfaces();
}

/// <summary>
///     Service which provides access to debug mode controls
/// </summary>
public interface IDebugMode
{
    bool IsDebugMode { get; set; }
}

/// <summary>
///     A debug surface which is contributed by an object implementing
///     <see cref="IDebug{TType}" /> which extends the user interface in Debug Mode.
/// </summary>
public interface IDebugSurface
{
    void Install(DebugContext dc);
    void Remove(DebugContext  dc);
}

public interface IAdorner<TType> : IAdorner
{
    Type IAdorner.TargetType => typeof(TType);
}

public interface IAdorner
{
    Type     TargetType { get; }
    Control? GetControl(AdornContext ctx);
}

public interface IAdornmentManager
{
    /// <summary>
    ///     Whether to show author adorners when in author, dev, or debug mode.
    /// </summary>
    bool ShowAuthorAdorners { get; set; }

    /// <summary>
    ///     Whether to show debug adorners when in Debug mode;  Debug Adorners are obtained via:
    ///     1. A control or model implementing IDebug and configuring adorners in the DebugContext
    ///     2. A model of type T having an associated IDebug{T} registered that configures adorners in the DebugContext
    ///     3. A control with a DataContext of type T having an associated IDebug{T}, etc
    /// </summary>
    bool ShowDebugAdorners { get; set; }

    /// <summary>
    ///     Whether to show development adorners when in dev or debug mode.
    /// </summary>
    bool ShowDevAdorners { get; set; }

    /// <summary>
    ///     Called to initialize the adornment manager on a new Window
    /// </summary>
    void Init(Window window);
}
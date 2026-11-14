using Serilog;

namespace Silo.ExternalTools;

/// <summary>
///     The registry of external tools: programs, web pages and commands that act on a type of target -- an
///     <see cref="System.Net.IPAddress" />, a <see cref="DomainName" />, a <see cref="FileInfo" />, or any other type.
///     <list type="bullet">
///         <item><see cref="Register{T}" /> adds a tool for targets of type <c>T</c> (and of the types derived from it).</item>
///         <item>
///             <see cref="RegisterTarget{TModel}" /> says which targets an Explorer node's model stands for (a DNS name node
///             stands for its <see cref="DomainName" />, a host of the network tree for its address...). An object that is
///             itself of a type tools are registered for is its own target.
///         </item>
///     </list>
///     The tools Silo comes with (<see cref="BuiltInExternalTools" />) are registered the first time the registry is used;
///     <see cref="ExternalToolActions" /> puts them in the Node menu and the Explorer's right-click menu.
/// </summary>
public static class ExternalTools
{
    private static readonly object Gate = new();

    private static readonly List<ExternalTool>                          Tools   = [];
    private static readonly List<(Type Model, Func<object, IEnumerable<object>> Targets)> Adapters = [];

    private static bool _builtIns;

    /// <summary>Every registered tool, in registration order.</summary>
    public static IReadOnlyList<ExternalTool> All
    {
        get
        {
            EnsureBuiltIns();
            lock (Gate) return Tools.ToList();
        }
    }

    /// <summary>
    ///     Adds a tool for targets of type <typeparamref name="T" />. A tool with the same id for the same type replaces the one
    ///     there was (so a tool Silo comes with can be swapped for another).
    /// </summary>
    /// <param name="id">Identifies the tool among those of its target type.</param>
    /// <param name="header">The menu entry's text ("_" before a letter makes it the access key).</param>
    /// <param name="iconKey">The key of its icon (see <see cref="ExternalToolIcons" />).</param>
    /// <param name="section">The submenu it goes in.</param>
    /// <param name="toolTip">What it does, in a sentence.</param>
    /// <param name="run">What it does for a target.</param>
    /// <param name="when">Whether it applies to a target; null: always.</param>
    /// <param name="requiresProgram">A program it needs that does not come with Windows (it is disabled when that is not on the PATH).</param>
    /// <param name="featured">Also offer it at the top of the menu, outside its section.</param>
    public static ExternalTool Register<T>(string id, string header, string iconKey, ExternalToolSection section, string toolTip,
                                           Func<T, ExternalToolRun> run, Func<T, bool>? when = null, string? requiresProgram = null,
                                           bool featured = false) where T : notnull
    {
        var tool = new ExternalTool(id, header, iconKey, section, toolTip, typeof(T), t => run((T)t),
                                    when is null ? null : t => when((T)t), requiresProgram, featured);
        Register(tool);
        return tool;
    }

    /// <summary>Adds a tool (see <see cref="Register{T}" />).</summary>
    public static void Register(ExternalTool tool)
    {
        // the built-in tools go in first, so one registered here replaces the built-in tool of its id rather than the other way round
        EnsureBuiltIns();
        lock (Gate)
        {
            Tools.RemoveAll(t => t.TargetType == tool.TargetType && t.Id == tool.Id);
            Tools.Add(tool);
        }
    }

    /// <summary>Removes the tool with the id <paramref name="id" /> registered for <typeparamref name="T" />; false when there is none.</summary>
    public static bool Unregister<T>(string id)
    {
        EnsureBuiltIns();
        lock (Gate) return Tools.RemoveAll(t => t.TargetType == typeof(T) && t.Id == id) > 0;
    }

    /// <summary>
    ///     Says which targets a model of type <typeparamref name="TModel" /> (or of a type derived from it) stands for: the
    ///     DNS name of a name node, the address of a host... <paramref name="target" /> returns null when the model stands for
    ///     none (a subnet rather than a host, say).
    /// </summary>
    public static void RegisterTarget<TModel>(Func<TModel, object?> target)
    {
        RegisterTargets<TModel>(m => target(m) is { } t ? [t] : []);
    }

    /// <summary>Says which targets a model stands for, when it can stand for several (see <see cref="RegisterTarget{TModel}" />).</summary>
    public static void RegisterTargets<TModel>(Func<TModel, IEnumerable<object>> targets)
    {
        EnsureBuiltIns();
        lock (Gate)
            Adapters.Add((typeof(TModel), m => targets((TModel)m)));
    }

    /// <summary>
    ///     The targets <paramref name="model" /> stands for: itself, when it is of a type tools are registered for, and those
    ///     the registered adapters find in it. Each once, in that order.
    /// </summary>
    public static IReadOnlyList<object> TargetsOf(object? model)
    {
        var result = new List<object>();
        if (model is null) return result;

        EnsureBuiltIns();
        List<ExternalTool>                                          tools;
        List<(Type Model, Func<object, IEnumerable<object>> Targets)> adapters;
        lock (Gate)
        {
            tools    = Tools.ToList();
            adapters = Adapters.ToList();
        }

        if (tools.Any(t => t.TargetType.IsInstanceOfType(model))) result.Add(model);

        foreach (var (type, targets) in adapters)
        {
            if (!type.IsInstanceOfType(model)) continue;

            try
            {
                foreach (var target in targets(model))
                    if (!result.Contains(target))
                        result.Add(target);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Finding the external tool targets of {model} failed", model.GetType().Name);
            }
        }

        return result;
    }

    /// <summary>The tools that apply to <paramref name="target" />, in registration order.</summary>
    public static IReadOnlyList<ExternalTool> For(object target)
    {
        return All.Where(t =>
                         {
                             try
                             {
                                 return t.AppliesTo(target);
                             }
                             catch (Exception ex)
                             {
                                 Log.Warning(ex, "External tool {tool} failed to say whether it applies to {target}", t.Id, target);
                                 return false;
                             }
                         })
                  .ToList();
    }

    private static void EnsureBuiltIns()
    {
        lock (Gate)
        {
            if (_builtIns) return;

            _builtIns = true;
            BuiltInExternalTools.Register();
        }
    }
}

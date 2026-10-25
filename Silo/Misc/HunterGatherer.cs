using System.Windows.Controls;
using Serilog;

namespace Silo.Ui;

public interface IWindowChrome : IAppliesTo<Window>;

public interface IControlMod : IAppliesTo<Control>;

public interface IAppliesTo<in TTarget>
{
    void Apply(TTarget target);
}

/// <summary>
///     A utility which gathers configured Mods (modifications) and
///     then applies them to one or more target objects.
/// </summary>
/// <typeparam name="TAttribute"></typeparam>
public class HunterGatherer<TAttribute>
{
    public IEnumerable<Mod> GatheredMods { get; set; } = [];


    public object? Target { get; set; }

    public void GatherAndApplyTo<TTarget>(TTarget target, bool throwErrors = false)
    {
        target.Required();
        if (Gather())
            Apply(target, throwErrors);
    }

    public bool Apply<TTarget>(TTarget target, bool throwErrors)
    {
        if (GatheredMods?.ToList() is null or [])
        {
            if (throwErrors)
                throw new InvalidOperationException("No hunted ites were gathered");
            return false;
        }

        bool b = false;
        foreach (var h in GatheredMods)
            try
            {
                b |= h.ApplyTo(target!);
                Log.Debug("Applied mod '{mod}' to target '{target}'", h, target);
            }
            catch (Exception ex) when (!throwErrors)
            {
                Log.Error(ex, "Exception thrown while applying " + "mod '{mod}'' to object '{target}:  {ex}'.", h, target, ex);
            }

        return b;
    }

    /// <summary>
    ///     Gathers all of the <see cref="Mod" />s contributed by attributes into the <see cref="GatheredMods" /> collection.
    /// </summary>
    /// <returns>Returns true if at least one Mod was gathered; otherwise false.</returns>
    private bool Gather()
    {
        List<Mod> l  = [];
        var       tt = typeof(TAttribute);
        var       ca = tt.GetCustomAttributes(tt, true);
        foreach (var a in ca) Log.Debug("Attribute:  {attr}", a);

        Log.Debug("Gathered {num} mods of type '{type}'", l.Count, tt);
        GatheredMods = l;
        return l.Any();
    }

    /// <summary>
    ///     A utility which gathers configured Mods (modifications) and
    ///     then applies them to one or more target objects.
    /// </summary>
    /// <typeparam name="TAttribute"></typeparam>
    /// <param name="target"></param>
    public HunterGatherer(object target)
    {
        Target = target.Required();
    }

    public HunterGatherer()
    {
    }
}

public class Mod
{
    public Func<object, bool> Applicator { get; set; } = _ => false;

    public bool ApplyTo(object target)
    {
        return Applicator.Invoke(target);
    }
}

public class Mod<TTarget> : Mod
{
    public new Func<TTarget, bool> Applicator { get; set; } = _ => false;

    public bool ApplyTo(TTarget target)
    {
        return Applicator.Invoke(target);
    }
}
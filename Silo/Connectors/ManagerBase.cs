using Serilog;

namespace Silo.Connectors;

/// <summary>
///     Keeps everything of one kind there is: what is registered with the container as <typeparamref name="TContract" /> itself and what the
///     providers (<typeparamref name="TProviderContract" />) supply, each counted once by its key. What was gathered is kept until
///     <see cref="Refresh" />. A provider that throws is logged and skipped, so one bad provider does not hide the rest.
/// </summary>
/// <remarks>
///     What is registered comes first and wins over a provider's version of the same thing (a program that registered itself knows more about
///     itself than one that was found by looking). A provider is not an <see cref="IProvider{TType}" /> (those are handed a receiver to push to)
///     but anything that can be asked for what it has, so the constraint is only that it is a class.
/// </remarks>
public abstract class ManagerBase<TContract, TProviderContract>
    where TContract : class
    where TProviderContract : class
{
    private volatile IReadOnlyList<TContract>? _all;

    /// <summary>Everything, in the order found.</summary>
    protected IReadOnlyList<TContract> All => _all ?? (_all = Gather());

    /// <summary>Forgets what was gathered, so the next question asks again (something was installed, a provider changed its mind).</summary>
    public virtual void Refresh()
    {
        _all = null;
    }

    /// <summary>What is registered with the container as <typeparamref name="TContract" />.</summary>
    protected virtual IEnumerable<TContract> ResolveContracts()
    {
        return App.ResolveAll<TContract>();
    }

    /// <summary>What is registered with the container as <typeparamref name="TProviderContract" />.</summary>
    protected virtual IEnumerable<TProviderContract> ResolveProviders()
    {
        return App.ResolveAll<TProviderContract>();
    }

    /// <summary>What one provider has.</summary>
    protected abstract IEnumerable<TContract> Provide(TProviderContract provider);

    /// <summary>
    ///     What makes two of them the same one (the program of a browser); null when it has none, which leaves it out. Compared
    ///     without regard to case.
    /// </summary>
    protected abstract string? KeyOf(TContract item);

    private List<TContract> Gather()
    {
        var found = new List<TContract>();
        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in Ask(ResolveContracts, "the registered " + typeof(TContract).Name))
            Add(found, seen, item, "the container");

        foreach (var provider in Ask(ResolveProviders, "the providers of " + typeof(TContract).Name))
        {
            string name = provider.GetType().Name;
            foreach (var item in Ask(() => Provide(provider), name))
                Add(found, seen, item, name);
        }

        return found;
    }

    private void Add(List<TContract> found, HashSet<string> seen, TContract? item, string source)
    {
        if (item is null) return;

        string? key;
        try
        {
            key = KeyOf(item);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "{source} gave a {type} that cannot be told from the others", source, typeof(TContract).Name);
            return;
        }

        if (key is null)
        {
            Log.Debug("{source} gave a {type} with nothing to tell it apart by: left out", source, typeof(TContract).Name);
            return;
        }

        if (seen.Add(key)) found.Add(item);
    }

    private static List<T> Ask<T>(Func<IEnumerable<T>?> get, string what)
    {
        try
        {
            return get()?.ToList() ?? [];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Asking {what} failed", what);
            return [];
        }
    }
}

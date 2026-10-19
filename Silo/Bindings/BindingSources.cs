using System.Collections.Concurrent;
using System.Diagnostics;

namespace Silo.Bindings;

/// <summary>
///     Opens readers for one URI scheme of <see cref="Silo.DbModel.Global.DataBinding.Source" /> (e.g. <c>perf</c>).
/// </summary>
public interface IBindingSource
{
    /// <summary>The scheme handled, without the colon.</summary>
    string Scheme { get; }

    /// <summary>A short example source shown in the editor.</summary>
    IEnumerable<string> Examples { get; }

    /// <summary>Opens a reader for <paramref name="source" />.</summary>
    /// <exception cref="ArgumentException">The source is malformed or names something that does not exist.</exception>
    IBindingReader Open(string source);
}

/// <summary>
///     Samples one source. Called from a background thread, one call at a time.
/// </summary>
public interface IBindingReader : IDisposable
{
    object? Read();
}

/// <summary>
///     The registered <see cref="IBindingSource" />s, by scheme.
/// </summary>
public static class BindingSources
{
    private static readonly ConcurrentDictionary<string, IBindingSource> Sources = new(StringComparer.OrdinalIgnoreCase);

    static BindingSources()
    {
        Register(new PerfCounterSource());
    }

    public static IEnumerable<IBindingSource> All => Sources.Values;

    public static void Register(IBindingSource source)
    {
        Sources[source.Scheme] = source;
    }

    /// <summary>The source for <paramref name="uri" />'s scheme, or null.</summary>
    public static IBindingSource? For(string? uri)
    {
        int colon = uri?.IndexOf(':') ?? -1;
        return colon > 0 && Sources.TryGetValue(uri![..colon], out var s) ? s : null;
    }

    /// <summary>Opens a reader for <paramref name="uri" />.</summary>
    /// <exception cref="ArgumentException">No source handles the scheme, or the source rejects it.</exception>
    public static IBindingReader Open(string uri)
    {
        var source = For(uri) ??
                     throw new ArgumentException($"No data source handles '{uri}'. Known schemes: {string.Join(", ", Sources.Keys.Select(k => k + ":"))}.");
        return source.Open(uri);
    }
}

/// <summary>
///     Windows performance counters: <c>perf:///Category(Instance)/Counter</c>, or <c>perf://machine/...</c> for another
///     computer. Parts are URL-decoded after splitting, so write <c>%25</c> for <c>%</c> and <c>%2F</c> for a <c>/</c> inside
///     a name; a <c>\</c> may also separate category and counter. Instance <c>*</c> sums every instance; a category without
///     instances is written without parentheses (<c>perf:///Memory/Available MBytes</c>).
/// </summary>
public sealed class PerfCounterSource : IBindingSource
{
    public string Scheme => "perf";

    public IEnumerable<string> Examples =>
    [
        "perf:///Processor(_Total)/%25 Processor Time",
        "perf:///Memory/Available MBytes",
        "perf:///PhysicalDisk(_Total)/Disk Transfers%2Fsec",
        "perf:///Network Interface(*)\\Bytes Total/sec"
    ];

    public IBindingReader Open(string source)
    {
        var (machine, category, instance, counter) = Parse(source);

        if (!OperatingSystem.IsWindows()) throw new ArgumentException("Performance counters are only available on Windows.");
        if (!PerformanceCounterCategory.Exists(category, machine))
            throw new ArgumentException($"There is no performance counter category '{category}'" + (machine == "." ? "." : $" on {machine}."));
        if (!PerformanceCounterCategory.CounterExists(counter, category, machine))
            throw new ArgumentException($"The category '{category}' has no counter '{counter}'.");

        return new Reader(machine, category, instance, counter);
    }

    /// <summary>Splits a <c>perf:</c> URI into its parts.</summary>
    /// <exception cref="ArgumentException">Malformed.</exception>
    public static (string Machine, string Category, string? Instance, string Counter) Parse(string source)
    {
        if (!source.StartsWith("perf:", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"'{source}' is not a perf: source.");

        string rest    = source[5..];
        string machine = ".";
        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            rest = rest[2..];
            int slash = rest.IndexOf('/');
            if (slash < 0) throw new ArgumentException($"'{source}' names no category.");
            if (slash > 0) machine = Uri.UnescapeDataString(rest[..slash]);
            rest = rest[(slash + 1)..];
        }

        rest = rest.TrimStart('/');

        // The category ends at the first separator after its (instance), if it has one.
        int open = rest.IndexOf('('), sep;
        int firstSep = rest.IndexOfAny(['/', '\\']);
        if (open >= 0 && (firstSep < 0 || open < firstSep))
        {
            int close = rest.IndexOf(')', open);
            if (close < 0) throw new ArgumentException($"'{source}' has an unclosed instance '('.");
            sep = rest.IndexOfAny(['/', '\\'], close);
        }
        else
        {
            sep = firstSep;
        }

        if (sep <= 0 || sep == rest.Length - 1) throw new ArgumentException($"'{source}' should look like perf:///Category(Instance)/Counter.");

        string head    = rest[..sep];
        string counter = Uri.UnescapeDataString(rest[(sep + 1)..]);
        string? instance = null;
        string category;

        int paren = head.IndexOf('(');
        if (paren >= 0)
        {
            category = Uri.UnescapeDataString(head[..paren]);
            instance = Uri.UnescapeDataString(head[(paren + 1)..head.LastIndexOf(')')]);
        }
        else
        {
            category = Uri.UnescapeDataString(head);
        }

        return (machine, category.Trim(), instance, counter.Trim());
    }

    private sealed class Reader(string machine, string category, string? instance, string counter) : IBindingReader
    {
        private readonly Dictionary<string, PerformanceCounter> _counters = new(StringComparer.OrdinalIgnoreCase);
        private          DateTime                               _listed;

        public object? Read()
        {
            if (!OperatingSystem.IsWindows()) return null;

            if (instance != "*")
            {
                if (_counters.Count == 0) _counters[instance ?? ""] = new PerformanceCounter(category, counter, instance ?? "", machine);
                return Math.Round(_counters.Values.First().NextValue(), 3);
            }

            // Every instance, summed; the instance list is refreshed now and then (adapters and disks come and go).
            if (DateTime.UtcNow - _listed > TimeSpan.FromSeconds(30))
            {
                _listed = DateTime.UtcNow;
                var names = new PerformanceCounterCategory(category, machine).GetInstanceNames()
                                                                              .Where(n => !n.Equals("_Total", StringComparison.OrdinalIgnoreCase))
                                                                              .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var gone in _counters.Keys.Where(k => !names.Contains(k)).ToList())
                {
                    _counters[gone].Dispose();
                    _counters.Remove(gone);
                }

                foreach (var name in names.Where(n => !_counters.ContainsKey(n)))
                    _counters[name] = new PerformanceCounter(category, counter, name, machine);
            }

            double sum = 0;
            foreach (var c in _counters.Values)
                try
                {
                    sum += c.NextValue();
                }
                catch (InvalidOperationException)
                {
                    _listed = default; // the instance went away; relist next time
                }

            return Math.Round(sum, 3);
        }

        public void Dispose()
        {
            if (!OperatingSystem.IsWindows()) return;
            foreach (var c in _counters.Values) c.Dispose();
            _counters.Clear();
        }
    }
}

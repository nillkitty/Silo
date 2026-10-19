namespace Silo.DbModel.Global;

/// <summary>
///     A live value the Silo samples on a timer from a source such as a performance counter, kept in the Silo document.
///     Diagram cells show it with a <c>{bind:Name}</c> token (see <c>DiagramBindings</c>).
/// </summary>
public class DataBinding
{
    public int Id { get; set; }

    /// <summary>The name used in tokens (<c>{bind:Name}</c>): letters, digits, <c>_</c>, <c>-</c> and <c>.</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Where the value comes from, e.g. <c>perf:///Processor(_Total)/%25 Processor Time</c>.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>How often the value is sampled.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Whether the value is sampled while the Silo is open.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>An optional note about what the value is.</summary>
    public string? Description { get; set; }

    public DateTime Created { get; set; } = DateTime.UtcNow;

    public DateTime Modified { get; set; } = DateTime.UtcNow;

    /// <summary>A shallow copy.</summary>
    public DataBinding Clone()
    {
        return (DataBinding)MemberwiseClone();
    }
}

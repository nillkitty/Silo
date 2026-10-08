namespace Silo.DbModel.Global;

/// <summary>
///     A draw.io diagram stored inside the Silo document, one row per diagram: the notes, drawings and
///     design-world sketches that travel with the Silo.
/// </summary>
public class Diagram
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>What the diagram is for, e.g. <c>canvas</c> or <c>model</c>.</summary>
    public string Kind { get; set; } = "canvas";

    /// <summary>Name of the <c>DrawioProfile</c> (restriction set) the diagram opens with.</summary>
    public string Profile { get; set; } = string.Empty;

    /// <summary>
    ///     Per-diagram <c>DrawioOptions</c> that replace the profile's for just this diagram (JSON), or
    ///     null to follow the profile.
    /// </summary>
    public string? OptionsJson { get; set; }

    /// <summary>The draw.io XML (without any cells Silo regenerates on open).</summary>
    public string Xml { get; set; } = string.Empty;

    public DateTime Created { get; set; } = DateTime.UtcNow;

    public DateTime Modified { get; set; } = DateTime.UtcNow;

    /// <summary>A shallow copy, for "the same diagram with these fields changed" (what <c>with</c> did on the old record).</summary>
    public Diagram Clone()
    {
        return (Diagram)MemberwiseClone();
    }
}

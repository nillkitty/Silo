namespace Silo.DbModel.Global;

/// <summary>
///     A draw.io diagram stored inside the Silo document, one row per diagram: the notes, drawings and
///     design-world sketches that travel with the Silo.
/// </summary>
/// <param name="Kind">What the diagram is for, e.g. <c>canvas</c> or <c>model</c>.</param>
/// <param name="Profile">Name of the <c>DrawioProfile</c> (restriction set) the diagram opens with.</param>
/// <param name="OptionsJson">
///     Per-diagram <c>DrawioOptions</c> that replace the profile's for just this diagram (JSON), or
///     null to follow the profile.
/// </param>
/// <param name="Xml">The draw.io XML (without any cells Silo regenerates on open).</param>
public record Diagram(
    int      Id,
    string   Name,
    string   Kind,
    string   Profile,
    string?  OptionsJson,
    string   Xml,
    DateTime Created,
    DateTime Modified);

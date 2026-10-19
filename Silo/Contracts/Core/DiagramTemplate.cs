using System.IO;
using Diagram = Silo.DbModel.Global.Diagram;

namespace Silo.Contracts.Core;

/// <summary>
///     A diagram a <see cref="SiloTemplate" /> adds to a new Silo, its draw.io XML read from a <c>.drawio</c> file rather than
///     written inline (draw.io XML does not survive the XAML compiler well, and a separate file can be edited in draw.io).
/// </summary>
/// <remarks>
///     <see cref="Source" /> is looked up first among the built-in template diagrams (<c>Resources/Templates/*.drawio</c>,
///     embedded as <c>silo-template/&lt;file&gt;</c>), then as a file path: absolute, or relative to the user templates folder
///     (<see cref="SiloTemplateCatalog.UserFolder" />).
/// </remarks>
public class DiagramTemplate
{
    /// <summary>What the diagram is for (see <see cref="Diagram.Kind" />).</summary>
    public string Kind { get; set; } = "canvas";

    /// <summary>The diagram's name in the new Silo.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The draw.io profile it opens with (see <see cref="Diagram.Profile" />).</summary>
    public string Profile { get; set; } = "canvas";

    /// <summary>The <c>.drawio</c> file holding the diagram, e.g. <c>RealTimeDiagram.drawio</c>.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>The diagram to store, with its XML loaded.</summary>
    /// <exception cref="FileNotFoundException">The source cannot be found.</exception>
    public Diagram ToDiagram()
    {
        return new Diagram { Name = Name, Kind = Kind, Profile = Profile, Xml = ReadSource() };
    }

    public override string ToString()
    {
        return $"Diagram '{Name}' from {Source}";
    }

    private string ReadSource()
    {
        var asm = typeof(DiagramTemplate).Assembly;
        using (var stream = asm.GetManifestResourceStream("silo-template/" + System.IO.Path.GetFileName(Source)))
            if (stream is not null)
                using (var reader = new StreamReader(stream))
                    return reader.ReadToEnd();

        string path = System.IO.Path.IsPathRooted(Source) ? Source : System.IO.Path.Combine(SiloTemplateCatalog.UserFolder, Source);
        if (System.IO.File.Exists(path)) return System.IO.File.ReadAllText(path);

        throw new FileNotFoundException($"The template diagram '{Source}' was not found (built in, or in {SiloTemplateCatalog.UserFolder}).", Source);
    }
}

using System.IO;
using System.Linq;
using System.Threading;
using Diagram = Silo.DbModel.Global.Diagram;

namespace Silo.Drawio;

/// <summary>
///     Reads and writes the diagrams stored inside a Silo document. Implemented by the SQLite provider;
///     a Silo's <c>IDatabaseProvider</c> that does not implement it simply cannot hold diagrams.
/// </summary>
public interface IDiagramStore
{
    bool DeleteDiagram(int id);

    Diagram? GetDiagram(int id);

    IReadOnlyList<Diagram> ListDiagrams();

    /// <summary>Inserts (<c>Id &lt;= 0</c>) or updates a diagram and returns it with its id assigned.</summary>
    Diagram SaveDiagram(Diagram diagram);
}

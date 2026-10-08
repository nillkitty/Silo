using System.IO;
using System.Linq;
using System.Threading;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Diagram = Silo.DbModel.Global.Diagram;
using Silo.Drawio;

namespace Silo.DbModel.Sqlite;

/// <summary>The diagram table access (<see cref="IDiagramStore" />) of the SQLite provider.</summary>
public sealed partial class SqliteDatabaseProvider : IDiagramStore
{
    private const string DiagramColumns = "Id, Name, Kind, Profile, OptionsJson, Xml, Created, Modified";

    public IReadOnlyList<Diagram> ListDiagrams()
    {
        _gate.Wait();
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT {DiagramColumns} FROM [main].[Diagram] ORDER BY Name COLLATE NOCASE";

            var list = new List<Diagram>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add(ReadDiagram(reader));
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Diagram? GetDiagram(int id)
    {
        _gate.Wait();
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT {DiagramColumns} FROM [main].[Diagram] WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", id);

            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadDiagram(reader) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Diagram SaveDiagram(Diagram diagram)
    {
        _gate.Wait();
        try
        {
            using var tx = _conn.BeginTransaction();
            try
            {
                if (diagram.Id <= 0)
                {
                    using var idCmd = _conn.CreateCommand();
                    idCmd.Transaction = tx;
                    idCmd.CommandText = "SELECT COALESCE(MAX(Id), 0) + 1 FROM [main].[Diagram]";
                    diagram = diagram with { Id = Convert.ToInt32(idCmd.ExecuteScalar(), CultureInfo.InvariantCulture) };
                }

                UpsertRecursive(diagram, tx, new HashSet<object>(ReferenceEqualityComparer.Instance));
                tx.Commit();
                return diagram;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool DeleteDiagram(int id)
    {
        _gate.Wait();
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM [main].[Diagram] WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static Diagram ReadDiagram(SqliteDataReader r)
    {
        return new Diagram(r.GetInt32(0),
                           r.GetString(1),
                           r.GetString(2),
                           r.GetString(3),
                           r.IsDBNull(4) ? null : r.GetString(4),
                           r.GetString(5),
                           ParseTime(r.GetString(6)),
                           ParseTime(r.GetString(7)));
    }

    private static DateTime ParseTime(string s)
    {
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : DateTime.UtcNow;
    }
}

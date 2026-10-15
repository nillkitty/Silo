using System.Collections.Generic;
using Silo.DbModel.Global;

namespace Silo.Tabular;

/// <summary>
///     Reads and writes the authored tables stored inside a Silo document. Implemented by the SQLite provider; a Silo
///     whose <c>IDatabaseProvider</c> does not implement it cannot hold authored tables.
/// </summary>
public interface ITableStore
{
    bool DeleteTable(int id);

    AuthoredTable? GetTable(int id);

    IReadOnlyList<AuthoredTable> ListTables();

    /// <summary>Inserts (<c>Id &lt;= 0</c>) or updates a table and returns it with its id assigned.</summary>
    AuthoredTable SaveTable(AuthoredTable table);
}

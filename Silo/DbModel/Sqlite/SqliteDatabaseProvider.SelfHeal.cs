using Microsoft.Data.Sqlite;
using Serilog;

namespace Silo.DbModel.Sqlite;

/// <summary>Repairs a Silo whose tables are behind the model, when an operation finds out (rather than only when the Silo is opened).</summary>
public sealed partial class SqliteDatabaseProvider
{
    /// <summary>SQLite's generic "SQL error" result code, which "no such column" and "no such table" come with.</summary>
    private const int SqliteError = 1;

    /// <summary>
    ///     Runs <paramref name="operation" />; if it fails because a column or table the model has is missing from the Silo
    ///     (an older build made it, or bringing it up to date when it was opened did not finish), adds what is missing
    ///     (<see cref="EntitySchema.EnsureFixedSchemas" />) and runs it once more. The caller holds the gate.
    /// </summary>
    private T Healing<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (SqliteException ex) when (IsMissingSchema(ex))
        {
            Log.Warning(ex, "The Silo is missing part of its schema ({message}); repairing it and trying again", ex.Message);
            EntitySchema.EnsureFixedSchemas(_conn, _layout, false);
            return operation();
        }
    }

    private void Healing(Action operation)
    {
        Healing(() =>
                {
                    operation();
                    return true;
                });
    }

    private static bool IsMissingSchema(SqliteException ex)
    {
        return ex.SqliteErrorCode == SqliteError &&
               (ex.Message.Contains("no such column", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase));
    }
}

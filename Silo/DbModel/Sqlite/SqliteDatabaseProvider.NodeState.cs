namespace Silo.DbModel.Sqlite;

/// <summary>How the Explorer tree was left (<see cref="INodeStateStore" />) in the SQLite provider.</summary>
/// <remarks>Like the window layout, none of this raises <c>Changed</c>: expanding a node does not make the Silo "modified".</remarks>
public sealed partial class SqliteDatabaseProvider : INodeStateStore
{
    private const string NodeTable = "[main].[Node]";

    public IReadOnlyList<NodeStateRecord> GetNodeStates()
    {
        _gate.Wait();
        try
        {
            return NodeStateRows.Get(_conn, NodeTable, CurrentUserId());
        }
        finally
        {
            _gate.Release();
        }
    }

    public void SaveNodeStates(IReadOnlyCollection<NodeStateRecord> states)
    {
        _gate.Wait();
        try
        {
            long? uid = CurrentUserId();
            using var tx = _conn.BeginTransaction();
            try
            {
                NodeStateRows.Save(_conn, tx, NodeTable, uid, states);
                tx.Commit();
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
}

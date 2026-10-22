using System.Security.Principal;
using Serilog;
using Silo.Providers.Data;

namespace Silo.DbModel.Sqlite;

/// <summary>The remembered window positions and layout (<see cref="IWindowStore" />) of the SQLite provider.</summary>
/// <remarks>
///     None of these raise <c>Changed</c>: arranging windows does not make the Silo "modified" (no star, no save
///     prompt).
/// </remarks>
public sealed partial class SqliteDatabaseProvider : IWindowStore
{
    private const string WindowTable = "[main].[Window]";
    private const string UserTable   = "[main].[User]";

    /// <summary>
    ///     The Silo user this person is, once there is one (see <see cref="IWindowStore" />); the id is looked up each
    ///     time, users can be added meanwhile.
    /// </summary>
    private long? CurrentUserId()
    {
        string name;
        string sid = string.Empty;
        try
        {
            var identity = WindowsIdentity.GetCurrent();
            name = identity.Name;
            sid  = identity.User?.Value ?? string.Empty;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not determine the Windows identity for the window layout");
            name = $"{Environment.UserDomainName}\\{Environment.UserName}";
        }

        return WindowRows.FindUserId(_conn, UserTable, name, sid);
    }

    public WindowRecord? GetWindow(string type, string uri)
    {
        _gate.Wait();
        try
        {
            return WindowRows.Get(_conn, WindowTable, CurrentUserId(), type, uri);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void SaveWindows(IReadOnlyCollection<WindowRecord> windows)
    {
        if (windows.Count == 0) return;

        _gate.Wait();
        try
        {
            long?     uid = CurrentUserId();
            using var tx  = _conn.BeginTransaction();
            try
            {
                WindowRows.Save(_conn, tx, WindowTable, uid, windows);
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

    public int ClearWindows()
    {
        _gate.Wait();
        try
        {
            return WindowRows.Clear(_conn, WindowTable, CurrentUserId());
        }
        finally
        {
            _gate.Release();
        }
    }

    public void ForgetWindow(string type, string uri)
    {
        _gate.Wait();
        try
        {
            WindowRows.Forget(_conn, WindowTable, type, uri);
        }
        finally
        {
            _gate.Release();
        }
    }
}
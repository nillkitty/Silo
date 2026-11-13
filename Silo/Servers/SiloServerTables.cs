using System.Linq;
using Silo.DbModel.Global;
using Silo.Providers.Network;
using Silo.Tabular;

namespace Silo.Servers;

/// <summary>
///     The authored tables of a Silo, for servers. Reads are cached for a moment (a DNS stub asks for its table on every
///     query); writes are made one at a time, and tell the table views that something changed.
/// </summary>
public sealed class SiloServerTables : IServerTables
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(2);

    private readonly Dictionary<string, (ServerTable? Table, DateTime At)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object                                                 _gate  = new();
    private readonly ITableStore                                            _store;

    public SiloServerTables(ITableStore store)
    {
        _store = store;
    }

    public ServerTable? Read(string name)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(name, out var hit) && DateTime.UtcNow - hit.At < CacheFor) return hit.Table;

            var table = Find(name) is { } t ? ToServerTable(t) : null;
            _cache[name] = (table, DateTime.UtcNow);
            return table;
        }
    }

    public void Append(string name, IReadOnlyList<string> columns, IReadOnlyDictionary<string, string> row)
    {
        lock (_gate)
        {
            var table = Find(name) ?? new AuthoredTable { Name = name };
            var sheet = string.IsNullOrEmpty(table.DataJson) ? new SheetData() : table.ReadSheet().TrimTrailingBlankRows();

            foreach (var column in columns.Concat(row.Keys))
                if (!sheet.Columns.Any(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase)))
                    sheet.Columns.Add(new SheetColumn { Name = column, Type = CellType.Text });

            sheet.Normalize();
            var cells = sheet.Columns.Select(c => row.TryGetValue(c.Name, out var v) ? v : string.Empty).ToArray();
            sheet.Rows.Add(cells);
            table.WriteSheet(sheet);
            _store.SaveTable(table);
            _cache.Remove(name);
        }

        TableEditors.RaiseChanged();
    }

    private AuthoredTable? Find(string name)
    {
        return _store.ListTables().FirstOrDefault(t => string.Equals(t.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static ServerTable ToServerTable(AuthoredTable t)
    {
        var sheet = t.ReadSheet().Normalize().TrimTrailingBlankRows();
        return new ServerTable(t.Name, sheet.Columns.Select(c => c.Name).ToList(), sheet.Rows.Select(r => (IReadOnlyList<string>)r).ToList());
    }
}

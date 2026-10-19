using Microsoft.Data.Sqlite;
using Silo.DbModel.Meta;
using Silo.Providers.Definitions;

namespace Silo.DbModel.Sqlite;

/// <summary>
///     The enums (<see cref="IEnumStore" />) and syntaxes (<see cref="ISyntaxStore" />) defined in the Silo, kept in the metaverse database
///     (<c>MvEnum</c>, <c>MvEnumValue</c>, <c>MvSyntax</c>) of the SQLite provider.
/// </summary>
public sealed partial class SqliteDatabaseProvider : IEnumStore, ISyntaxStore
{
    // The physical table names depend on the storage format, so they are asked for each time rather than kept in constants.
    private string EnumTable   => T(EntitySchema.MetaverseAlias, nameof(MvEnum));
    private string ValueTable  => T(EntitySchema.MetaverseAlias, nameof(MvEnumValue));
    private string SyntaxTable => T(EntitySchema.MetaverseAlias, nameof(MvSyntax));

    private TResult ReadLocked<TResult>(Func<TResult> read)
    {
        _gate.Wait();
        try
        {
            return read();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Runs the change in one transaction (rolled back if it throws) and, when it reports a real change, raises <see cref="IDatabaseChanges.Changed" /> afterwards.</summary>
    private TResult WriteLocked<TResult>(Func<SqliteTransaction, (TResult Result, bool Changed)> write)
    {
        TResult result;
        bool    changed;
        _gate.Wait();
        try
        {
            using var tx = _conn.BeginTransaction();
            try
            {
                (result, changed) = write(tx);
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

        if (changed) RaiseChanged();
        return result;
    }

    // ---- enums ---------------------------------------------------------------------------------------------------

    public IReadOnlyList<EnumRecord> GetEnums() => ReadLocked(() => EnumRows.GetAll(_conn, null, EnumTable, ValueTable));

    public EnumRecord? GetEnum(int id) => ReadLocked(() => EnumRows.Get(_conn, null, EnumTable, ValueTable, id));

    public EnumRecord? FindEnum(string name) => ReadLocked(() => EnumRows.Find(_conn, null, EnumTable, ValueTable, name));

    public EnumRecord SaveEnum(EnumRecord definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return WriteLocked(tx => (EnumRows.Save(_conn, tx, EnumTable, ValueTable, definition), true));
    }

    public bool DeleteEnum(int id) => WriteLocked(tx => { bool removed = EnumRows.Delete(_conn, tx, EnumTable, ValueTable, id); return (removed, removed); });

    public EnumValueRecord SaveEnumValue(int enumId, EnumValueRecord value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return WriteLocked(tx => (EnumRows.SaveValue(_conn, tx, EnumTable, ValueTable, enumId, value), true));
    }

    public bool DeleteEnumValue(int valueId) => WriteLocked(tx => { bool removed = EnumRows.DeleteValue(_conn, tx, ValueTable, valueId); return (removed, removed); });

    // ---- syntaxes ------------------------------------------------------------------------------------------------

    public IReadOnlyList<SyntaxRecord> GetSyntaxes() => ReadLocked(() => SyntaxRows.GetAll(_conn, null, SyntaxTable));

    public SyntaxRecord? GetSyntax(int id) => ReadLocked(() => SyntaxRows.Get(_conn, null, SyntaxTable, id));

    public SyntaxRecord? FindSyntax(string nameOrAlias) => ReadLocked(() => SyntaxRows.Find(_conn, null, SyntaxTable, nameOrAlias));

    public SyntaxRecord SaveSyntax(SyntaxRecord syntax)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        return WriteLocked(tx => (SyntaxRows.Save(_conn, tx, SyntaxTable, syntax), true));
    }

    public bool DeleteSyntax(int id) => WriteLocked(tx => { bool removed = SyntaxRows.Delete(_conn, tx, SyntaxTable, id); return (removed, removed); });

    public int RestoreDefaults() => WriteLocked(tx => { int added = SyntaxRows.InsertMissing(_conn, tx, SyntaxTable, SyntaxSeeds.Defaults); return (added, added > 0); });
}

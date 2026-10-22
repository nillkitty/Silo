using Microsoft.Data.Sqlite;
using Silo.DbModel.Meta;
using Silo.DbModel.Sqlite;
using Silo.Providers.Definitions;

namespace Silo.Tests.DbModel;

/// <summary>
///     What <c>EntitySchema.EnsureFixedSchemas</c> does for the enum and syntax tables: creates them, and seeds the
///     predefined syntaxes once.
/// </summary>
public class SchemaSeedTests
{
    private static readonly string           SyntaxTable = SiloLayout.Simple.Table(EntitySchema.MetaverseAlias, nameof(MvSyntax));
    private                 SqliteConnection _conn       = null!;

    [SetUp]
    public void SetUp()
    {
        _conn = new SqliteConnection("Data Source=:memory:"); // one database: the simple layout keeps every table in it
        _conn.Open();
    }

    [TearDown]
    public void TearDown()
    {
        _conn.Dispose();
    }

    private long Count(string table)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM " + table;
        return (long)cmd.ExecuteScalar()!;
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Test]
    public void The_enum_and_syntax_entities_are_registered_in_the_metaverse_database()
    {
        foreach (var type in new[] { typeof(MvEnum), typeof(MvEnumValue), typeof(MvSyntax) })
        {
            var info = EntitySchema.Get(type);
            Assert.That(info,             Is.Not.Null,                             type.Name);
            Assert.That(info!.SchemaKind, Is.EqualTo(SchemaKind.Fixed),            type.Name);
            Assert.That(info.FixedAlias,  Is.EqualTo(EntitySchema.MetaverseAlias), type.Name);
        }
    }

    [Test]
    public void An_enums_values_are_rows_of_their_own_table_not_a_column_of_the_enum()
    {
        var columns = EntitySchema.Get(typeof(MvEnum))!.Columns.Select(c => c.ColumnName).ToList();
        Assert.That(columns, Does.Not.Contain("Items"));
        Assert.That(columns, Does.Contain("Name"));
        Assert.That(columns, Does.Contain("Type"));
        Assert.That(columns, Does.Contain("Flags"));

        var valueColumns = EntitySchema.Get(typeof(MvEnumValue))!.Columns.Select(c => c.ColumnName).ToList();
        Assert.That(valueColumns, Does.Contain("EnumId")); // the owning enum, as a reference
        Assert.That(valueColumns, Does.Contain("Value"));
    }

    [Test]
    public void A_new_silo_gets_the_predefined_syntaxes_and_empty_enum_tables()
    {
        EntitySchema.EnsureFixedSchemas(_conn, SiloLayout.Simple);

        Assert.That(Count(SyntaxTable), Is.EqualTo(SyntaxSeeds.Defaults.Count));
        Assert.That(Count(SiloLayout.Simple.Table(EntitySchema.MetaverseAlias, nameof(MvEnum))), Is.EqualTo(0));
        Assert.That(Count(SiloLayout.Simple.Table(EntitySchema.MetaverseAlias, nameof(MvEnumValue))), Is.EqualTo(0));

        var seeded = SyntaxRows.GetAll(_conn, null, SyntaxTable);
        Assert.That(seeded.Select(s => s.Name),                   Does.Contain("Timestamp"));
        Assert.That(seeded.Select(s => s.Name),                   Does.Contain("SecurityIdentifier"));
        Assert.That(seeded.Single(s => s.Name == "JToken").Alias, Is.EqualTo("jtok"));
    }

    [Test]
    public void Opening_the_silo_again_does_not_add_what_was_deleted_nor_duplicate_what_is_there()
    {
        EntitySchema.EnsureFixedSchemas(_conn, SiloLayout.Simple);
        Exec($"DELETE FROM {SyntaxTable} WHERE [Name] = 'Csv'");

        EntitySchema.EnsureFixedSchemas(_conn, SiloLayout.Simple);
        Assert.That(Count(SyntaxTable), Is.EqualTo(SyntaxSeeds.Defaults.Count - 1)); // the table existed, so nothing is seeded again

        Exec($"DELETE FROM {SyntaxTable}");
        EntitySchema.EnsureFixedSchemas(_conn, SiloLayout.Simple);
        Assert.That(Count(SyntaxTable), Is.EqualTo(0)); // deleting them all sticks too
    }

    [Test]
    public void A_silo_made_before_syntaxes_existed_gets_them_when_it_is_opened()
    {
        EntitySchema.EnsureFixedSchemas(_conn, SiloLayout.Simple);
        Exec($"DROP TABLE {SyntaxTable}"); // what an older build's file looks like

        EntitySchema.EnsureFixedSchemas(_conn, SiloLayout.Simple);
        Assert.That(Count(SyntaxTable), Is.EqualTo(SyntaxSeeds.Defaults.Count));
    }

    [Test]
    public void Seeding_can_be_left_out_when_the_rows_are_about_to_be_copied_in()
    {
        EntitySchema.EnsureFixedSchemas(_conn, SiloLayout.Simple, false);
        Assert.That(Count(SyntaxTable), Is.EqualTo(0));
    }
}
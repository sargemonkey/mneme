using Microsoft.Data.Sqlite;
using Mneme.Storage;

namespace Mneme.Tests;

public sealed class SqliteSchemaTests
{
    [Fact]
    public void Initialize_creates_all_expected_tables()
    {
        using var db = new TestDatabase();
        using var c = db.Factory.Open();

        var tables = QueryTableNames(c);
        Assert.Contains("memory_events", tables);
        Assert.Contains("memory_artifacts", tables);
        Assert.Contains("memory_edges", tables);
        Assert.Contains("distillation_queue", tables);
        Assert.Contains("schema_meta", tables);
    }

    [Fact]
    public void Initialize_is_idempotent()
    {
        using var db = new TestDatabase();
        using var c = db.Factory.Open();
        SqliteSchema.Initialize(c);
        SqliteSchema.Initialize(c);
        var v = Scalar(c, "SELECT value FROM schema_meta WHERE key='version';");
        Assert.Equal(SqliteSchema.Version.ToString(), v);
    }

    [Fact]
    public void Foreign_keys_are_enabled_on_every_connection()
    {
        using var db = new TestDatabase();
        using var c = db.Factory.Open();
        var v = Scalar(c, "PRAGMA foreign_keys;");
        Assert.Equal("1", v);
    }

    [Fact]
    public void Expected_indexes_exist()
    {
        using var db = new TestDatabase();
        using var c = db.Factory.Open();
        var indexes = QueryIndexNames(c);
        Assert.Contains("idx_memory_events_workstream", indexes);
        Assert.Contains("idx_memory_events_workstream_channel", indexes);
        Assert.Contains("idx_memory_events_category", indexes);
        Assert.Contains("idx_memory_events_valid_at", indexes);
        Assert.Contains("idx_memory_edges_workstream", indexes);
        Assert.Contains("idx_distillation_queue_workstream", indexes);
    }

    private static List<string> QueryTableNames(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
        var result = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(r.GetString(0));
        return result;
    }

    private static List<string> QueryIndexNames(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' ORDER BY name;";
        var result = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(r.GetString(0));
        return result;
    }

    private static string? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v?.ToString();
    }
}

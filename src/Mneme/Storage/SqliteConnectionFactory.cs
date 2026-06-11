using Microsoft.Data.Sqlite;

namespace Mneme.Storage;

/// <summary>
/// Builds and configures <see cref="SqliteConnection"/> instances against a
/// single physical database file (or in-memory database for tests). Every
/// connection opened through the factory has the same set of pragmas applied
/// before any user statement runs: WAL journal, foreign keys on, normal
/// synchronous mode, a 5-second busy timeout, and UTF-8 encoding.
/// </summary>
/// <remarks>
/// <para>
/// WAL is non-negotiable per <c>AGENTS.md</c> — it is what lets the sync
/// ingest stage return after a single fsync in &lt; 50ms while readers
/// continue uninterrupted.
/// </para>
/// <para>
/// In-memory databases (<see cref="ForSharedMemory"/>) use the
/// <c>file::memory:?cache=shared</c> URI so multiple connections in the same
/// process see the same database — required for tests that spin up multiple
/// connections against one schema.
/// </para>
/// </remarks>
public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;
    private readonly bool _isMemory;

    /// <summary>
    /// Build a factory bound to a SQLite file on disk. The containing
    /// directory must already exist.
    /// </summary>
    public SqliteConnectionFactory(string databasePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true,
        };
        _connectionString = builder.ConnectionString;
        _isMemory = false;
    }

    private SqliteConnectionFactory(string connectionString, bool isMemory)
    {
        _connectionString = connectionString;
        _isMemory = isMemory;
    }

    /// <summary>
    /// Build a factory bound to a process-private in-memory database that
    /// can be shared between connections. Useful for tests. The factory
    /// holds an internal "keep-alive" connection so the database is not
    /// torn down between transient connections; dispose the factory to
    /// release it.
    /// </summary>
    public static SqliteConnectionFactory ForSharedMemory(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = $"file:{name}?mode=memory&cache=shared",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
        };
        return new SqliteConnectionFactory(builder.ConnectionString, true);
    }

    /// <summary>
    /// Open a new connection and apply Mneme's standard pragmas before
    /// returning it. Callers are responsible for disposing the connection.
    /// </summary>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        // journal_mode and encoding are not applied to in-memory dbs.
        cmd.CommandText = _isMemory
            ? "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;"
            : """
              PRAGMA journal_mode = WAL;
              PRAGMA synchronous = NORMAL;
              PRAGMA foreign_keys = ON;
              PRAGMA busy_timeout = 5000;
              PRAGMA encoding = 'UTF-8';
              """;
        cmd.ExecuteNonQuery();
        return connection;
    }
}

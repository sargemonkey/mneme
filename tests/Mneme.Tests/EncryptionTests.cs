using System.Text;
using Microsoft.Data.Sqlite;
using Mneme.Ingest;
using Mneme.Storage;

namespace Mneme.Tests;

/// <summary>
/// Covers SQLCipher at-rest encryption via <see cref="SqliteConnectionFactory"/>'s
/// optional key: the database file is ciphertext on disk, unreadable without the key,
/// and round-trips with it. No key = plain SQLite (unchanged behavior).
/// </summary>
public sealed class EncryptionTests : IDisposable
{
    private readonly string _tmpDir;

    public EncryptionTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "mneme-enc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task Encrypted_database_is_ciphertext_on_disk_and_needs_the_key()
    {
        var path = Path.Combine(_tmpDir, "enc.db");
        const string key = "correct horse battery staple";
        const string secret = "SECRET-CONTENT-a1b2c3-do-not-leak";

        // Write one event through an encrypted factory.
        {
            var factory = new SqliteConnectionFactory(path, key);
            using (var boot = factory.Open()) SqliteSchema.Initialize(boot);
            var agent = new MemoryAgent(factory);
            await agent.IngestAsync(TestFixtures.NewEvidence(eventId: "enc-evt-1", content: secret));
        }
        SqliteConnection.ClearAllPools();

        // 1. On-disk bytes must NOT contain the plaintext payload.
        var bytes = File.ReadAllBytes(path);
        var asLatin1 = Encoding.Latin1.GetString(bytes);
        Assert.DoesNotContain(secret, asLatin1);
        // SQLCipher scrambles the header too — no "SQLite format 3" magic.
        Assert.DoesNotContain("SQLite format 3", asLatin1);

        // 2. Opening without the key fails.
        Assert.ThrowsAny<SqliteException>(() =>
        {
            var plain = new SqliteConnectionFactory(path);
            using var c = plain.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM memory_events;";
            cmd.ExecuteScalar();
        });
        SqliteConnection.ClearAllPools();

        // 3. Opening with the wrong key fails.
        Assert.ThrowsAny<SqliteException>(() =>
        {
            var wrong = new SqliteConnectionFactory(path, "wrong key");
            using var c = wrong.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM memory_events;";
            cmd.ExecuteScalar();
        });
        SqliteConnection.ClearAllPools();

        // 4. Opening with the right key reads the row back.
        {
            var factory = new SqliteConnectionFactory(path, key);
            using var c = factory.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM memory_events WHERE event_id = 'enc-evt-1';";
            Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        }
    }

    [Fact]
    public async Task No_key_is_plain_sqlite_readable_without_a_key()
    {
        var path = Path.Combine(_tmpDir, "plain.db");
        {
            var factory = new SqliteConnectionFactory(path);
            using (var boot = factory.Open()) SqliteSchema.Initialize(boot);
            var agent = new MemoryAgent(factory);
            await agent.IngestAsync(TestFixtures.NewEvidence(eventId: "plain-evt-1"));
        }
        SqliteConnection.ClearAllPools();

        var bytes = File.ReadAllBytes(path);
        Assert.Contains("SQLite format 3", Encoding.Latin1.GetString(bytes)); // plain header

        var reopen = new SqliteConnectionFactory(path);
        using var c = reopen.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM memory_events;";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }
}

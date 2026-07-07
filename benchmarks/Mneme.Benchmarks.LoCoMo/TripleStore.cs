using Microsoft.Data.Sqlite;

namespace Mneme.Benchmarks.LoCoMo;

/// <summary>
/// Sidecar store for prototype knowledge triples, kept in the same SQLite file
/// as the conversation's Mneme workstream so <c>--reuse-db</c> caches extraction
/// across runs. Retrieval is subject-scoped: given the entities named in a
/// question, return only triples whose subject matches — the structural fix for
/// the adversarial attribution failure (distractor facts about other people are
/// excluded by construction rather than out-ranked).
/// </summary>
public sealed class TripleStore
{
    private readonly string _dbPath;

    public TripleStore(string dbPath) => _dbPath = dbPath;

    private SqliteConnection Open()
    {
        var c = new SqliteConnection($"Data Source={_dbPath}");
        c.Open();
        return c;
    }

    public void EnsureSchema()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS fact_triples (
                subject_key  TEXT NOT NULL,
                subject_text TEXT NOT NULL,
                predicate    TEXT NOT NULL,
                object       TEXT NOT NULL,
                valid_at     TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_fact_triples_subject ON fact_triples(subject_key);
            """;
        cmd.ExecuteNonQuery();
    }

    public int Count()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM fact_triples;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void Insert(IReadOnlyList<TripleRow> rows)
    {
        if (rows.Count == 0) return;
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO fact_triples(subject_key, subject_text, predicate, object, valid_at)
            VALUES ($k, $s, $p, $o, $v);
            """;
        var pk = cmd.CreateParameter(); pk.ParameterName = "$k"; cmd.Parameters.Add(pk);
        var ps = cmd.CreateParameter(); ps.ParameterName = "$s"; cmd.Parameters.Add(ps);
        var pp = cmd.CreateParameter(); pp.ParameterName = "$p"; cmd.Parameters.Add(pp);
        var po = cmd.CreateParameter(); po.ParameterName = "$o"; cmd.Parameters.Add(po);
        var pv = cmd.CreateParameter(); pv.ParameterName = "$v"; cmd.Parameters.Add(pv);
        foreach (var r in rows)
        {
            pk.Value = r.SubjectKey; ps.Value = r.SubjectText; pp.Value = r.Predicate;
            po.Value = r.Object; pv.Value = r.At.ToString("O");
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>
    /// All triples whose normalized subject key contains (or is contained by) any
    /// of the query subject keys — e.g. query "Melanie" matches subjects
    /// "melanie" and "melanie grandma", scoping to that person's sub-graph.
    /// Returned as dated context lines "[date] subject predicate object".
    /// </summary>
    public IReadOnlyList<string> SubjectScoped(IReadOnlyCollection<string> subjectKeys, int limit)
    {
        if (subjectKeys.Count == 0) return Array.Empty<string>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT subject_key, subject_text, predicate, object, valid_at FROM fact_triples;";
        var keys = subjectKeys.Select(k => k.ToLowerInvariant()).ToArray();
        var hits = new List<string>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            var sk = rd.GetString(0);
            var match = keys.Any(k => sk.Contains(k, StringComparison.Ordinal) || k.Contains(sk, StringComparison.Ordinal));
            if (!match) continue;
            var at = DateTimeOffset.TryParse(rd.GetString(4), out var d) ? d.ToString("yyyy-MM-dd") : "";
            hits.Add($"[{at}] {rd.GetString(1)} {rd.GetString(2).Replace('_', ' ')} {rd.GetString(3)}");
        }
        return hits.Take(limit).ToList();
    }
}

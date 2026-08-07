using Mneme.Contracts;
using Mneme.Revocation;
using Mneme.Storage;
using Mneme.Studio.Agent.Acp;
using EventId = Mneme.Contracts.EventId;

namespace Mneme.Studio.Agent.Memory;

/// <summary>
/// Orchestrates the whole loop the app exists to demonstrate:
/// <list type="number">
///   <item>Take a user prompt, append it to the host-owned context buffer.</item>
///   <item>Drive the ACP agent, stream back its reply, append that too.</item>
///   <item>Hand the accumulated slice to
///         <see cref="IMemoryAgent.DistillSessionAsync"/> so Mneme distills the
///         conversation into epistemic memory (Mneme never stores the raw
///         turns — only the interpretation + a session-range citation).</item>
///   <item>Surface the distilled memory back to the UI via the query API.</item>
/// </list>
/// The app is the <em>host</em> in Mneme's "host owns the chat log; Mneme owns
/// the interpretation" model, and simultaneously the <em>client</em> in ACP's
/// "client owns the conversation; agent does the work" model — the same split,
/// which is why the two libraries compose so cleanly.
/// </summary>
internal sealed class AgentChatService : IAsyncDisposable
{
    private readonly IMemoryAgent _memory;
    private readonly IMemoryQueryAPI _query;
    private readonly IRevocationService _revocation;
    private readonly SqliteConnectionFactory _connections;
    private readonly CapabilityToken _token;
    private readonly IChatCompletion? _distillerLlm;
    private readonly AcpAgentConnection _conn = new();
    private readonly List<ChatTurn> _transcript = new();
    private readonly List<ContextEntry> _buffer = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _agentGate = new(1, 1);

    private bool _started;
    private bool _agentStarted;
    private int _seq;

    public AgentChatService(
        IMemoryAgent memory,
        IMemoryQueryAPI query,
        IRevocationService revocation,
        SqliteConnectionFactory connections,
        CapabilityToken token,
        IEnumerable<IChatCompletion> distillerLlms)
    {
        _memory = memory;
        _query = query;
        _revocation = revocation;
        _connections = connections;
        _token = token;
        _distillerLlm = distillerLlms.FirstOrDefault();
    }

    public WorkstreamId Workstream => _token.Workstream ?? new WorkstreamId("studio-agent");
    public SessionId Session { get; private set; } = new($"acp-{Guid.NewGuid():n}");
    public IReadOnlyList<ChatTurn> Transcript => _transcript;
    public string AgentName => _conn.AgentName;
    public string LastWatermark { get; private set; } = "<none>";
    public int TotalDistilled { get; private set; }
    public int TotalDropped { get; private set; }

    /// <summary>Human-readable label for the active distiller (LLM vs offline heuristic).</summary>
    public string DistillerLabel => _distillerLlm is not null ? $"LLM · {_distillerLlm.Id}" : "heuristic (offline)";

    /// <summary>Whether the live conversation agent is real Copilot (vs the mock).</summary>
    public bool UsingCopilot { get; private set; }

    /// <summary>
    /// Prepare for distillation: warm up the distiller LLM (Copilot) in the
    /// background so the first corpus/chat turn doesn't pay the cold-start cost.
    /// The conversation agent is started lazily on the first interactive
    /// <see cref="SendAsync"/> — corpus replay doesn't need it.
    /// </summary>
    public Task EnsureStartedAsync(CancellationToken ct = default)
    {
        if (_started) return Task.CompletedTask;
        _started = true;
        if (_distillerLlm is not null)
        {
            _ = Task.Run(() => _distillerLlm.WarmUpAsync(CancellationToken.None), CancellationToken.None);
        }
        return Task.CompletedTask;
    }

    private async Task EnsureConversationAgentAsync(CancellationToken ct)
    {
        if (_agentStarted) return;
        await _agentGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_agentStarted) return;
            var cwd = Environment.CurrentDirectory;
            var forceMock = string.Equals(
                Environment.GetEnvironmentVariable("MNEME_AGENT"), "mock", StringComparison.OrdinalIgnoreCase);
            if (!forceMock)
            {
                try
                {
                    await _conn.StartCopilotAsync(cwd, ct).ConfigureAwait(false);
                    UsingCopilot = true;
                }
                catch
                {
                    await _conn.StartMockAsync(cwd, ct).ConfigureAwait(false);
                    UsingCopilot = false;
                }
            }
            else
            {
                await _conn.StartMockAsync(cwd, ct).ConfigureAwait(false);
                UsingCopilot = false;
            }
            _agentStarted = true;
        }
        finally
        {
            _agentGate.Release();
        }
    }

    /// <summary>Send one turn end-to-end and distill the new slice.</summary>
    public async Task<SendResult> SendAsync(string userText, CancellationToken ct = default)
    {
        await EnsureStartedAsync(ct).ConfigureAwait(false);
        await EnsureConversationAgentAsync(ct).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        AddTurn("user", userText, now, ContextEntryKind.UserMessage);

        var reply = await _conn.PromptAsync(userText, ct).ConfigureAwait(false);
        AddTurn("agent", reply, DateTimeOffset.UtcNow, ContextEntryKind.AssistantMessage);

        // Distill everything so far. The SDK filters out entries at/below the
        // persisted watermark, so re-passing the full buffer is safe (and the
        // second call for an unchanged range is an idempotent no-op).
        var result = await _memory.DistillSessionAsync(Session, _buffer, _token, ct).ConfigureAwait(false);

        var newlyDistilled = result.NewEvents.Count;
        var droppedCount = result.Dropped?.Count ?? 0;
        TotalDistilled += newlyDistilled;
        TotalDropped += droppedCount;
        LastWatermark = result.NewWatermark.LastDistilledEntryId;

        return new SendResult(reply, newlyDistilled, droppedCount, result.WasNoOp);
    }

    /// <summary>The distilled memory for this workstream, newest first.</summary>
    public async Task<IReadOnlyList<QueryResultItem>> GetMemoryAsync(int limit = 50, CancellationToken ct = default)
        => await _query.ListRecentAsync(Workstream, limit, _token, ct).ConfigureAwait(false);

    /// <summary>
    /// Feed one raw corpus turn (e.g. a LoCoMo conversation turn) into the
    /// host-owned buffer and distill it. Unlike <see cref="SendAsync"/> this
    /// does NOT drive the ACP agent — corpus turns are already a conversation
    /// (human ↔ human), so we replay them straight into Mneme's distillation
    /// pipeline. The <paramref name="speaker"/> rides along as entry metadata
    /// so the distiller attributes each memory to the right person.
    /// </summary>
    public async Task<SendResult> FeedEntryAsync(
        string speaker, string text, DateTimeOffset at, CancellationToken ct = default)
    {
        _transcript.Add(new ChatTurn(speaker, text, at));
        _buffer.Add(new ContextEntry(
            EntryId: _seq++.ToString("D6"),
            Timestamp: at,
            Kind: ContextEntryKind.UserMessage,
            Text: text,
            SourceRef: $"corpus#{speaker}",
            Metadata: new Dictionary<string, string> { ["speaker"] = speaker }));

        var result = await _memory.DistillSessionAsync(Session, _buffer, _token, ct).ConfigureAwait(false);

        var newly = result.NewEvents.Count;
        var droppedCount = result.Dropped?.Count ?? 0;
        TotalDistilled += newly;
        TotalDropped += droppedCount;
        LastWatermark = result.NewWatermark.LastDistilledEntryId;
        return new SendResult(string.Empty, newly, droppedCount, result.WasNoOp);
    }

    /// <summary>
    /// Reject a captured memory. Mneme is append-only, so this doesn't delete
    /// the event — it records a revocation tombstone (who/when/why) and zeroes
    /// any artifact body. The query API then filters the event out, so it
    /// disappears from the memory panel while the audit trail is preserved.
    /// </summary>
    public async Task RejectMemoryAsync(EventId eventId, CancellationToken ct = default)
        => await _revocation.RevokeAsync(
            eventId, Workstream, new PrincipalId(Environment.UserName),
            reason: "rejected by user in Studio", ct).ConfigureAwait(false);

    /// <summary>
    /// "Sleep": run the read-side distiller over everything captured so far and
    /// return the condensed <see cref="ContextBundle"/> — a one-paragraph
    /// orientation plus per-category sections. This is the compressed synthesis
    /// a consuming agent would load instead of the raw event dump. Works offline
    /// (heuristic bundle) when no LLM distiller is wired.
    /// </summary>
    public async Task<ContextBundle> SleepAsync(int? tokenBudget = null, CancellationToken ct = default)
        => await _query.DistillAsync(
            Workstream, new DistillOptions(ForceRefresh: true, TokenBudget: tokenBudget), _token, ct)
            .ConfigureAwait(false);

    /// <summary>
    /// "Clear": start completely fresh. Resets the host-owned conversation
    /// (transcript + context buffer + counters), starts a new session, and wipes
    /// the workstream's distilled memory so the panels are empty again.
    /// <para>
    /// This is a <em>demo maintenance</em> operation. Mneme's log is append-only,
    /// so a production consumer would either revoke individual events
    /// (<see cref="IRevocationService"/>) or switch to a fresh workstream rather
    /// than truncate the store; here we truncate the app's own throwaway database
    /// to give the demo a genuine clean slate on one click.
    /// </para>
    /// </summary>
    public Task ClearAsync(CancellationToken ct = default)
    {
        _transcript.Clear();
        _buffer.Clear();
        _seq = 0;
        TotalDistilled = 0;
        TotalDropped = 0;
        LastWatermark = "<none>";
        Session = new SessionId($"acp-{Guid.NewGuid():n}");
        WipeStore();
        return Task.CompletedTask;
    }

    // Truncate every data table in the app's throwaway SQLite database while
    // preserving the schema (and its version row). The FTS5 index is cleared via
    // its own virtual table; its shadow tables are left alone. FK enforcement is
    // disabled for the wipe so table order doesn't matter.
    private void WipeStore()
    {
        using var c = _connections.Open();
        using (var off = c.CreateCommand()) { off.CommandText = "PRAGMA foreign_keys = OFF;"; off.ExecuteNonQuery(); }

        var tables = new List<string>();
        using (var q = c.CreateCommand())
        {
            q.CommandText =
                "SELECT name FROM sqlite_master WHERE type='table' " +
                "AND name NOT LIKE 'sqlite_%' AND name <> 'schema_meta' " +
                "AND name NOT LIKE 'event_text_index%' " +          // exclude FTS virtual + shadow tables
                "AND (sql IS NULL OR sql NOT LIKE 'CREATE VIRTUAL%');";
            using var r = q.ExecuteReader();
            while (r.Read()) tables.Add(r.GetString(0));
        }

        using var tx = c.BeginTransaction();
        using (var d = c.CreateCommand()) { d.Transaction = tx; d.CommandText = "DELETE FROM event_text_index;"; d.ExecuteNonQuery(); }
        foreach (var t in tables)
        {
            using var d = c.CreateCommand();
            d.Transaction = tx;
            d.CommandText = $"DELETE FROM \"{t}\";";
            d.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private void AddTurn(string role, string text, DateTimeOffset at, ContextEntryKind kind)
    {
        _transcript.Add(new ChatTurn(role, text, at));
        _buffer.Add(new ContextEntry(
            EntryId: _seq++.ToString("D6"),
            Timestamp: at,
            Kind: kind,
            Text: text,
            SourceRef: $"acp#{role}"));
    }

    public async ValueTask DisposeAsync()
    {
        await _conn.DisposeAsync().ConfigureAwait(false);
        _startGate.Dispose();
    }
}

/// <summary>One visible turn in the conversation transcript.</summary>
internal sealed record ChatTurn(string Role, string Text, DateTimeOffset At);

/// <summary>Outcome of a single <see cref="AgentChatService.SendAsync"/> call.</summary>
internal sealed record SendResult(string AgentReply, int NewlyDistilled, int Dropped, bool WasNoOp);

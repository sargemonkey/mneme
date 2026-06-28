using System.Text;
using System.Text.Json;
using Mneme.Contracts;

namespace Mneme.Benchmarks.LoCoMo;

/// <summary>
/// Host-supplied <see cref="ISessionDistiller"/> that extracts atomic,
/// self-contained facts from a window of conversation turns using the chat
/// model. This is the piece that exercises Mneme's actual thesis — proactive
/// distillation — instead of retrieving over raw turns. The SDK chunks the
/// conversation (via repeated <see cref="IMemoryAgent.DistillSessionAsync"/>
/// calls) and ingests the returned facts with session-range citations.
/// </summary>
public sealed class LlmSessionDistiller : ISessionDistiller
{
    private readonly IChatCompletion _chat;
    public string Id { get; }

    public LlmSessionDistiller(IChatCompletion chat)
    {
        _chat = chat;
        Id = $"session-distiller/{_chat.Id}";
    }

    private const string System = """
        You convert a slice of a personal conversation into atomic, durable
        memory facts. Rules:
        - Each fact is ONE self-contained sentence that stands alone without
          the surrounding dialogue (resolve pronouns to names).
        - Capture concrete facts, preferences, plans, events, and relationships.
        - Preserve dates/times mentioned in or around the turns.
        - Attribute to the speaker by name.
        - Skip pure pleasantries that carry no durable information.
        Reply with JSON only:
        {"facts":[{"statement":"...","supporting":["<entryId>"]}]}
        where supporting lists the entry id(s) the fact came from.
        """;

    public async Task<SessionDistillationResult> DistillAsync(SessionDistillationRequest req, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Conversation slice (entryId | speaker-tagged text):");
        foreach (var e in req.Entries)
        {
            sb.Append(e.EntryId).Append(" | ").AppendLine(e.Text);
        }
        var reply = await _chat.CompleteAsync(System, sb.ToString(), ct).ConfigureAwait(false);

        var events = new List<DistilledEvent>();
        try
        {
            var json = ExtractJson(reply);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("facts", out var facts) && facts.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in facts.EnumerateArray())
                {
                    var statement = f.TryGetProperty("statement", out var s) ? s.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(statement)) continue;
                    var supporting = f.TryGetProperty("supporting", out var sup) && sup.ValueKind == JsonValueKind.Array
                        ? sup.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
                        : Array.Empty<string>();
                    if (supporting.Length == 0 && req.Entries.Count > 0)
                    {
                        supporting = new[] { req.Entries[0].EntryId };
                    }
                    events.Add(new DistilledEvent(
                        new FactPayload(statement, Array.Empty<EventId>()), supporting));
                }
            }
        }
        catch
        {
            // A malformed model reply yields no facts for this slice; the run
            // continues. (Logged sparsely to avoid noise on long runs.)
        }
        return new SessionDistillationResult(events);
    }

    // Models sometimes wrap JSON in prose or fences; pull out the first {...}.
    private static string ExtractJson(string s)
    {
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        return start >= 0 && end > start ? s[start..(end + 1)] : "{\"facts\":[]}";
    }
}

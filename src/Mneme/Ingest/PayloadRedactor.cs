using Mneme.Contracts;
using Mneme.Ingest.Redaction;

namespace Mneme.Ingest;

/// <summary>
/// Walks the free-text fields of every concrete <see cref="EventPayload"/>
/// variant, runs them through an <see cref="IRedactor"/>, and returns a
/// new payload record with the redacted text in place. Returns the input
/// unchanged when the redactor finds nothing.
/// </summary>
/// <remarks>
/// Records are immutable, so this is a pure function: it produces a new
/// payload value rather than mutating. The set of redacted fields is the
/// exhaustive list of free-text fields across the seven payload types.
/// </remarks>
internal static class PayloadRedactor
{
    public static (EventPayload Payload, bool HadHits, int HitCount) Redact(
        EventPayload payload, IRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(redactor);

        switch (payload)
        {
            case EvidencePayload e:
            {
                var r = redactor.Redact(e.Content);
                return (e with { Content = r.RedactedContent }, r.HadHits, r.Hits.Count);
            }
            case FactPayload f:
            {
                var r = redactor.Redact(f.Statement);
                var triples = f.Triples;
                var tripleHits = 0;
                if (f.Triples is { Count: > 0 })
                {
                    var redacted = new List<FactTriple>(f.Triples.Count);
                    foreach (var t in f.Triples)
                    {
                        var rs = redactor.Redact(t.Subject);
                        var ro = redactor.Redact(t.Object);
                        tripleHits += rs.Hits.Count + ro.Hits.Count;
                        redacted.Add(t with { Subject = rs.RedactedContent, Object = ro.RedactedContent });
                    }
                    triples = redacted;
                }
                return (f with { Statement = r.RedactedContent, Triples = triples },
                        r.HadHits || tripleHits > 0, r.Hits.Count + tripleHits);
            }
            case DecisionPayload d:
            {
                var r1 = redactor.Redact(d.Statement);
                var r2 = redactor.Redact(d.Rationale);
                var had = r1.HadHits || r2.HadHits;
                return (d with { Statement = r1.RedactedContent, Rationale = r2.RedactedContent },
                        had, r1.Hits.Count + r2.Hits.Count);
            }
            case HypothesisPayload h:
            {
                var r = redactor.Redact(h.Statement);
                return (h with { Statement = r.RedactedContent }, r.HadHits, r.Hits.Count);
            }
            case GoalPayload g:
            {
                var r = redactor.Redact(g.Statement);
                return (g with { Statement = r.RedactedContent }, r.HadHits, r.Hits.Count);
            }
            case ActionPayload a:
            {
                var r1 = redactor.Redact(a.Statement);
                var totalHits = r1.Hits.Count;
                var had = r1.HadHits;
                string? extRef = a.ExternalReference;
                if (extRef is not null)
                {
                    var r2 = redactor.Redact(extRef);
                    extRef = r2.RedactedContent;
                    had |= r2.HadHits;
                    totalHits += r2.Hits.Count;
                }
                return (a with { Statement = r1.RedactedContent, ExternalReference = extRef },
                        had, totalHits);
            }
            case OutcomePayload o:
            {
                var r = redactor.Redact(o.Statement);
                return (o with { Statement = r.RedactedContent }, r.HadHits, r.Hits.Count);
            }
            case SkillPayload s:
            {
                var rn = redactor.Redact(s.Name);
                var rp = redactor.Redact(s.Procedure);
                var had = rn.HadHits || rp.HadHits;
                var hits = rn.Hits.Count + rp.Hits.Count;
                string? trigger = s.Trigger;
                if (trigger is not null)
                {
                    var rt = redactor.Redact(trigger);
                    trigger = rt.RedactedContent;
                    had |= rt.HadHits;
                    hits += rt.Hits.Count;
                }
                return (s with { Name = rn.RedactedContent, Procedure = rp.RedactedContent, Trigger = trigger },
                        had, hits);
            }
            default:
                throw new NotSupportedException(
                    $"Unknown payload type {payload.GetType().FullName}. Add a case to PayloadRedactor.");
        }
    }
}

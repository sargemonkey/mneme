namespace Mneme.Contracts;

/// <summary>
/// A structured, subject-attributed assertion extracted alongside a
/// <see cref="FactPayload"/>'s natural-language statement: <c>subject</c>
/// (the entity the fact is about), <c>predicate</c> (the relation), and
/// <c>object</c> (the value).
/// </summary>
/// <remarks>
/// <para>
/// Triples make the <em>subject</em> of a fact explicit so retrieval can scope
/// to "facts about person X" instead of "facts whose text mentions X" — the
/// latter is useless in multi-party conversations where pronoun resolution
/// names every participant in nearly every statement. The full
/// <see cref="FactPayload.Statement"/> remains the primary evidence; triples
/// are an attribution index over it, not a replacement (a terse triple loses
/// detail the answer step needs — see the LoCoMo benchmark analysis).
/// </para>
/// <para>
/// The <see cref="Subject"/> here is the raw surface form the distiller emitted
/// (e.g. "Melanie", "Melanie's grandma"). The Mneme storage layer resolves it to
/// a canonical entity id via the entity resolver when projecting; the contract
/// carries only the surface form so it stays free of any resolution concern.
/// </para>
/// </remarks>
/// <param name="Subject">The entity the fact is about, as a surface name or possessive chain.</param>
/// <param name="Predicate">The relation, typically a short snake_case verb/attribute (e.g. <c>lives_in</c>).</param>
/// <param name="Object">The value or target of the relation.</param>
public sealed record FactTriple(
    string Subject,
    string Predicate,
    string Object);

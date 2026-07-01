using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Mneme.Contracts;

namespace Mneme.Benchmarks.LoCoMo;

/// <summary>
/// A <em>true</em> cross-encoder reranker: a local ONNX BERT model
/// (ms-marco-MiniLM-L-6-v2) that scores each (query, candidate) pair jointly.
/// Fully offline — no network, no API key — and a drop-in for the same
/// <see cref="IReranker"/> seam the LLM-listwise reranker uses. This is the
/// "host brings a local model" routing: Mneme ships only the interface.
/// </summary>
/// <remarks>
/// Unlike the bi-encoder retrieval (query and doc embedded independently), the
/// cross-encoder feeds <c>[CLS] query [SEP] candidate [SEP]</c> through the
/// transformer together and reads a single relevance logit — markedly more
/// precise at the head of the list, which is what buried-single-fact
/// (adversarial) questions need.
/// </remarks>
public sealed class OnnxCrossEncoderReranker : IReranker, IDisposable
{
    private const int Cls = 101, Sep = 102, Pad = 0, MaxLen = 256;

    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly object _lock = new(); // ORT session single-threaded use here
    public string Id { get; }

    public OnnxCrossEncoderReranker(string modelPath, string vocabPath)
    {
        _session = new InferenceSession(modelPath);
        _tokenizer = BertTokenizer.Create(vocabPath);
        Id = $"cross-encoder/onnx/{Path.GetFileNameWithoutExtension(modelPath)}";
    }

    public Task<IReadOnlyList<RerankResult>> RerankAsync(
        string query, IReadOnlyList<RerankCandidate> candidates, int topK, CancellationToken ct = default)
    {
        if (candidates.Count == 0) return Task.FromResult<IReadOnlyList<RerankResult>>(Array.Empty<RerankResult>());

        var qIds = Strip(_tokenizer.EncodeToIds(query));
        var scored = new List<(EventId Id, double Score)>(candidates.Count);
        lock (_lock)
        {
            foreach (var cand in candidates)
            {
                ct.ThrowIfCancellationRequested();
                scored.Add((cand.EventId, Score(qIds, Strip(_tokenizer.EncodeToIds(cand.Text)))));
            }
        }
        var ranked = scored.OrderByDescending(s => s.Score).Take(topK)
            .Select(s => new RerankResult(s.Id, s.Score)).ToArray();
        return Task.FromResult<IReadOnlyList<RerankResult>>(ranked);
    }

    private double Score(IReadOnlyList<int> qIds, IReadOnlyList<int> pIds)
    {
        // [CLS] q [SEP] p [SEP], truncating the passage to fit MaxLen.
        var budget = MaxLen - qIds.Count - 3;
        if (budget < 1) budget = 1;
        var p = pIds.Count > budget ? pIds.Take(budget).ToList() : pIds;

        var len = qIds.Count + p.Count + 3;
        var inputIds = new long[len];
        var typeIds = new long[len];
        var mask = new long[len];
        var k = 0;
        inputIds[k] = Cls; typeIds[k] = 0; mask[k] = 1; k++;
        foreach (var id in qIds) { inputIds[k] = id; typeIds[k] = 0; mask[k] = 1; k++; }
        inputIds[k] = Sep; typeIds[k] = 0; mask[k] = 1; k++;
        foreach (var id in p) { inputIds[k] = id; typeIds[k] = 1; mask[k] = 1; k++; }
        inputIds[k] = Sep; typeIds[k] = 1; mask[k] = 1; k++;

        var shape = new[] { 1, len };
        using var results = _session.Run(new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds, shape)),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, shape)),
            NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(typeIds, shape)),
        });
        return results.First().AsEnumerable<float>().First(); // single relevance logit
    }

    // EncodeToIds wraps with [CLS]…[SEP]; strip them so we can build the pair.
    private static List<int> Strip(IReadOnlyList<int> ids)
    {
        var list = ids.ToList();
        if (list.Count > 0 && list[0] == Cls) list.RemoveAt(0);
        if (list.Count > 0 && list[^1] == Sep) list.RemoveAt(list.Count - 1);
        return list;
    }

    public void Dispose() => _session.Dispose();
}

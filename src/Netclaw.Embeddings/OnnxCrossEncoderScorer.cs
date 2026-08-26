// -----------------------------------------------------------------------
// <copyright file="OnnxCrossEncoderScorer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Netclaw.Actors.Memory;

namespace Netclaw.Embeddings;

/// <summary>
/// In-process ONNX-backed <see cref="IRelevanceScorer"/> (memory-relevance-gate D1). Owns
/// exactly one <see cref="InferenceSession"/> and one <see cref="FastBertTokenizer.BertTokenizer"/>
/// for its lifetime, mirroring <see cref="OnnxMemoryEmbedder"/>'s exact lifecycle shape — a
/// second, independently lifecycled session rather than an extension of the embedder's, because
/// the allowlisted relevance model (<c>Xenova/ms-marco-MiniLM-L-6-v2</c>) is a materially
/// different graph (<c>BertForSequenceClassification</c> pair-input head, not the bi-encoder's
/// single-input pooling graph) with its own tokenizer vocabulary (design D1's "alternative
/// considered").
///
/// <para>
/// <b>Pair encoding:</b> <see cref="FastBertTokenizer.BertTokenizer"/> has no built-in support
/// for two-segment (query, candidate) encoding with distinct <c>token_type_ids</c> — its
/// <c>Encode</c> overloads always wrap a single input in <c>[CLS] ... [SEP]</c> with
/// <c>token_type_ids</c> fixed at all-zero (see its XML docs: "Some models which can take
/// multiple sequences as input might need this but this is currently not supported by
/// FastBertTokenizer"). <see cref="EncodePair"/> assembles the pair manually: it encodes the
/// query and candidate independently (each already wrapped in its own <c>[CLS] ... [SEP]</c>),
/// strips each segment's <c>[CLS]</c>/trailing <c>[SEP]</c> via the attention-mask sum (exactly
/// like <see cref="OnnxMemoryEmbedder.EmbedOne"/> reads its own actual-vs-padded length), then
/// splices <c>[CLS] query [SEP] candidate [SEP]</c> back together with the correct
/// <c>token_type_ids</c> (0 for <c>[CLS]</c>+query+first <c>[SEP]</c>, 1 for candidate+final
/// <c>[SEP]</c> — verified against the real model's <c>tokenizer.json</c> pair post-processing
/// template, which encodes exactly this convention).
/// </para>
///
/// <para>
/// <b>Truncation (<c>only_second</c>):</b> the query is encoded into a buffer one token shorter
/// than <see cref="MaxTokens"/> (see <see cref="QueryEncodeBufferLength"/>'s remarks) so that,
/// even in the extreme case where the query alone would consume the entire sequence budget, the
/// pair assembly can never exceed <see cref="MaxTokens"/> — the candidate side always absorbs
/// the truncation, down to zero candidate tokens in that extreme case, and the query is never
/// truncated for the pair's sake.
/// </para>
///
/// <para>
/// <b>Sigmoid:</b> the model's single <c>logits</c> output (shape <c>[batch, 1]</c>) ships with
/// <c>sbert_ce_default_activation_function: Identity</c> in its <c>config.json</c> — the
/// activation is deliberately not baked into the graph, so it is applied host-side here.
/// </para>
/// </summary>
public sealed class OnnxCrossEncoderScorer : IRelevanceScorer, IDisposable
{
    // The allowlisted model's tokenizer_config.json declares model_max_length: 512 (standard
    // BERT position-embedding cap) — verified directly against the pinned artifact at the time
    // this scorer was authored, the same way OnnxMemoryEmbedder's two allowlisted models both
    // cap at 512.
    private const int MaxTokens = 512;

    // The query is encoded into a buffer ONE token shorter than MaxTokens so that, even when the
    // query alone would consume every position under a plain single-sequence encode (queryLen up
    // to MaxTokens), the resulting query CONTENT length can never exceed MaxTokens-3. That
    // invariant is what guarantees EncodePair's assembled pair -- 1x[CLS] + query + 1x[SEP] +
    // candidate + 1x[SEP] -- never exceeds MaxTokens even in the pathological case where the
    // candidate is truncated to zero tokens. Without this one-token reservation, a query that
    // maxed out a full MaxTokens-sized single-sequence encode would leave no room for the pair's
    // second [SEP], overflowing the model's position-embedding table by one.
    private const int QueryEncodeBufferLength = MaxTokens - 1;

    private readonly InferenceSession _session;
    private readonly FastBertTokenizer.BertTokenizer _tokenizer;
    private readonly BoundedConcurrencyGate _gate;
    private readonly string _outputName;

    private OnnxCrossEncoderScorer(
        string modelId,
        InferenceSession session,
        FastBertTokenizer.BertTokenizer tokenizer,
        int maxConcurrency)
    {
        if (session.OutputMetadata.Count != 1)
            throw new InvalidOperationException(
                $"Relevance model '{modelId}' declares {session.OutputMetadata.Count} outputs; " +
                "OnnxCrossEncoderScorer expects exactly one (the single-logit classification head).");

        ModelId = modelId;
        _session = session;
        _tokenizer = tokenizer;
        _gate = new BoundedConcurrencyGate(maxConcurrency);
        _outputName = session.OutputMetadata.Keys.Single();
    }

    /// <inheritdoc />
    public string ModelId { get; }

    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <summary>
    /// Loads the ONNX model and WordPiece vocabulary from disk. Both files are expected to
    /// already be provisioned and hash-verified (<see cref="EmbeddingModelProvisioner"/>) — this
    /// constructor does no downloading or verification of its own.
    /// </summary>
    /// <param name="modelPath">Path to the <c>model.onnx</c> file.</param>
    /// <param name="vocabPath">Path to the WordPiece <c>vocab.txt</c> file.</param>
    /// <param name="modelId">The allowlisted model id these files correspond to.</param>
    /// <param name="maxConcurrency">Maximum concurrent inference calls (default 2).</param>
    /// <param name="intraOpNumThreads">Threads ONNX Runtime uses per inference call (default 4).</param>
    public static async Task<OnnxCrossEncoderScorer> LoadAsync(
        string modelPath,
        string vocabPath,
        string modelId,
        int maxConcurrency = 2,
        int intraOpNumThreads = 4,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var sessionOptions = new SessionOptions { IntraOpNumThreads = intraOpNumThreads };
        var session = new InferenceSession(modelPath, sessionOptions);
        try
        {
            var tokenizer = new FastBertTokenizer.BertTokenizer();
            // The allowlisted model (Xenova/ms-marco-MiniLM-L-6-v2) publishes do_lower_case=true in
            // its tokenizer_config.json — a standard BERT-base-uncased vocabulary.
            await tokenizer.LoadVocabularyAsync(vocabPath, convertInputToLowercase: true);

            return new OnnxCrossEncoderScorer(modelId, session, tokenizer, maxConcurrency);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<double>> ScoreAsync(string query, IReadOnlyList<string> candidates, CancellationToken ct)
    {
        if (candidates.Count == 0)
            return [];

        // Each candidate acquires the gate independently (mirrors
        // OnnxMemoryEmbedder.EmbedBatchAsync) rather than holding one slot for the whole call —
        // in practice this is at most AutoRecallMaxItems (3) pairs per turn, so the difference is
        // academic, but it keeps this call path consistent with the embedder's own convention.
        var tasks = new Task<double>[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            tasks[i] = _gate.RunAsync(_ => Task.FromResult(ScoreOne(query, candidate)), ct);
        }

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private double ScoreOne(string query, string candidate)
    {
        var (ids, mask, types, length) = EncodePair(query, candidate);

        var inputIdsTensor = new DenseTensor<long>(ids, [1, length]);
        var attentionMaskTensor = new DenseTensor<long>(mask, [1, length]);
        var tokenTypeIdsTensor = new DenseTensor<long>(types, [1, length]);

        var available = new Dictionary<string, NamedOnnxValue>(StringComparer.Ordinal)
        {
            ["input_ids"] = NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            ["attention_mask"] = NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor),
            ["token_type_ids"] = NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIdsTensor),
        };

        // Feed only the inputs the loaded graph actually declares (same defensive pattern as
        // OnnxMemoryEmbedder.EmbedOne) rather than hardcoding the production model's 3-input
        // signature — the test fixture graph declares the same three inputs, but this keeps the
        // two code paths structurally identical rather than by coincidence.
        var feed = new List<NamedOnnxValue>(_session.InputMetadata.Count);
        foreach (var inputName in _session.InputMetadata.Keys)
        {
            if (!available.TryGetValue(inputName, out var value))
                throw new InvalidOperationException(
                    $"Relevance model '{ModelId}' declares input '{inputName}', which this scorer does not know how to produce.");
            feed.Add(value);
        }

        using var outputs = _session.Run(feed);
        var logits = outputs.First(o => o.Name == _outputName).AsTensor<float>();
        var logit = logits[0, 0];

        return Sigmoid(logit);
    }

    /// <summary>
    /// Assembles <c>[CLS] query [SEP] candidate [SEP]</c> with correct <c>token_type_ids</c>
    /// (0 for the query segment including both flanking special tokens laid out below, 1 for the
    /// candidate segment and its closing <c>[SEP]</c>) and <c>only_second</c> truncation (the
    /// candidate is truncated to fit; the query never is — see <see cref="QueryEncodeBufferLength"/>'s
    /// remarks for the invariant that makes this safe), then pads the assembled length up to a
    /// bucket-of-8 boundary via <see cref="OnnxMemoryEmbedder.ComputeBucketedLength"/> — the same
    /// dynamic-length convention <see cref="OnnxMemoryEmbedder"/> already uses, reused directly
    /// rather than duplicated. Internal (not private) so <c>OnnxCrossEncoderScorerTests</c> can
    /// assert on the exact assembled arrays without needing a live ONNX <c>Run</c> for every
    /// encoding-correctness scenario.
    /// </summary>
    internal (long[] Ids, long[] Mask, long[] Types, int Length) EncodePair(string query, string candidate)
    {
        // Buffers sized so Encode's own padTo argument fully populates them; only the
        // non-padded prefix (found via the attention-mask sum) is meaningful, exactly like
        // OnnxMemoryEmbedder.EmbedOne's own actualLen/scratch-buffer pattern.
        var queryIds = new long[QueryEncodeBufferLength];
        var queryMask = new long[QueryEncodeBufferLength];
        var queryTypes = new long[QueryEncodeBufferLength];
        _tokenizer.Encode(query, queryIds, queryMask, queryTypes, QueryEncodeBufferLength);
        var queryLen = (int)queryMask.Sum();
        var queryContentLen = queryLen - 2; // drop [CLS] and the single-sequence encode's own [SEP]

        var candidateIds = new long[MaxTokens];
        var candidateMask = new long[MaxTokens];
        var candidateTypes = new long[MaxTokens];
        _tokenizer.Encode(candidate, candidateIds, candidateMask, candidateTypes, MaxTokens);
        var candidateLen = (int)candidateMask.Sum();
        var candidateContentLen = candidateLen - 2;

        // CLS/SEP ids are read off the query's own encode rather than hardcoded: FastBertTokenizer
        // assigns special-token ids from vocab.txt line numbers, so they vary across vocabularies
        // even though the tokens are always named [CLS]/[SEP] by convention.
        var clsId = queryIds[0];
        var sepId = queryIds[queryLen - 1];

        // only_second truncation: the candidate absorbs whatever budget remains after
        // [CLS] + query + 2x[SEP]. QueryEncodeBufferLength guarantees this is never negative.
        var availableForCandidate = Math.Max(0, MaxTokens - queryContentLen - 3);
        var truncatedCandidateLen = Math.Min(candidateContentLen, availableForCandidate);

        var rawLength = 1 + queryContentLen + 1 + truncatedCandidateLen + 1;
        var bucketLength = OnnxMemoryEmbedder.ComputeBucketedLength(rawLength);

        var ids = new long[bucketLength];
        var mask = new long[bucketLength];
        var types = new long[bucketLength];

        var pos = 0;
        ids[pos] = clsId;
        mask[pos] = 1;
        pos++;

        Array.Copy(queryIds, 1, ids, pos, queryContentLen);
        for (var i = 0; i < queryContentLen; i++)
            mask[pos + i] = 1;
        pos += queryContentLen;

        ids[pos] = sepId;
        mask[pos] = 1;
        pos++;

        Array.Copy(candidateIds, 1, ids, pos, truncatedCandidateLen);
        for (var i = 0; i < truncatedCandidateLen; i++)
        {
            types[pos + i] = 1;
            mask[pos + i] = 1;
        }
        pos += truncatedCandidateLen;

        ids[pos] = sepId;
        types[pos] = 1;
        mask[pos] = 1;

        // Positions [rawLength, bucketLength) are left at their default 0/0/0 (id/mask/type) —
        // the WordPiece convention every vocab.txt this repo touches follows ([PAD] is always
        // line 1, i.e. id 0) plus the model's own attention-masked self-attention makes the
        // padded id's exact value irrelevant to the score regardless.
        return (ids, mask, types, bucketLength);
    }

    private static double Sigmoid(float logit) => 1.0 / (1.0 + Math.Exp(-logit));

    public void Dispose() => _session.Dispose();
}

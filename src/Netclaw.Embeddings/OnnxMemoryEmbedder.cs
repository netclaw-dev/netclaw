// -----------------------------------------------------------------------
// <copyright file="OnnxMemoryEmbedder.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Numerics.Tensors;
using FastBertTokenizer;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Netclaw.Actors.Memory;

namespace Netclaw.Embeddings;

/// <summary>
/// In-process ONNX-backed <see cref="IMemoryEmbedder"/> (memory-core-redesign D1). Owns
/// exactly one <see cref="InferenceSession"/> and one <see cref="BertTokenizer"/> for its
/// lifetime — construction loads both once; there is no re-provisioning without constructing a
/// new instance (daemon wiring for that is Stage B).
///
/// <para>
/// <b>Pooling:</b> both allowlisted models (<see cref="EmbeddingModelProvisioner"/>'s
/// <c>snowflake-arctic-embed-m</c> and <c>mxbai-embed-large-v1</c>) are BERT-class encoders
/// exported with <c>add_pooling_layer=False</c> — their ONNX graphs return only
/// <c>last_hidden_state</c> (per-token hidden states), never a pre-pooled vector. Both model
/// cards document CLS-token pooling as the correct/default strategy for retrieval embeddings
/// (arctic-embed-m: "use the CLS token to embed each text portion"; mxbai-embed-large-v1:
/// "works really well with cls pooling (default)"), so this embedder always reads
/// <c>last_hidden_state[:, 0, :]</c> — position 0 along the sequence axis — rather than mean-
/// pooling across tokens. The result is then L2-normalized so stored cosine similarity needs
/// no further scaling.
/// </para>
///
/// <para>
/// <b>Inputs:</b> this embedder feeds only the input names the loaded ONNX graph actually
/// declares (<see cref="InferenceSession.InputMetadata"/>), rather than hardcoding the
/// production models' 3-input BERT signature (<c>input_ids</c>, <c>attention_mask</c>,
/// <c>token_type_ids</c>) — the test fixture graph declares a different, smaller input set, and
/// this embedder must work against either without a fixture-only code path.
/// </para>
///
/// <para>
/// <b>Query prefix (memory-query-prefix design D2):</b> asymmetric retrieval models document a
/// query-side instruction prefix that must never reach document embeddings. This embedder is
/// handed its active model's <c>QueryPrefix</c> (empty for a model that documents none) at
/// <see cref="LoadAsync"/> time and prepends it — before tokenization, so it counts against the
/// token budget like any other text — only when a caller passes
/// <see cref="Netclaw.Actors.Memory.EmbeddingPurpose.RetrievalQuery"/>.
/// <see cref="Netclaw.Actors.Memory.EmbeddingPurpose.Passage"/> embeddings are never prefixed,
/// which is what keeps them byte-identical to vectors already stored before prefix support
/// existed — no re-embed is required when a prefix is adopted.
/// </para>
///
/// <para>
/// <b>Concurrency:</b> a single <see cref="InferenceSession"/> supports concurrent
/// <see cref="InferenceSession.Run(IReadOnlyCollection{NamedOnnxValue})"/> calls, but an
/// unbounded number of them would oversubscribe the CPU beyond what
/// <see cref="SessionOptions.IntraOpNumThreads"/> assumes. <see cref="BoundedConcurrencyGate"/>
/// caps concurrent inference calls (default 2) so embedding work shares the machine
/// predictably with everything else the daemon is doing — this matters because query
/// embedding sits on the recall latency budget in a later slice.
/// </para>
/// </summary>
public sealed class OnnxMemoryEmbedder : IMemoryEmbedder, IDisposable
{
    // Both allowlisted models cap at 512 (their tokenizer_config.json model_max_length).
    private const int MaxTokens = 512;

    // Dynamic-length padding (memory-core-redesign Slice 4, design D6 mitigation): the ONNX
    // graph's sequence axis is symbolic (input_ids/attention_mask/token_type_ids all declare
    // [batch_size, sequence_length], no fixed shape), so padding to the actual tokenized length
    // -- rounded up to a multiple of this bucket -- instead of always MaxTokens is a drop-in
    // performance change with no retrieval-quality risk (measured cosine parity vs fixed-512:
    // 1.000000 on every sentence in the Slice 2/4 correctness set,
    // tools/embed-latency-bench). Reference-box short-query latency: p50 19.0ms / p95 20.9ms,
    // vs p50 281.9ms / p95 310.5ms fixed-512 -- ~15x faster, leaving large headroom under the
    // 150ms recall sub-budget (SQLiteMemoryRecallCoordinator.VectorEmbedSubBudgetMs).
    private const int DynamicLengthBucket = 8;

    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly BoundedConcurrencyGate _gate;
    private readonly string _outputName;
    private readonly string _queryPrefix;

    private OnnxMemoryEmbedder(
        string modelId,
        int dimensions,
        InferenceSession session,
        BertTokenizer tokenizer,
        int maxConcurrency,
        string queryPrefix)
    {
        if (session.OutputMetadata.Count != 1)
            throw new InvalidOperationException(
                $"Embedding model '{modelId}' declares {session.OutputMetadata.Count} outputs; " +
                "OnnxMemoryEmbedder expects exactly one (the per-token hidden-state tensor).");

        ModelId = modelId;
        Dimensions = dimensions;
        _session = session;
        _tokenizer = tokenizer;
        _gate = new BoundedConcurrencyGate(maxConcurrency);
        _outputName = session.OutputMetadata.Keys.Single();
        _queryPrefix = queryPrefix;
    }

    /// <inheritdoc />
    public string ModelId { get; }

    /// <inheritdoc />
    public int Dimensions { get; }

    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <summary>
    /// Loads the ONNX model and WordPiece vocabulary from disk. Both files are expected to
    /// already be provisioned and hash-verified (<see cref="EmbeddingModelProvisioner"/>) —
    /// this constructor does no downloading or verification of its own.
    /// </summary>
    /// <param name="modelPath">Path to the <c>model.onnx</c> file.</param>
    /// <param name="vocabPath">Path to the WordPiece <c>vocab.txt</c> file.</param>
    /// <param name="modelId">The allowlisted model id these files correspond to.</param>
    /// <param name="dimensions">Expected output vector width, from the allowlist manifest.</param>
    /// <param name="queryPrefix">
    /// The allowlist manifest's <see cref="EmbeddingModelManifestEntry.QueryPrefix"/> for this
    /// model id (memory-query-prefix design D2) — pass <see cref="string.Empty"/> for a model
    /// that documents no retrieval-query prefix, or for a caller (a test fixture graph) that has
    /// no manifest entry at all. Required rather than defaulted so every call site names its
    /// choice explicitly; there is no safe default between "this model has a prefix" and "it
    /// doesn't."
    /// </param>
    /// <param name="maxConcurrency">Maximum concurrent inference calls (default 2).</param>
    /// <param name="intraOpNumThreads">Threads ONNX Runtime uses per inference call (default 4).</param>
    public static async Task<OnnxMemoryEmbedder> LoadAsync(
        string modelPath,
        string vocabPath,
        string modelId,
        int dimensions,
        string queryPrefix,
        int maxConcurrency = 2,
        int intraOpNumThreads = 4,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(queryPrefix);

        using var sessionOptions = new SessionOptions { IntraOpNumThreads = intraOpNumThreads };
        var session = new InferenceSession(modelPath, sessionOptions);
        try
        {
            var tokenizer = new BertTokenizer();
            // Both allowlisted models (Snowflake/snowflake-arctic-embed-m,
            // mixedbread-ai/mxbai-embed-large-v1) publish do_lower_case=true in their
            // tokenizer_config.json — a standard BERT-base-uncased vocabulary.
            await tokenizer.LoadVocabularyAsync(vocabPath, convertInputToLowercase: true);

            return new OnnxMemoryEmbedder(modelId, dimensions, session, tokenizer, maxConcurrency, queryPrefix);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<ReadOnlyMemory<float>> EmbedAsync(string text, EmbeddingPurpose purpose, CancellationToken ct)
        => await _gate.RunAsync(_ => Task.FromResult(EmbedOne(text, purpose)), ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedBatchAsync(IReadOnlyList<string> texts, EmbeddingPurpose purpose, CancellationToken ct)
    {
        if (texts.Count == 0)
            return [];

        // Each item acquires the gate independently (rather than holding one slot for the
        // whole batch) so a large batch call and a concurrent single EmbedAsync call from the
        // live write path interleave fairly instead of one blocking behind the other for the
        // batch's full duration.
        var tasks = new Task<ReadOnlyMemory<float>>[texts.Count];
        for (var i = 0; i < texts.Count; i++)
        {
            var text = texts[i];
            tasks[i] = _gate.RunAsync(_ => Task.FromResult(EmbedOne(text, purpose)), ct);
        }

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private ReadOnlyMemory<float> EmbedOne(string text, EmbeddingPurpose purpose)
    {
        // Prefix applied before tokenization (memory-query-prefix design D2) so it counts
        // against the token budget/bucketing below like any other text, and so the resulting
        // vector reflects the exact string the model card instructs embedding. Never applied to
        // Passage purpose -- that is what keeps document-side vectors byte-identical to ones
        // stored before prefix support existed.
        var effectiveText = purpose == EmbeddingPurpose.RetrievalQuery && _queryPrefix.Length > 0
            ? _queryPrefix + text
            : text;

        var scratchIds = new long[MaxTokens];
        var scratchMask = new long[MaxTokens];
        var scratchTypes = new long[MaxTokens];

        // This overload writes into the caller-supplied spans instead of BertTokenizer's
        // internal reused buffers, so calling it from multiple gate-scheduled tasks
        // concurrently against the one shared _tokenizer instance is safe.
        _tokenizer.Encode(effectiveText, scratchIds, scratchMask, scratchTypes, MaxTokens);

        // Dynamic-length padding: only feed the ONNX graph the actual tokenized length
        // (rounded up to DynamicLengthBucket), not the full fixed-512 scratch buffers -- see
        // DynamicLengthBucket's remarks.
        var actualLen = (int)scratchMask.Sum();
        var bucketLen = ComputeBucketedLength(actualLen);

        var inputIds = scratchIds[..bucketLen];
        var attentionMask = scratchMask[..bucketLen];
        var tokenTypeIds = scratchTypes[..bucketLen];

        var inputIdsTensor = new DenseTensor<long>(inputIds, [1, bucketLen]);
        var attentionMaskTensor = new DenseTensor<long>(attentionMask, [1, bucketLen]);
        var tokenTypeIdsTensor = new DenseTensor<long>(tokenTypeIds, [1, bucketLen]);

        var available = new Dictionary<string, NamedOnnxValue>(StringComparer.Ordinal)
        {
            ["input_ids"] = NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            ["attention_mask"] = NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor),
            ["token_type_ids"] = NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIdsTensor),
        };

        var feed = new List<NamedOnnxValue>(_session.InputMetadata.Count);
        foreach (var inputName in _session.InputMetadata.Keys)
        {
            if (!available.TryGetValue(inputName, out var value))
                throw new InvalidOperationException(
                    $"Embedding model '{ModelId}' declares input '{inputName}', which this embedder does not know how to produce.");
            feed.Add(value);
        }

        using var outputs = _session.Run(feed);
        var lastHiddenState = outputs.First(o => o.Name == _outputName).AsTensor<float>();

        var dims = lastHiddenState.Dimensions[^1];
        if (dims != Dimensions)
            throw new InvalidOperationException(
                $"Embedding model '{ModelId}' produced a {dims}-dimensional vector; allowlist declares {Dimensions}.");

        var vector = new float[dims];
        for (var d = 0; d < dims; d++)
            vector[d] = lastHiddenState[0, 0, d]; // CLS token: position 0 along the sequence axis

        NormalizeL2(vector);
        return vector;
    }

    /// <summary>
    /// Rounds <paramref name="actualTokenCount"/> up to the nearest multiple of
    /// <see cref="DynamicLengthBucket"/> (minimum one bucket). A pure, directly-unit-tested
    /// helper so the rounding rule itself has coverage independent of a live ONNX session.
    /// Never exceeds <see cref="MaxTokens"/> in practice: <paramref name="actualTokenCount"/>
    /// comes from <see cref="FastBertTokenizer.BertTokenizer.Encode"/>'s attention-mask sum,
    /// which <see cref="EmbedOne"/> already truncates to <see cref="MaxTokens"/>, and
    /// <see cref="MaxTokens"/> (512) is itself a multiple of <see cref="DynamicLengthBucket"/>.
    /// </summary>
    internal static int ComputeBucketedLength(int actualTokenCount)
    {
        if (actualTokenCount <= 0)
            return DynamicLengthBucket;

        return Math.Max(
            DynamicLengthBucket,
            ((actualTokenCount + DynamicLengthBucket - 1) / DynamicLengthBucket) * DynamicLengthBucket);
    }

    private static void NormalizeL2(float[] vector)
    {
        var norm = TensorPrimitives.Norm((ReadOnlySpan<float>)vector);
        if (norm > 0f)
            TensorPrimitives.Divide(vector, norm, vector);
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>
/// Bounds concurrent execution of a unit of async work and reports the peak concurrency
/// actually observed, so tests can prove the bound is enforced under real contention without
/// racing on wall-clock sleeps. Used by <see cref="OnnxMemoryEmbedder"/> to keep concurrent
/// ONNX inference calls within a predictable share of the CPU.
/// </summary>
internal sealed class BoundedConcurrencyGate
{
    private readonly SemaphoreSlim _semaphore;
    private int _active;
    private int _peakObserved;

    public BoundedConcurrencyGate(int maxConcurrency)
    {
        if (maxConcurrency <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency), maxConcurrency, "Must be positive.");

        MaxConcurrency = maxConcurrency;
        _semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    public int MaxConcurrency { get; }

    /// <summary>Highest number of calls ever observed executing inside <see cref="RunAsync{T}"/> concurrently.</summary>
    public int PeakObservedConcurrency => Volatile.Read(ref _peakObserved);

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = Interlocked.Increment(ref _active);
            InterlockedMax(ref _peakObserved, current);
            try
            {
                return await work(ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int initial;
        do
        {
            initial = Volatile.Read(ref target);
            if (value <= initial)
                return;
        } while (Interlocked.CompareExchange(ref target, value, initial) != initial);
    }
}

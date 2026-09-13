// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

// Honest one-shot latency bench for OnnxMemoryEmbedder (memory-core-redesign task 2.13).
//
// Loads the production embedder exactly the way the daemon would (provisioner hash-verify
// against the pinned allowlist, then OnnxMemoryEmbedder.LoadAsync — same pooling, same
// IntraOpNumThreads=4, same BoundedConcurrencyGate(2)), then times batch=1 EmbedAsync calls
// across three hardcoded corpora (short query / medium / doc-length), a cold-load measurement,
// and a concurrency-2 pass. This is a Stopwatch harness, not BenchmarkDotNet — the goal is one
// honest percentile table on the reference box, not microbenchmark rigor.
//
// Usage: dotnet run -c Release --project tools/embed-latency-bench [modelDirectory]
// Default modelDirectory: ~/recall-research-local/models/snowflake-arctic-embed-m
//
// Never downloads anything: if the model directory is missing or fails SHA-256 verification
// against EmbeddingModelProvisioner.Allowlist, this exits with an error instead of fetching it.

using System.Diagnostics;
using System.Numerics.Tensors;
using FastBertTokenizer;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Netclaw.Actors.Memory;
using Netclaw.Embeddings;

// Captured before any other work so the cold-load number can include .NET host/runtime
// startup — the literal "process start -> first embed complete" the task asked for.
var processStartUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();

const int WarmupIterations = 20;
const int TimedIterations = 200;
const int ConcurrencyIterationsPerLoop = 100;
const int MaxTokens = 512;
const int DynamicLengthBucket = 8;

// Honest contention context: load average is one line in /proc/loadavg (1m 5m 15m ...).
// Read once here, and again at the very end, so the report shows what the box looked like
// before this ~5-6 minute run started and what it drifted to by the time it finished.
string ReadLoadAverage() => File.Exists("/proc/loadavg")
    ? string.Join(' ', File.ReadAllText("/proc/loadavg").Split(' ').Take(3))
    : "unavailable (non-Linux host)";

var loadAverageBefore = ReadLoadAverage();

var modelDir = args.Length > 0
    ? args[0]
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "recall-research-local", "models", "snowflake-arctic-embed-m");

Console.WriteLine($"Model directory: {modelDir}");

using var httpClient = new HttpClient(); // required by EmbeddingModelProvisioner's constructor; never used for I/O here — TryLoadVerifiedAsync is disk-only.
var provisioner = new EmbeddingModelProvisioner(httpClient, EmbeddingModelProvisioner.Allowlist);

var verified = await provisioner.TryLoadVerifiedAsync("snowflake-arctic-embed-m", modelDir);
if (verified is null)
{
    Console.Error.WriteLine(
        $"STOP: '{modelDir}' does not contain a hash-verified snowflake-arctic-embed-m " +
        "(model.onnx + vocab.txt) matching EmbeddingModelProvisioner.Allowlist. Refusing to " +
        "proceed — this tool never downloads.");
    return 1;
}

Console.WriteLine($"Verified model: {verified.ModelId} ({verified.Dimensions} dims) at {verified.ModelPath}");

// --- Dynamic-sequence-length feasibility check (Slice 4 design experiment) ------------------
//
// A -1 (or named symbolic) dimension on the sequence axis means the exported ONNX graph
// accepts any sequence length at inference time — the padding to a fixed MaxTokens=512 in
// OnnxMemoryEmbedder is an application choice, not something the graph requires. A concrete
// positive dimension there means the graph was exported with a static shape and rejects
// anything else; dynamic length would need a re-export, not just a code change.
bool sequenceAxisIsDynamic;
using (var diagSession = new InferenceSession(verified.ModelPath))
{
    Console.WriteLine();
    Console.WriteLine("ONNX graph input metadata (dynamic-sequence-length feasibility check):");
    var seqDims = new List<bool>();
    foreach (var (name, meta) in diagSession.InputMetadata)
    {
        var dims = string.Join(", ", meta.Dimensions);
        var symbolic = string.Join(", ", meta.SymbolicDimensions.Select(s => string.IsNullOrEmpty(s) ? "<fixed>" : s));
        Console.WriteLine($"  {name}: dims=[{dims}] symbolic=[{symbolic}]");

        // Sequence axis is conventionally dimension index 1 (dim 0 is batch) for a
        // [batch, sequence] BERT input tensor.
        if (meta.Dimensions.Length > 1)
            seqDims.Add(meta.Dimensions[1] < 0 || !string.IsNullOrEmpty(meta.SymbolicDimensions[1]));
    }

    sequenceAxisIsDynamic = seqDims.Count > 0 && seqDims.All(d => d);
    Console.WriteLine(sequenceAxisIsDynamic
        ? "  Verdict: sequence axis is DYNAMIC on every input — graph accepts variable-length sequences."
        : "  Verdict: sequence axis is FIXED on at least one input — graph requires the exported shape.");
}

// --- Corpora (deterministic, hardcoded) ---------------------------------------------------

string[] shortQueries =
[
    "what's our grafana dashboard convention?",
    "how do I restart the daemon safely?",
    "where do we store the slack webhook secret?",
    "what does MinCosineSimilarity default to in production?",
    "did we ever decide on mirroring model artifacts into R2?",
    "what version is pinned in Directory.Build.props right now?",
    "how many logical cores does the reference box have?",
    "which model is the allowlist's default embedder?",
    "what's the checkpoint worker's idle loop actually for?",
    "can you summarize yesterday's release notes for me?",
    "who owns the memory_embeddings table schema change?",
    "what's the config key for the recall timeout?",
    "is the semaphore capped at two concurrent inference calls?",
    "what tokenizer library are we using for BERT models?",
    "when did we last run the full eval suite?",
    "what's the vector weight in the hybrid fusion score?",
    "how do I run the light smoke test suite locally?",
    "what's currently blocking slice four from shipping?",
    "which subreddit rule blocks self-promotional posts?",
    "what does netclaw doctor --fix actually repair?",
];

// Medium/doc-length corpora are built from a fixed sentence bank (thematically real content
// about this codebase) rather than hand-authored essays, so their length is deterministically
// controllable; actual token counts are measured below rather than assumed.
string[] sentenceBank =
[
    "The daemon persists session state under the Slack thread identity of channelId and threadTs, so every conversation maps to exactly one actor.",
    "Query embedding runs in-process through OnnxRuntime with CLS-token pooling and L2 normalization before the vector is compared against stored memories.",
    "The recall coordinator merges FTS5 lexical candidates with vector nearest-neighbor candidates before applying the policy gates uniformly across both sources.",
    "Consolidation only executes from a human-ratified plan file, never automatically, and always takes a VACUUM INTO backup before touching the live database.",
    "The expiry sweep runs inside the checkpoint worker's idle loop and deletes rows whose expires_at timestamp has already passed the grace window.",
    "MinCosineSimilarity acts as an absolute floor rather than a relative rank cutoff, so a mediocre top candidate can still be suppressed entirely.",
    "The embedding model allowlist pins a specific HuggingFace commit SHA for both the model weights and the tokenizer vocabulary file.",
    "SchemaFixResolver can only repair validation errors it recognizes, so new enum properties must ship as strings with named values from day one.",
    "Akka.Hosting wires the actor system through dependency injection, keeping the constructor signature explicit about every collaborator the actor needs.",
    "The bounded concurrency gate caps simultaneous ONNX inference calls at two by default, sharing the CPU predictably with the rest of the daemon.",
    "TimeProvider is injected everywhere instead of DateTimeOffset.UtcNow so that tests can advance a virtual clock without any wall-clock sleeping.",
    "The nominator model and the fallback model both export fp32 ONNX graphs with add_pooling_layer disabled, so pooling always happens in application code.",
    "Backfill re-embeds only rows whose content hash no longer matches the stored hash, making repeated runs of the same backfill essentially free.",
    "The doctor command surfaces embedding coverage gaps, model hash mismatches, and mixed-model rows as loud warnings rather than silent degradation.",
    "Slopwatch flags disabled tests, suppressed warnings, and empty catch blocks as reward-hacking signals that must be fixed or explicitly baselined.",
    "The observer sidecar proposes a recall mode for each distilled memory, and the policy gate honors that proposal for durable facts by default.",
    "A crash between the document commit and the embedding upsert leaves a coverage gap that the next backfill pass repairs automatically.",
    "The vector index is a flat in-memory array per model, invalidated by a store version counter whenever the underlying table changes.",
    "Structural append is the fallback path whenever the merge guard rejects a synthesized body for losing too many load-bearing tokens.",
    "Trace-class memories are short-lived operational state with a seventy-two hour time-to-live, weighted below durable facts during recall scoring.",
    "The tool-lessons block is injected once per tool per session as an exact anchor-id lookup, entirely outside the pre-turn recall budget.",
    "Recency decay multiplies the fused score by a floor-bounded factor derived from a configurable half-life measured in days.",
    "Every configuration schema uses additionalProperties false, so an unlisted property on any Config type is rejected at doctor time.",
    "The release version gate checks that the pushed tag matches VersionPrefix and VersionSuffix exactly, rejecting any other tag shape.",
    "Prerelease tags always use the dotted beta.N form, because a mixed identifier like beta1 sorts lexically in the wrong order.",
    "The memory store's InitializeAsync method creates the embeddings table idempotently, independent of the daemon's own migration pipeline.",
    "Evidence records are policy-forced into an immutable, searchable class, which is why lessons needed their own dedicated memory class instead.",
    "The 22 legacy compaction rows were repaired directly during the quick-win slice, ahead of the taxonomy rebalance that formalized the invariant.",
    "Content hash is computed over the normalized title and body concatenation, using SHA-256 the same way the provisioner verifies model artifacts.",
    "A rate-limited log line fires whenever vector recall degrades to lexical-only, so operators see the condition without being flooded by it.",
];

string BuildFromBank(int startIndex, int count)
{
    var parts = new string[count];
    for (var i = 0; i < count; i++)
        parts[i] = sentenceBank[(startIndex + i) % sentenceBank.Length];
    return string.Join(' ', parts);
}

string[] mediumCorpus = Enumerable.Range(0, 20)
    .Select(i => BuildFromBank(startIndex: i * 3, count: 6))
    .ToArray();

string[] docCorpus = Enumerable.Range(0, 20)
    .Select(i => BuildFromBank(startIndex: i * 7, count: 15))
    .ToArray();

// Fixed 10-sentence correctness set spanning short queries and longer bank sentences, so the
// fixed-512-vs-dynamic-length parity check isn't only exercised at one length.
string[] correctnessSentences =
[
    .. shortQueries.Take(5),
    .. sentenceBank.Take(5),
];

// --- Token-count diagnostic: measure the corpora shape claim rather than assume it ----------

var diagTokenizer = new BertTokenizer();
await diagTokenizer.LoadVocabularyAsync(verified.VocabPath, convertInputToLowercase: true);

(int Min, int Max, double Mean) TokenStats(string[] corpus)
{
    var counts = new int[corpus.Length];
    for (var i = 0; i < corpus.Length; i++)
    {
        var ids = new long[MaxTokens];
        var mask = new long[MaxTokens];
        var types = new long[MaxTokens];
        diagTokenizer.Encode(corpus[i], ids, mask, types, MaxTokens);
        counts[i] = (int)mask.Sum();
    }
    return (counts.Min(), counts.Max(), counts.Average());
}

var shortStats = TokenStats(shortQueries);
var mediumStats = TokenStats(mediumCorpus);
var docStats = TokenStats(docCorpus);

Console.WriteLine();
Console.WriteLine("Corpus token counts (actual, via production tokenizer):");
Console.WriteLine($"  short : min={shortStats.Min} max={shortStats.Max} mean={shortStats.Mean:F1}");
Console.WriteLine($"  medium: min={mediumStats.Min} max={mediumStats.Max} mean={mediumStats.Mean:F1}");
Console.WriteLine($"  doc   : min={docStats.Min} max={docStats.Max} mean={docStats.Mean:F1}");

// --- Cold load -------------------------------------------------------------------------------

var loadOnlySw = Stopwatch.StartNew();
var embedder = await OnnxMemoryEmbedder.LoadAsync(verified.ModelPath, verified.VocabPath, verified.ModelId, verified.Dimensions, verified.QueryPrefix);
// Cold-load's first embed mirrors EmbeddingWarmupHostedService's own warm-up call (Passage
// purpose) -- see that type's remarks.
_ = await embedder.EmbedAsync(shortQueries[0], EmbeddingPurpose.Passage, CancellationToken.None);
loadOnlySw.Stop();
var processToFirstEmbedMs = (DateTime.UtcNow - processStartUtc).TotalMilliseconds;

Console.WriteLine();
Console.WriteLine($"Cold load — process start -> first embed complete: {processToFirstEmbedMs:F1} ms (includes .NET host/runtime startup)");
Console.WriteLine($"Cold load — LoadAsync + first embed only: {loadOnlySw.Elapsed.TotalMilliseconds:F1} ms");

// --- Percentile helper -----------------------------------------------------------------------

Row Percentiles(string label, List<double> samplesMs)
{
    var sorted = samplesMs.Order().ToArray();
    double Pct(double p)
    {
        var rank = (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }

    return new Row(label, sorted.Length, Pct(50), Pct(90), Pct(95), Pct(99), sorted[^1], sorted.Average());
}

async Task<List<double>> RunCorpus(string[] corpus, int warmup, int timed, EmbeddingPurpose purpose)
{
    for (var i = 0; i < warmup; i++)
        _ = await embedder.EmbedAsync(corpus[i % corpus.Length], purpose, CancellationToken.None);

    var samples = new List<double>(timed);
    for (var i = 0; i < timed; i++)
    {
        var sw = Stopwatch.StartNew();
        _ = await embedder.EmbedAsync(corpus[i % corpus.Length], purpose, CancellationToken.None);
        sw.Stop();
        samples.Add(sw.Elapsed.TotalMilliseconds);
    }

    return samples;
}

var rows = new List<Row>
{
    // "short" mirrors SQLiteMemoryRecallCoordinator's per-turn query embedding (memory-query-
    // prefix design D2): RetrievalQuery purpose, so this measurement includes the active
    // model's query prefix -- the real cost VectorEmbedSubBudgetMs must budget for. "medium"/
    // "doc" mirror embed-on-write/backfill document embedding: Passage purpose, never prefixed.
    Percentiles("short", await RunCorpus(shortQueries, WarmupIterations, TimedIterations, EmbeddingPurpose.RetrievalQuery)),
    Percentiles("medium", await RunCorpus(mediumCorpus, WarmupIterations, TimedIterations, EmbeddingPurpose.Passage)),
    Percentiles("doc", await RunCorpus(docCorpus, WarmupIterations, TimedIterations, EmbeddingPurpose.Passage)),
};

// --- Concurrency-2 short-query pass (two parallel loops share the SemaphoreSlim(2) gate) ---

async Task<List<double>> RunConcurrentLoop(int iterations)
{
    var samples = new List<double>(iterations);
    for (var i = 0; i < iterations; i++)
    {
        var sw = Stopwatch.StartNew();
        _ = await embedder.EmbedAsync(shortQueries[i % shortQueries.Length], EmbeddingPurpose.RetrievalQuery, CancellationToken.None);
        sw.Stop();
        samples.Add(sw.Elapsed.TotalMilliseconds);
    }

    return samples;
}

var concurrencySw = Stopwatch.StartNew();
var concurrentResults = await Task.WhenAll(
    RunConcurrentLoop(ConcurrencyIterationsPerLoop),
    RunConcurrentLoop(ConcurrencyIterationsPerLoop));
concurrencySw.Stop();
var concurrentSamples = concurrentResults[0].Concat(concurrentResults[1]).ToList();
rows.Add(Percentiles("short (concurrency=2)", concurrentSamples));

Console.WriteLine();
Console.WriteLine($"Concurrency-2 pass total wall time: {concurrencySw.Elapsed.TotalMilliseconds:F1} ms for {concurrentSamples.Count} total calls (2x{ConcurrencyIterationsPerLoop})");

// Capture fixed-512 embeddings for the correctness set before disposing the fixed embedder —
// these are compared against the dynamic-length variant below (bitwise-different padding, same
// semantic content, should cosine-agree near 1.0 if the attention mask does its job).
var fixedCorrectnessEmbeddings = new ReadOnlyMemory<float>[correctnessSentences.Length];
for (var i = 0; i < correctnessSentences.Length; i++)
    fixedCorrectnessEmbeddings[i] = await embedder.EmbedAsync(correctnessSentences[i], EmbeddingPurpose.Passage, CancellationToken.None);

embedder.Dispose();

// --- Dynamic sequence length experiment (Slice 4 design decision) --------------------------
//
// Bench-only parallel code path: OnnxMemoryEmbedder is not touched. This loads its own
// InferenceSession + BertTokenizer and pads each input only to its actual tokenized length,
// rounded up to a multiple of DynamicLengthBucket, instead of the fixed MaxTokens=512.
List<(string Sentence, float Cosine)>? correctnessResults = null;

if (sequenceAxisIsDynamic)
{
    using var dynamicSessionOptions = new SessionOptions { IntraOpNumThreads = 4 };
    using var dynamicSession = new InferenceSession(verified.ModelPath, dynamicSessionOptions);
    var dynamicTokenizer = new BertTokenizer();
    await dynamicTokenizer.LoadVocabularyAsync(verified.VocabPath, convertInputToLowercase: true);
    var outputName = dynamicSession.OutputMetadata.Keys.Single();

    ReadOnlyMemory<float> EmbedOneDynamic(string text)
    {
        var scratchIds = new long[MaxTokens];
        var scratchMask = new long[MaxTokens];
        var scratchTypes = new long[MaxTokens];
        dynamicTokenizer.Encode(text, scratchIds, scratchMask, scratchTypes, MaxTokens);

        var actualLen = (int)scratchMask.Sum();
        var bucketLen = Math.Max(DynamicLengthBucket, ((actualLen + DynamicLengthBucket - 1) / DynamicLengthBucket) * DynamicLengthBucket);

        var inputIds = scratchIds[..bucketLen];
        var attentionMask = scratchMask[..bucketLen];
        var tokenTypeIds = scratchTypes[..bucketLen];

        var available = new Dictionary<string, NamedOnnxValue>(StringComparer.Ordinal)
        {
            ["input_ids"] = NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds, [1, bucketLen])),
            ["attention_mask"] = NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(attentionMask, [1, bucketLen])),
            ["token_type_ids"] = NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(tokenTypeIds, [1, bucketLen])),
        };

        var feed = new List<NamedOnnxValue>(dynamicSession.InputMetadata.Count);
        foreach (var inputName in dynamicSession.InputMetadata.Keys)
            feed.Add(available[inputName]);

        using var outputs = dynamicSession.Run(feed);
        var lastHiddenState = outputs.First(o => o.Name == outputName).AsTensor<float>();
        var dims = lastHiddenState.Dimensions[^1];

        var vector = new float[dims];
        for (var d = 0; d < dims; d++)
            vector[d] = lastHiddenState[0, 0, d]; // CLS token

        var norm = TensorPrimitives.Norm((ReadOnlySpan<float>)vector);
        if (norm > 0f)
            TensorPrimitives.Divide(vector, norm, vector);

        return vector;
    }

    List<double> RunCorpusDynamic(string[] corpus, int warmup, int timed)
    {
        for (var i = 0; i < warmup; i++)
            _ = EmbedOneDynamic(corpus[i % corpus.Length]);

        var samples = new List<double>(timed);
        for (var i = 0; i < timed; i++)
        {
            var sw = Stopwatch.StartNew();
            _ = EmbedOneDynamic(corpus[i % corpus.Length]);
            sw.Stop();
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }

        return samples;
    }

    rows.Add(Percentiles("short (dynamic-len)", RunCorpusDynamic(shortQueries, WarmupIterations, TimedIterations)));
    rows.Add(Percentiles("medium (dynamic-len)", RunCorpusDynamic(mediumCorpus, WarmupIterations, TimedIterations)));
    rows.Add(Percentiles("doc (dynamic-len)", RunCorpusDynamic(docCorpus, WarmupIterations, TimedIterations)));

    // Correctness: same 10 sentences, dynamic-length path, cosine-compared to the fixed-512
    // embeddings captured above. Both vectors are already L2-normalized, so cosine similarity
    // reduces to a plain dot product.
    correctnessResults = new List<(string, float)>(correctnessSentences.Length);
    for (var i = 0; i < correctnessSentences.Length; i++)
    {
        var dynamicVec = EmbedOneDynamic(correctnessSentences[i]);
        var cosine = TensorPrimitives.Dot(fixedCorrectnessEmbeddings[i].Span, dynamicVec.Span);
        correctnessResults.Add((correctnessSentences[i], cosine));
    }
}
else
{
    Console.WriteLine();
    Console.WriteLine(
        "Dynamic-length pass SKIPPED: the ONNX graph's sequence axis is fixed on at least one " +
        "input, so it rejects any shape other than the exported one. Padding to a different " +
        "fixed size (e.g. 64) is not an option either — a statically-shaped graph has exactly " +
        "one legal input shape, not a small set of them. Verdict: dynamic sequence length is " +
        "NOT a drop-in change here; it would require re-exporting the ONNX graph with dynamic " +
        "axes on the sequence dimension, or pursuing int8 quantization (the deferred D2 lever) " +
        "instead.");
}

// --- Report ------------------------------------------------------------------------------

Console.WriteLine();
Console.WriteLine($"{"corpus",-24}{"n",5}{"p50",8}{"p90",8}{"p95",8}{"p99",8}{"max",8}{"mean",8}   (ms, batch=1)");
foreach (var row in rows)
{
    Console.WriteLine(
        $"{row.Label,-24}{row.N,5}{row.P50,8:F1}{row.P90,8:F1}{row.P95,8:F1}{row.P99,8:F1}{row.Max,8:F1}{row.Mean,8:F1}");
}

if (correctnessResults is not null)
{
    Console.WriteLine();
    Console.WriteLine("Fixed-512 vs dynamic-length correctness check (cosine similarity, 10 fixed sentences):");
    foreach (var (sentence, cosine) in correctnessResults)
    {
        var preview = sentence.Length > 60 ? sentence[..60] + "..." : sentence;
        Console.WriteLine($"  {cosine:F6}  \"{preview}\"");
    }

    var minCosine = correctnessResults.Min(r => r.Cosine);
    var meanCosine = correctnessResults.Average(r => r.Cosine);
    Console.WriteLine($"  min={minCosine:F6} mean={meanCosine:F6}");
}

Console.WriteLine();
Console.WriteLine($"Load average before run (1m 5m 15m): {loadAverageBefore}");
Console.WriteLine($"Load average after run  (1m 5m 15m): {ReadLoadAverage()}");

return 0;

internal readonly record struct Row(string Label, int N, double P50, double P90, double P95, double P99, double Max, double Mean);

// -----------------------------------------------------------------------
// <copyright file="EmbeddingModelProvisioner.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;

namespace Netclaw.Embeddings;

/// <summary>
/// One entry in <see cref="EmbeddingModelProvisioner.Allowlist"/>: everything needed to fetch
/// and verify one embedding model's artifacts. <see cref="ModelUrl"/>/<see cref="TokenizerUrl"/>
/// are pinned to a specific upstream commit (not a mutable branch) so the pinned SHA-256 values
/// can never silently stop matching what the URL serves.
/// </summary>
/// <param name="ModelId">Allowlist key, e.g. <c>snowflake-arctic-embed-m</c>.</param>
/// <param name="ModelUrl">Download location for <c>model.onnx</c>.</param>
/// <param name="TokenizerUrl">Download location for the WordPiece <c>vocab.txt</c>.</param>
/// <param name="ModelSha256">Expected SHA-256 (lowercase hex) of the model artifact.</param>
/// <param name="TokenizerSha256">Expected SHA-256 (lowercase hex) of the vocab artifact.</param>
/// <param name="Dimensions">Embedding vector width this model produces.</param>
/// <param name="ModelByteSize">Expected byte size of the model artifact — a cheap first check before hashing.</param>
/// <param name="QueryPrefix">
/// The model card's documented retrieval-query prefix (memory-query-prefix design D2), applied
/// verbatim by <see cref="OnnxMemoryEmbedder"/> when embedding for
/// <see cref="Netclaw.Actors.Memory.EmbeddingPurpose.RetrievalQuery"/>. Empty for a model that
/// documents no query-side prefix. Pinned next to the model hash in the same entry so a model
/// bump forces the author past this field too — a stale prefix silently paired with a new
/// model's weights would degrade retrieval quality without any loud failure.
/// </param>
/// <param name="CalibratedMinCosineSimilarity">
/// The absolute cosine floor calibrated for this model id in its documented retrieval-query
/// encoding (with <see cref="QueryPrefix"/> applied) — memory-query-prefix design D3/D4: "the
/// same manifest-carries-calibration pattern the relevance gate established with
/// <see cref="RelevanceModelManifestEntry.CalibratedThreshold"/>." <c>null</c> means this entry
/// has not been calibrated for retrieval: <see cref="Netclaw.Actors.Sessions.SQLiteMemoryRecallCoordinator"/>
/// treats an active model with no calibration and no explicit
/// <c>Memory.Recall.MinCosineSimilarity</c> override as hybrid-recall-unavailable (lexical-only,
/// degraded log) rather than guessing a floor calibrated for a different model or encoding mode.
/// </param>
public sealed record EmbeddingModelManifestEntry(
    string ModelId,
    Uri ModelUrl,
    Uri TokenizerUrl,
    string ModelSha256,
    string TokenizerSha256,
    int Dimensions,
    long ModelByteSize,
    string QueryPrefix,
    double? CalibratedMinCosineSimilarity);

/// <summary>
/// Files placed on disk by <see cref="EmbeddingModelProvisioner.ProvisionAsync"/>, ready for
/// <see cref="OnnxMemoryEmbedder.LoadAsync"/>. Carries the manifest entry's
/// <see cref="EmbeddingModelManifestEntry.QueryPrefix"/> and
/// <see cref="EmbeddingModelManifestEntry.CalibratedMinCosineSimilarity"/> alongside the
/// provisioned files (memory-query-prefix design D2) — the same "download result also carries
/// the model's calibration" shape as <see cref="ProvisionedRelevanceModel"/>.
/// </summary>
public sealed record ProvisionedEmbeddingModel(
    string ModelId,
    string ModelPath,
    string VocabPath,
    int Dimensions,
    string QueryPrefix,
    double? CalibratedMinCosineSimilarity);

/// <summary>
/// One entry in <see cref="EmbeddingModelProvisioner.RelevanceAllowlist"/> (memory-relevance-gate
/// D3): the same download/verification fields as <see cref="EmbeddingModelManifestEntry"/>, minus
/// <c>Dimensions</c> (a cross-encoder produces a single logit, not a fixed-width vector) and plus
/// <see cref="CalibratedThreshold"/> — the one field embedding manifests don't need. The
/// threshold travels with the model id it was measured against so a future model swap can never
/// silently reuse a threshold calibrated for a different model's score distribution.
/// </summary>
/// <param name="ModelId">Allowlist key, e.g. <c>ms-marco-minilm-l-6-v2</c>.</param>
/// <param name="ModelUrl">Download location for <c>model.onnx</c>.</param>
/// <param name="TokenizerUrl">Download location for the WordPiece <c>vocab.txt</c>.</param>
/// <param name="ModelSha256">Expected SHA-256 (lowercase hex) of the model artifact.</param>
/// <param name="TokenizerSha256">Expected SHA-256 (lowercase hex) of the vocab artifact.</param>
/// <param name="ModelByteSize">Expected byte size of the model artifact — a cheap first check before hashing.</param>
/// <param name="CalibratedThreshold">
/// The similarity threshold calibrated for this model id's score distribution (memory-relevance-gate
/// D2: S*=0.02 for the shipped <c>ms-marco-minilm-l-6-v2</c>). Governs gating unless the operator
/// configures an explicit <c>Memory.Recall.RelevanceGate.Threshold</c> override.
/// </param>
public sealed record RelevanceModelManifestEntry(
    string ModelId,
    Uri ModelUrl,
    Uri TokenizerUrl,
    string ModelSha256,
    string TokenizerSha256,
    long ModelByteSize,
    double CalibratedThreshold);

/// <summary>Files placed on disk by <see cref="EmbeddingModelProvisioner.ProvisionRelevanceModelAsync"/>, ready for <c>OnnxCrossEncoderScorer.LoadAsync</c>.</summary>
public sealed record ProvisionedRelevanceModel(string ModelId, string ModelPath, string VocabPath, double CalibratedThreshold);

/// <summary>
/// Thrown when a requested model id is not on the allowlist, or a downloaded artifact fails
/// byte-size or SHA-256 verification. Never wraps a partially-written file — callers can treat
/// this as "nothing was provisioned."
/// </summary>
public sealed class EmbeddingModelProvisioningException(string message) : Exception(message);

/// <summary>
/// Downloads and verifies embedding model artifacts against a pinned in-code allowlist
/// (memory-core-redesign D2) — a supply-chain boundary. Arbitrary model URLs are rejected by
/// construction: there is no code path that accepts a caller-supplied URL, only a caller-
/// supplied <see cref="EmbeddingModelManifestEntry.ModelId"/> looked up in
/// <see cref="Allowlist"/>. This type performs no daemon wiring, no <see cref="OnnxMemoryEmbedder"/>
/// construction, and no warm-up inference — it only gets verified files onto disk.
/// </summary>
public sealed class EmbeddingModelProvisioner
{
    /// <summary>
    /// Pinned allowlist: model id → download locations, expected hashes, and dimensions.
    /// <c>snowflake-arctic-embed-m-int8</c> is the DEFAULT (<see cref="Netclaw.Configuration.MemoryEmbeddingsConfig.ModelId"/>);
    /// <c>snowflake-arctic-embed-m</c> (fp32) and <c>mxbai-embed-large-v1</c> remain allowlisted
    /// as explicit operator choices. The int8 entry is HuggingFace's static-quantized
    /// <c>onnx/model_uint8.onnx</c> export of the same fp32 weights (NOT <c>onnx/model_int8.onnx</c>
    /// or <c>onnx/model_quantized.onnx</c> — both exist in the same repo tree under the same byte
    /// size but a *different* SHA-256, a distinct dynamic-quantization export; only
    /// <c>model_uint8.onnx</c>'s hash matches what was calibrated). All URLs are pinned to a
    /// specific HuggingFace repo commit sha (not <c>main</c>) so the pinned hash can never
    /// silently drift out of sync with what the URL serves.
    /// </summary>
    public static IReadOnlyDictionary<string, EmbeddingModelManifestEntry> Allowlist { get; } =
        new Dictionary<string, EmbeddingModelManifestEntry>(StringComparer.Ordinal)
        {
            // Query prefix verified 2026-07-08 against the model card at the pinned HF commit
            // (memory-query-prefix design D2): "Represent this sentence for searching relevant
            // passages: " (trailing space is part of the documented string — the prefix and the
            // query text are meant to read as one sentence, not two concatenated with no
            // separator). CalibratedMinCosineSimilarity=0.24 is the gold-prod-2026-07 sweep
            // optimum for this prefixed encoding (design.md D4; supersedes the no-prefix 0.68
            // figure recorded in memory-core-redesign design.md D6). No longer the default model
            // (see snowflake-arctic-embed-m-int8 below) but remains allowlisted as an explicit,
            // higher-RAM/higher-latency choice.
            ["snowflake-arctic-embed-m"] = new EmbeddingModelManifestEntry(
                ModelId: "snowflake-arctic-embed-m",
                ModelUrl: new Uri("https://huggingface.co/Snowflake/snowflake-arctic-embed-m/resolve/fc74610d18462d218e312aa986ec5c8a75a98152/onnx/model.onnx"),
                TokenizerUrl: new Uri("https://huggingface.co/Snowflake/snowflake-arctic-embed-m/resolve/fc74610d18462d218e312aa986ec5c8a75a98152/vocab.txt"),
                ModelSha256: "564e6c65ee0c739a486702e9e3e9b33c3f697c19c34dbe886bce9eec497ce971",
                TokenizerSha256: "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3",
                Dimensions: 768,
                ModelByteSize: 435_811_541,
                QueryPrefix: "Represent this sentence for searching relevant passages: ",
                CalibratedMinCosineSimilarity: 0.24),

            // DEFAULT model (Memory.Embeddings.ModelId). Same tokenizer/vocab.txt as the fp32
            // entry above (shared across every variant HuggingFace publishes for this repo — hash
            // verified identical, 07eced37...038a3). ModelUrl is onnx/model_uint8.onnx at the SAME
            // pinned commit as the fp32 entry: verified 2026-07-08 against the HF tree API that
            // this exact path+hash+byte-size exists in Snowflake/snowflake-arctic-embed-m at
            // fc74610d18462d218e312aa986ec5c8a75a98152, and that it matches the locally-calibrated
            // artifact byte-for-byte (never pin a hash without confirming upstream serves it).
            // CalibratedMinCosineSimilarity=0.24 comes from a dedicated gold-prod-2026-07 +
            // repooled-test sweep with the SAME documented query prefix applied
            // (arctic-int8-prefix-eval, 2026-07-08) — measured BETTER than the fp32-with-prefix
            // entry above on every retrieval axis (F0.5 0.244 vs 0.239, recall@3 0.404 vs 0.318,
            // zero-injection accuracy 28.3% vs 26.7%), at ~1.7x the inference speed (~12ms vs
            // ~20ms p50 short-query on the reference box) and ~57% less steady-state embedder RSS
            // (261 MB vs 611 MB, memory-core-redesign design.md D6's quant-eval). This is a
            // strict improvement, not a size/quality tradeoff, which is why int8 — not fp32 — is
            // the default.
            ["snowflake-arctic-embed-m-int8"] = new EmbeddingModelManifestEntry(
                ModelId: "snowflake-arctic-embed-m-int8",
                ModelUrl: new Uri("https://huggingface.co/Snowflake/snowflake-arctic-embed-m/resolve/fc74610d18462d218e312aa986ec5c8a75a98152/onnx/model_uint8.onnx"),
                TokenizerUrl: new Uri("https://huggingface.co/Snowflake/snowflake-arctic-embed-m/resolve/fc74610d18462d218e312aa986ec5c8a75a98152/vocab.txt"),
                ModelSha256: "4cfc22160ddd52bac43697b6b84a4b29ea25a82db23841c27436dbddcfd5f88a",
                TokenizerSha256: "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3",
                Dimensions: 768,
                ModelByteSize: 110_084_023,
                QueryPrefix: "Represent this sentence for searching relevant passages: ",
                CalibratedMinCosineSimilarity: 0.24),

            // Query prefix verified 2026-07-08 against the model card (mixedbread-ai's usage
            // examples document the identical instruction string arctic-embed-m uses — both
            // cards converge on the same widely-used E5-style retrieval instruction; this is
            // NOT copy-paste drift, it is independently confirmed for this model's own card).
            // CalibratedMinCosineSimilarity is null: this fallback entry has not been through
            // the gold-set floor sweep, so it is deliberately uncalibrated — activating this
            // model with no explicit Memory.Recall.MinCosineSimilarity override degrades hybrid
            // recall to lexical-only (memory-query-prefix design D2/D3; see
            // SQLiteMemoryRecallCoordinator's missing-calibration degraded path) rather than
            // silently reusing a floor measured for a different model.
            ["mxbai-embed-large-v1"] = new EmbeddingModelManifestEntry(
                ModelId: "mxbai-embed-large-v1",
                ModelUrl: new Uri("https://huggingface.co/mixedbread-ai/mxbai-embed-large-v1/resolve/b33106f585b9ce46904ad7443a3b52b7a63e231c/onnx/model.onnx"),
                TokenizerUrl: new Uri("https://huggingface.co/mixedbread-ai/mxbai-embed-large-v1/resolve/b33106f585b9ce46904ad7443a3b52b7a63e231c/vocab.txt"),
                ModelSha256: "adb53ed475faa339bfad3bd2bdb7e6a30b4f47280ade9811f81bef7953f9ab77",
                TokenizerSha256: "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3",
                Dimensions: 1024,
                ModelByteSize: 1_336_854_282,
                QueryPrefix: "Represent this sentence for searching relevant passages: ",
                CalibratedMinCosineSimilarity: null),
        };

    /// <summary>
    /// Allowlist key for the single ratified relevance (cross-encoder) model
    /// (memory-relevance-gate D2). Unlike embeddings, there is no operator-facing model-choice
    /// knob for the relevance gate — the shoot-out ratified exactly one design/model pair, so
    /// this id is a fixed constant rather than a <c>Memory.Recall.RelevanceGate</c> config
    /// property.
    /// </summary>
    public const string DefaultRelevanceModelId = "ms-marco-minilm-l-6-v2";

    /// <summary>
    /// Pinned allowlist for relevance (cross-encoder) models — the same supply-chain mechanism
    /// as <see cref="Allowlist"/>, generalized to a manifest entry kind that additionally carries
    /// a calibrated operating threshold (memory-relevance-gate D2/D3). <c>Xenova/ms-marco-MiniLM-L-6-v2</c>
    /// is the winner of a 4-design measured shoot-out, re-validated out-of-sample (see
    /// <c>openspec/changes/memory-relevance-gate/design.md</c> D2): quantized int8,
    /// bit-for-bit quality-identical to the fp32 variant on both gold sets at a fraction of the
    /// RAM. URL is pinned to the repo's HEAD commit sha at the time this artifact was verified
    /// (not <c>main</c>), matching <see cref="Allowlist"/>'s own pinning convention.
    /// </summary>
    public static IReadOnlyDictionary<string, RelevanceModelManifestEntry> RelevanceAllowlist { get; } =
        new Dictionary<string, RelevanceModelManifestEntry>(StringComparer.Ordinal)
        {
            [DefaultRelevanceModelId] = new RelevanceModelManifestEntry(
                ModelId: DefaultRelevanceModelId,
                ModelUrl: new Uri("https://huggingface.co/Xenova/ms-marco-MiniLM-L-6-v2/resolve/a09144355adeed5f58c8ed011d209bf8ee5a1fec/onnx/model_quantized.onnx"),
                TokenizerUrl: new Uri("https://huggingface.co/Xenova/ms-marco-MiniLM-L-6-v2/resolve/a09144355adeed5f58c8ed011d209bf8ee5a1fec/vocab.txt"),
                ModelSha256: "e9d8ebf845c413e981c175bfe49a3bfa9b3dcce2a3ba54875ee5df5a58639fbe",
                TokenizerSha256: "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3",
                ModelByteSize: 23_143_499,
                CalibratedThreshold: 0.02),
        };

    private readonly HttpClient _httpClient;
    private readonly IReadOnlyDictionary<string, EmbeddingModelManifestEntry> _allowlist;

    /// <param name="httpClient">Used for all artifact downloads.</param>
    /// <param name="allowlist">
    /// The allowlist to resolve model ids against — an explicit, required dependency rather
    /// than always reading the static <see cref="Allowlist"/> internally, so tests can supply
    /// a small allowlist pointed at a local HTTP fixture instead of ever reaching the real
    /// HuggingFace URLs. Production wiring passes <see cref="Allowlist"/> itself.
    /// </param>
    public EmbeddingModelProvisioner(HttpClient httpClient, IReadOnlyDictionary<string, EmbeddingModelManifestEntry> allowlist)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(allowlist);
        _httpClient = httpClient;
        _allowlist = allowlist;
    }

    /// <summary>
    /// Downloads and verifies <paramref name="modelId"/>'s artifacts into
    /// <paramref name="destinationDirectory"/> as <c>model.onnx</c> and <c>vocab.txt</c>. Each
    /// download lands in a temp file first and is only renamed into place (atomic on the same
    /// filesystem) after its SHA-256 (and, for the model file, byte size) matches the allowlist
    /// entry — a hash mismatch discards the temp file and throws
    /// <see cref="EmbeddingModelProvisioningException"/> without ever creating or replacing the
    /// destination file.
    ///
    /// <para>
    /// When both destination files already exist and hash-verify against the allowlist entry,
    /// this method returns immediately without any network access (memory-core-redesign task
    /// 2.7: "already-provisioned+hash-valid loads without network"). This makes repeated calls
    /// — e.g. the daemon's warmup service running on every restart — idempotent and safe to run
    /// with <c>AutoDownload=false</c> once a model has been provisioned at least once.
    /// </para>
    /// </summary>
    public async Task<ProvisionedEmbeddingModel> ProvisionAsync(
        string modelId,
        string destinationDirectory,
        CancellationToken ct = default)
    {
        if (!_allowlist.TryGetValue(modelId, out var entry))
        {
            throw new EmbeddingModelProvisioningException(
                $"Unknown embedding model id '{modelId}'. Allowlisted ids: {string.Join(", ", _allowlist.Keys.Order(StringComparer.Ordinal))}.");
        }

        Directory.CreateDirectory(destinationDirectory);
        var modelPath = Path.Combine(destinationDirectory, "model.onnx");
        var vocabPath = Path.Combine(destinationDirectory, "vocab.txt");

        if (await IsValidAsync(modelPath, entry.ModelSha256, entry.ModelByteSize, ct).ConfigureAwait(false)
            && await IsValidAsync(vocabPath, entry.TokenizerSha256, expectedByteSize: null, ct).ConfigureAwait(false))
        {
            return new ProvisionedEmbeddingModel(modelId, modelPath, vocabPath, entry.Dimensions, entry.QueryPrefix, entry.CalibratedMinCosineSimilarity);
        }

        await DownloadAndVerifyAsync(entry.ModelUrl, modelPath, entry.ModelSha256, entry.ModelByteSize, ct).ConfigureAwait(false);
        await DownloadAndVerifyAsync(entry.TokenizerUrl, vocabPath, entry.TokenizerSha256, expectedByteSize: null, ct).ConfigureAwait(false);

        return new ProvisionedEmbeddingModel(modelId, modelPath, vocabPath, entry.Dimensions, entry.QueryPrefix, entry.CalibratedMinCosineSimilarity);
    }

    /// <summary>
    /// Verifies whether <paramref name="modelId"/>'s artifacts are already present and
    /// hash-valid at <paramref name="destinationDirectory"/>, without ever accessing the
    /// network. Returns null when the model id is unknown to the allowlist, or either file is
    /// missing or fails verification (including a corrupted local copy) — callers that must
    /// never trigger a download use this instead of <see cref="ProvisionAsync"/>
    /// (memory-core-redesign task 2.7: <c>Memory.Embeddings.AutoDownload=false</c> gates the
    /// network path entirely, even to repair a bad local copy).
    /// </summary>
    public async Task<ProvisionedEmbeddingModel?> TryLoadVerifiedAsync(
        string modelId,
        string destinationDirectory,
        CancellationToken ct = default)
    {
        if (!_allowlist.TryGetValue(modelId, out var entry))
            return null;

        var modelPath = Path.Combine(destinationDirectory, "model.onnx");
        var vocabPath = Path.Combine(destinationDirectory, "vocab.txt");

        if (!await IsValidAsync(modelPath, entry.ModelSha256, entry.ModelByteSize, ct).ConfigureAwait(false))
            return null;
        if (!await IsValidAsync(vocabPath, entry.TokenizerSha256, expectedByteSize: null, ct).ConfigureAwait(false))
            return null;

        return new ProvisionedEmbeddingModel(modelId, modelPath, vocabPath, entry.Dimensions, entry.QueryPrefix, entry.CalibratedMinCosineSimilarity);
    }

    /// <summary>
    /// Downloads and verifies <paramref name="modelId"/>'s relevance-model artifacts (memory-
    /// relevance-gate D3) — identical download/atomic-rename/hash-verify code path as
    /// <see cref="ProvisionAsync"/>, reused unchanged; only the manifest entry type differs.
    /// The allowlist is a method parameter rather than a constructor-injected field (unlike
    /// <see cref="_allowlist"/>) so this and <see cref="TryLoadVerifiedRelevanceModelAsync"/> can
    /// be added without perturbing every existing embedding-only call site's constructor call —
    /// callers pass <see cref="RelevanceAllowlist"/> in production, or a small fixture-pointed
    /// dictionary in tests.
    /// </summary>
    public async Task<ProvisionedRelevanceModel> ProvisionRelevanceModelAsync(
        string modelId,
        IReadOnlyDictionary<string, RelevanceModelManifestEntry> allowlist,
        string destinationDirectory,
        CancellationToken ct = default)
    {
        if (!allowlist.TryGetValue(modelId, out var entry))
        {
            throw new EmbeddingModelProvisioningException(
                $"Unknown relevance model id '{modelId}'. Allowlisted ids: {string.Join(", ", allowlist.Keys.Order(StringComparer.Ordinal))}.");
        }

        Directory.CreateDirectory(destinationDirectory);
        var modelPath = Path.Combine(destinationDirectory, "model.onnx");
        var vocabPath = Path.Combine(destinationDirectory, "vocab.txt");

        if (await IsValidAsync(modelPath, entry.ModelSha256, entry.ModelByteSize, ct).ConfigureAwait(false)
            && await IsValidAsync(vocabPath, entry.TokenizerSha256, expectedByteSize: null, ct).ConfigureAwait(false))
        {
            return new ProvisionedRelevanceModel(modelId, modelPath, vocabPath, entry.CalibratedThreshold);
        }

        await DownloadAndVerifyAsync(entry.ModelUrl, modelPath, entry.ModelSha256, entry.ModelByteSize, ct).ConfigureAwait(false);
        await DownloadAndVerifyAsync(entry.TokenizerUrl, vocabPath, entry.TokenizerSha256, expectedByteSize: null, ct).ConfigureAwait(false);

        return new ProvisionedRelevanceModel(modelId, modelPath, vocabPath, entry.CalibratedThreshold);
    }

    /// <summary>
    /// Verifies whether <paramref name="modelId"/>'s relevance-model artifacts are already
    /// present and hash-valid at <paramref name="destinationDirectory"/>, without ever accessing
    /// the network — the relevance-model analogue of <see cref="TryLoadVerifiedAsync"/>, used
    /// when <c>Memory.Embeddings.AutoDownload=false</c> gates the network path entirely.
    /// </summary>
    public async Task<ProvisionedRelevanceModel?> TryLoadVerifiedRelevanceModelAsync(
        string modelId,
        IReadOnlyDictionary<string, RelevanceModelManifestEntry> allowlist,
        string destinationDirectory,
        CancellationToken ct = default)
    {
        if (!allowlist.TryGetValue(modelId, out var entry))
            return null;

        var modelPath = Path.Combine(destinationDirectory, "model.onnx");
        var vocabPath = Path.Combine(destinationDirectory, "vocab.txt");

        if (!await IsValidAsync(modelPath, entry.ModelSha256, entry.ModelByteSize, ct).ConfigureAwait(false))
            return null;
        if (!await IsValidAsync(vocabPath, entry.TokenizerSha256, expectedByteSize: null, ct).ConfigureAwait(false))
            return null;

        return new ProvisionedRelevanceModel(modelId, modelPath, vocabPath, entry.CalibratedThreshold);
    }

    private static async Task<bool> IsValidAsync(string path, string expectedSha256, long? expectedByteSize, CancellationToken ct)
    {
        if (!File.Exists(path))
            return false;

        if (expectedByteSize is { } expected && new FileInfo(path).Length != expected)
            return false;

        var actualSha256 = await ComputeSha256Async(path, ct).ConfigureAwait(false);
        return string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private async Task DownloadAndVerifyAsync(
        Uri source,
        string destinationPath,
        string expectedSha256,
        long? expectedByteSize,
        CancellationToken ct)
    {
        var tempPath = $"{destinationPath}.tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var responseStream = await _httpClient.GetStreamAsync(source, ct).ConfigureAwait(false))
            await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await responseStream.CopyToAsync(fileStream, ct).ConfigureAwait(false);
            }

            // Cheap fail-fast before hashing a potentially large file: a truncated or swapped
            // artifact almost always has the wrong size.
            var actualByteSize = new FileInfo(tempPath).Length;
            if (expectedByteSize is { } expected && actualByteSize != expected)
            {
                throw new EmbeddingModelProvisioningException(
                    $"Downloaded artifact from {source} is {actualByteSize} bytes; the allowlist for this entry expects {expected} bytes. " +
                    "Discarding — this is a supply-chain integrity boundary, never loaded.");
            }

            var actualSha256 = await ComputeSha256Async(tempPath, ct).ConfigureAwait(false);
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new EmbeddingModelProvisioningException(
                    $"Downloaded artifact from {source} does not match the pinned SHA-256 (expected {expectedSha256}, got {actualSha256}). " +
                    "Discarding — this is a supply-chain integrity boundary, never loaded.");
            }

            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            // No-op once Move above has succeeded (the file no longer exists at tempPath);
            // cleans up the partial download on any failure path, including a hash/size
            // mismatch or a cancelled/faulted copy.
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}

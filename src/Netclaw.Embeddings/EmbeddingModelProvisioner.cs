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
public sealed record EmbeddingModelManifestEntry(
    string ModelId,
    Uri ModelUrl,
    Uri TokenizerUrl,
    string ModelSha256,
    string TokenizerSha256,
    int Dimensions,
    long ModelByteSize);

/// <summary>Files placed on disk by <see cref="EmbeddingModelProvisioner.ProvisionAsync"/>, ready for <see cref="OnnxMemoryEmbedder.LoadAsync"/>.</summary>
public sealed record ProvisionedEmbeddingModel(string ModelId, string ModelPath, string VocabPath, int Dimensions);

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
    /// Primary is <c>snowflake-arctic-embed-m</c> (May-2026-ratified nominator model);
    /// <c>mxbai-embed-large-v1</c> is the allowlisted fallback. Both entries point at the
    /// plain fp32 <c>onnx/model.onnx</c> artifact (not the int8/fp16/quantized variants also
    /// published on HuggingFace) for correctness; a quantized variant is a future optimization,
    /// not this stage's concern. URLs are pinned to a specific HuggingFace repo commit sha
    /// (not <c>main</c>) so the pinned hash can never silently drift out of sync with what the
    /// URL serves.
    /// </summary>
    public static IReadOnlyDictionary<string, EmbeddingModelManifestEntry> Allowlist { get; } =
        new Dictionary<string, EmbeddingModelManifestEntry>(StringComparer.Ordinal)
        {
            ["snowflake-arctic-embed-m"] = new EmbeddingModelManifestEntry(
                ModelId: "snowflake-arctic-embed-m",
                ModelUrl: new Uri("https://huggingface.co/Snowflake/snowflake-arctic-embed-m/resolve/fc74610d18462d218e312aa986ec5c8a75a98152/onnx/model.onnx"),
                TokenizerUrl: new Uri("https://huggingface.co/Snowflake/snowflake-arctic-embed-m/resolve/fc74610d18462d218e312aa986ec5c8a75a98152/vocab.txt"),
                ModelSha256: "564e6c65ee0c739a486702e9e3e9b33c3f697c19c34dbe886bce9eec497ce971",
                TokenizerSha256: "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3",
                Dimensions: 768,
                ModelByteSize: 435_811_541),

            ["mxbai-embed-large-v1"] = new EmbeddingModelManifestEntry(
                ModelId: "mxbai-embed-large-v1",
                ModelUrl: new Uri("https://huggingface.co/mixedbread-ai/mxbai-embed-large-v1/resolve/b33106f585b9ce46904ad7443a3b52b7a63e231c/onnx/model.onnx"),
                TokenizerUrl: new Uri("https://huggingface.co/mixedbread-ai/mxbai-embed-large-v1/resolve/b33106f585b9ce46904ad7443a3b52b7a63e231c/vocab.txt"),
                ModelSha256: "adb53ed475faa339bfad3bd2bdb7e6a30b4f47280ade9811f81bef7953f9ab77",
                TokenizerSha256: "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3",
                Dimensions: 1024,
                ModelByteSize: 1_336_854_282),
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
            return new ProvisionedEmbeddingModel(modelId, modelPath, vocabPath, entry.Dimensions);
        }

        await DownloadAndVerifyAsync(entry.ModelUrl, modelPath, entry.ModelSha256, entry.ModelByteSize, ct).ConfigureAwait(false);
        await DownloadAndVerifyAsync(entry.TokenizerUrl, vocabPath, entry.TokenizerSha256, expectedByteSize: null, ct).ConfigureAwait(false);

        return new ProvisionedEmbeddingModel(modelId, modelPath, vocabPath, entry.Dimensions);
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

        return new ProvisionedEmbeddingModel(modelId, modelPath, vocabPath, entry.Dimensions);
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

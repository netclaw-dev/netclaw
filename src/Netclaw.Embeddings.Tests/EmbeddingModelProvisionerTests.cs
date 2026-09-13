// -----------------------------------------------------------------------
// <copyright file="EmbeddingModelProvisionerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Netclaw.Embeddings.Tests;

/// <summary>
/// Exercises <see cref="EmbeddingModelProvisioner"/> against a local
/// <see cref="LocalArtifactServer"/> fixture — no network access, and never touches the real
/// production <see cref="EmbeddingModelProvisioner.Allowlist"/> (tests build their own small
/// allowlist pointed at the local server, since the allowlist is an injected, required
/// dependency rather than a hardcoded internal).
/// </summary>
public sealed class EmbeddingModelProvisionerTests : IAsyncLifetime
{
    private LocalArtifactServer _server = null!;
    private HttpClient _httpClient = null!;
    private string _destinationDirectory = null!;

    public ValueTask InitializeAsync()
    {
        _server = new LocalArtifactServer();
        _httpClient = new HttpClient();
        _destinationDirectory = Path.Combine(Path.GetTempPath(), "netclaw-embedding-provisioner-tests", Guid.NewGuid().ToString("N"));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        await _server.DisposeAsync();
        if (Directory.Exists(_destinationDirectory))
            Directory.Delete(_destinationDirectory, recursive: true);
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Fact]
    public async Task ProvisionAsync_downloads_and_verifies_matching_artifacts()
    {
        var modelBytes = Encoding.UTF8.GetBytes("fake-onnx-model-bytes");
        var vocabBytes = Encoding.UTF8.GetBytes("[PAD]\n[UNK]\n[CLS]\n[SEP]\n");

        var modelUrl = _server.AddRoute("/model.onnx", modelBytes);
        var vocabUrl = _server.AddRoute("/vocab.txt", vocabBytes);

        var allowlist = new Dictionary<string, EmbeddingModelManifestEntry>
        {
            ["test-model"] = new EmbeddingModelManifestEntry(
                "test-model", modelUrl, vocabUrl,
                Sha256Hex(modelBytes), Sha256Hex(vocabBytes),
                Dimensions: 8, ModelByteSize: modelBytes.Length,
                QueryPrefix: "", CalibratedMinCosineSimilarity: null),
        };

        var provisioner = new EmbeddingModelProvisioner(_httpClient, allowlist);
        var result = await provisioner.ProvisionAsync("test-model", _destinationDirectory, TestContext.Current.CancellationToken);

        Assert.Equal("test-model", result.ModelId);
        Assert.Equal(8, result.Dimensions);
        Assert.Equal(modelBytes, await File.ReadAllBytesAsync(result.ModelPath, TestContext.Current.CancellationToken));
        Assert.Equal(vocabBytes, await File.ReadAllBytesAsync(result.VocabPath, TestContext.Current.CancellationToken));

        // Nothing but the two final artifacts remains — no leftover temp files.
        var leftoverFiles = Directory.GetFiles(_destinationDirectory).Select(Path.GetFileName).ToArray();
        Assert.Equal(["model.onnx", "vocab.txt"], leftoverFiles.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task ProvisionAsync_skips_the_network_entirely_when_a_valid_local_copy_already_exists()
    {
        var modelBytes = Encoding.UTF8.GetBytes("fake-onnx-model-bytes");
        var vocabBytes = Encoding.UTF8.GetBytes("[PAD]\n[UNK]\n[CLS]\n[SEP]\n");

        var modelUrl = _server.AddRoute("/model.onnx", modelBytes);
        var vocabUrl = _server.AddRoute("/vocab.txt", vocabBytes);

        var allowlist = new Dictionary<string, EmbeddingModelManifestEntry>
        {
            ["test-model"] = new EmbeddingModelManifestEntry(
                "test-model", modelUrl, vocabUrl,
                Sha256Hex(modelBytes), Sha256Hex(vocabBytes),
                Dimensions: 8, ModelByteSize: modelBytes.Length,
                QueryPrefix: "", CalibratedMinCosineSimilarity: null),
        };
        var provisioner = new EmbeddingModelProvisioner(_httpClient, allowlist);
        await provisioner.ProvisionAsync("test-model", _destinationDirectory, TestContext.Current.CancellationToken);

        // Tear down the server: any further attempt to reach the network would now throw.
        await _server.DisposeAsync();

        // Task 2.7: "already-provisioned+hash-valid loads without network" — this call must
        // succeed even though the server is gone, proving it never re-downloaded.
        var result = await provisioner.ProvisionAsync("test-model", _destinationDirectory, TestContext.Current.CancellationToken);

        Assert.Equal("test-model", result.ModelId);
        Assert.Equal(modelBytes, await File.ReadAllBytesAsync(result.ModelPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TryLoadVerifiedAsync_returns_null_when_no_local_copy_exists()
    {
        var allowlist = new Dictionary<string, EmbeddingModelManifestEntry>
        {
            ["test-model"] = DummyEntry("test-model"),
        };
        var provisioner = new EmbeddingModelProvisioner(_httpClient, allowlist);

        var result = await provisioner.TryLoadVerifiedAsync("test-model", _destinationDirectory, TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryLoadVerifiedAsync_returns_null_for_an_unknown_model_id_without_touching_the_network()
    {
        var provisioner = new EmbeddingModelProvisioner(_httpClient, new Dictionary<string, EmbeddingModelManifestEntry>());

        var result = await provisioner.TryLoadVerifiedAsync("nonexistent-model", _destinationDirectory, TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryLoadVerifiedAsync_returns_the_provisioned_model_without_network_when_the_local_copy_is_valid()
    {
        var modelBytes = Encoding.UTF8.GetBytes("fake-onnx-model-bytes");
        var vocabBytes = Encoding.UTF8.GetBytes("[PAD]\n[UNK]\n[CLS]\n[SEP]\n");
        var modelUrl = _server.AddRoute("/model.onnx", modelBytes);
        var vocabUrl = _server.AddRoute("/vocab.txt", vocabBytes);

        var allowlist = new Dictionary<string, EmbeddingModelManifestEntry>
        {
            ["test-model"] = new EmbeddingModelManifestEntry(
                "test-model", modelUrl, vocabUrl,
                Sha256Hex(modelBytes), Sha256Hex(vocabBytes),
                Dimensions: 8, ModelByteSize: modelBytes.Length,
                QueryPrefix: "", CalibratedMinCosineSimilarity: null),
        };
        var provisioner = new EmbeddingModelProvisioner(_httpClient, allowlist);
        await provisioner.ProvisionAsync("test-model", _destinationDirectory, TestContext.Current.CancellationToken);
        await _server.DisposeAsync();

        var result = await provisioner.TryLoadVerifiedAsync("test-model", _destinationDirectory, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(8, result!.Dimensions);
    }

    [Fact]
    public async Task ProvisionAsync_rejects_unknown_model_id_listing_the_allowlist()
    {
        var allowlist = new Dictionary<string, EmbeddingModelManifestEntry>
        {
            ["known-a"] = DummyEntry("known-a"),
            ["known-b"] = DummyEntry("known-b"),
        };
        var provisioner = new EmbeddingModelProvisioner(_httpClient, allowlist);

        var ex = await Assert.ThrowsAsync<EmbeddingModelProvisioningException>(
            () => provisioner.ProvisionAsync("nonexistent-model", _destinationDirectory, TestContext.Current.CancellationToken));

        Assert.Contains("nonexistent-model", ex.Message, StringComparison.Ordinal);
        Assert.Contains("known-a", ex.Message, StringComparison.Ordinal);
        Assert.Contains("known-b", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProvisionAsync_rejects_sha256_mismatch_and_leaves_nothing_behind()
    {
        var modelBytes = Encoding.UTF8.GetBytes("real-content");
        var vocabBytes = Encoding.UTF8.GetBytes("vocab-content");
        var modelUrl = _server.AddRoute("/model.onnx", modelBytes);
        var vocabUrl = _server.AddRoute("/vocab.txt", vocabBytes);

        var allowlist = new Dictionary<string, EmbeddingModelManifestEntry>
        {
            ["tampered"] = new EmbeddingModelManifestEntry(
                "tampered", modelUrl, vocabUrl,
                ModelSha256: Sha256Hex(Encoding.UTF8.GetBytes("this-does-not-match-the-served-bytes")),
                TokenizerSha256: Sha256Hex(vocabBytes),
                Dimensions: 8, ModelByteSize: modelBytes.Length,
                QueryPrefix: "", CalibratedMinCosineSimilarity: null),
        };

        var provisioner = new EmbeddingModelProvisioner(_httpClient, allowlist);

        var ex = await Assert.ThrowsAsync<EmbeddingModelProvisioningException>(
            () => provisioner.ProvisionAsync("tampered", _destinationDirectory, TestContext.Current.CancellationToken));

        Assert.Contains("SHA-256", ex.Message, StringComparison.Ordinal);

        // The artifact was discarded, not loaded — no final file and no leftover temp file.
        if (Directory.Exists(_destinationDirectory))
            Assert.Empty(Directory.GetFiles(_destinationDirectory));
    }

    [Fact]
    public async Task ProvisionAsync_rejects_byte_size_mismatch_before_hashing()
    {
        var modelBytes = Encoding.UTF8.GetBytes("some content of a certain length");
        var vocabBytes = Encoding.UTF8.GetBytes("vocab");
        var modelUrl = _server.AddRoute("/model.onnx", modelBytes);
        var vocabUrl = _server.AddRoute("/vocab.txt", vocabBytes);

        var allowlist = new Dictionary<string, EmbeddingModelManifestEntry>
        {
            ["wrong-size"] = new EmbeddingModelManifestEntry(
                "wrong-size", modelUrl, vocabUrl,
                Sha256Hex(modelBytes), Sha256Hex(vocabBytes),
                Dimensions: 8, ModelByteSize: modelBytes.Length + 1,
                QueryPrefix: "", CalibratedMinCosineSimilarity: null),
        };

        var provisioner = new EmbeddingModelProvisioner(_httpClient, allowlist);

        var ex = await Assert.ThrowsAsync<EmbeddingModelProvisioningException>(
            () => provisioner.ProvisionAsync("wrong-size", _destinationDirectory, TestContext.Current.CancellationToken));

        Assert.Contains("bytes", ex.Message, StringComparison.Ordinal);
        if (Directory.Exists(_destinationDirectory))
            Assert.Empty(Directory.GetFiles(_destinationDirectory));
    }

    [Fact]
    public void ProductionAllowlist_has_the_three_ratified_models_with_distinct_ids()
    {
        Assert.True(EmbeddingModelProvisioner.Allowlist.ContainsKey("snowflake-arctic-embed-m"));
        Assert.True(EmbeddingModelProvisioner.Allowlist.ContainsKey("snowflake-arctic-embed-m-int8"));
        Assert.True(EmbeddingModelProvisioner.Allowlist.ContainsKey("mxbai-embed-large-v1"));
        Assert.Equal(768, EmbeddingModelProvisioner.Allowlist["snowflake-arctic-embed-m"].Dimensions);
        Assert.Equal(768, EmbeddingModelProvisioner.Allowlist["snowflake-arctic-embed-m-int8"].Dimensions);
        Assert.Equal(1024, EmbeddingModelProvisioner.Allowlist["mxbai-embed-large-v1"].Dimensions);
        Assert.All(EmbeddingModelProvisioner.Allowlist.Values, e => Assert.Equal(64, e.ModelSha256.Length));
        Assert.All(EmbeddingModelProvisioner.Allowlist.Values, e => Assert.Equal(64, e.TokenizerSha256.Length));
    }

    // ── Retrieval-mode metadata (memory-query-prefix design D2/D4) ──────

    [Fact]
    public void ArcticEntry_carries_the_model_card_query_prefix_verbatim_and_its_calibrated_floor()
    {
        // Pins the exact model-card string (design.md D2: verified 2026-07-08 against the
        // pinned HF commit) — a future model bump forces the author past this assertion too,
        // so a stale prefix silently paired with new weights fails loudly here instead of only
        // degrading retrieval quality at runtime.
        var entry = EmbeddingModelProvisioner.Allowlist["snowflake-arctic-embed-m"];
        Assert.Equal("Represent this sentence for searching relevant passages: ", entry.QueryPrefix);
        Assert.Equal(0.24, entry.CalibratedMinCosineSimilarity);
    }

    [Fact]
    public void ArcticInt8Entry_is_the_default_model_pinned_to_the_uint8_artifact_with_its_own_calibrated_floor()
    {
        // Pins the exact artifact this repo's default now loads: onnx/model_uint8.onnx (NOT
        // onnx/model_int8.onnx or onnx/model_quantized.onnx — both exist in the same upstream
        // repo tree at the same byte size but a DIFFERENT sha256, a distinct dynamic-quantization
        // export; only model_uint8.onnx's hash matches the artifact that was actually
        // calibrated). A future re-pin that silently swapped in either sibling file would fail
        // this hash assertion instead of only degrading retrieval quality at runtime.
        var entry = EmbeddingModelProvisioner.Allowlist["snowflake-arctic-embed-m-int8"];
        Assert.Equal("snowflake-arctic-embed-m-int8", entry.ModelId);
        Assert.Equal(768, entry.Dimensions);
        Assert.Equal(110_084_023, entry.ModelByteSize);
        Assert.Equal("4cfc22160ddd52bac43697b6b84a4b29ea25a82db23841c27436dbddcfd5f88a", entry.ModelSha256, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("model_uint8.onnx", entry.ModelUrl.ToString(), StringComparison.Ordinal);
        Assert.Equal("Represent this sentence for searching relevant passages: ", entry.QueryPrefix);
        Assert.Equal(0.24, entry.CalibratedMinCosineSimilarity);

        // Tokenizer is genuinely shared with the fp32 entry (same HF commit, same vocab.txt) —
        // not merely coincidentally equal.
        var fp32Entry = EmbeddingModelProvisioner.Allowlist["snowflake-arctic-embed-m"];
        Assert.Equal(fp32Entry.TokenizerSha256, entry.TokenizerSha256, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(fp32Entry.TokenizerUrl, entry.TokenizerUrl);
    }

    [Fact]
    public void MxbaiFallbackEntry_carries_a_query_prefix_but_no_retrieval_calibration()
    {
        // The fallback entry has not been through its own gold-set floor sweep (design D2): its
        // CalibratedMinCosineSimilarity MUST stay null until that calibration lands, so
        // SQLiteMemoryRecallCoordinator degrades to lexical-only rather than silently reusing a
        // floor measured for a different model.
        var entry = EmbeddingModelProvisioner.Allowlist["mxbai-embed-large-v1"];
        Assert.False(string.IsNullOrEmpty(entry.QueryPrefix));
        Assert.Null(entry.CalibratedMinCosineSimilarity);
    }

    private static EmbeddingModelManifestEntry DummyEntry(string id)
        => new(id, new Uri("http://127.0.0.1:1/model.onnx"), new Uri("http://127.0.0.1:1/vocab.txt"), new string('0', 64), new string('0', 64), 8, 1, QueryPrefix: "", CalibratedMinCosineSimilarity: null);
}

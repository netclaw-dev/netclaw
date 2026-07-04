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

    public ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        _server.Dispose();
        if (Directory.Exists(_destinationDirectory))
            Directory.Delete(_destinationDirectory, recursive: true);
        return ValueTask.CompletedTask;
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
                Dimensions: 8, ModelByteSize: modelBytes.Length),
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
                Dimensions: 8, ModelByteSize: modelBytes.Length),
        };
        var provisioner = new EmbeddingModelProvisioner(_httpClient, allowlist);
        await provisioner.ProvisionAsync("test-model", _destinationDirectory, TestContext.Current.CancellationToken);

        // Tear down the server: any further attempt to reach the network would now throw.
        _server.Dispose();

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
                Dimensions: 8, ModelByteSize: modelBytes.Length),
        };
        var provisioner = new EmbeddingModelProvisioner(_httpClient, allowlist);
        await provisioner.ProvisionAsync("test-model", _destinationDirectory, TestContext.Current.CancellationToken);
        _server.Dispose();

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
                Dimensions: 8, ModelByteSize: modelBytes.Length),
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
                Dimensions: 8, ModelByteSize: modelBytes.Length + 1),
        };

        var provisioner = new EmbeddingModelProvisioner(_httpClient, allowlist);

        var ex = await Assert.ThrowsAsync<EmbeddingModelProvisioningException>(
            () => provisioner.ProvisionAsync("wrong-size", _destinationDirectory, TestContext.Current.CancellationToken));

        Assert.Contains("bytes", ex.Message, StringComparison.Ordinal);
        if (Directory.Exists(_destinationDirectory))
            Assert.Empty(Directory.GetFiles(_destinationDirectory));
    }

    [Fact]
    public void ProductionAllowlist_has_the_two_ratified_models_with_distinct_ids()
    {
        Assert.True(EmbeddingModelProvisioner.Allowlist.ContainsKey("snowflake-arctic-embed-m"));
        Assert.True(EmbeddingModelProvisioner.Allowlist.ContainsKey("mxbai-embed-large-v1"));
        Assert.Equal(768, EmbeddingModelProvisioner.Allowlist["snowflake-arctic-embed-m"].Dimensions);
        Assert.Equal(1024, EmbeddingModelProvisioner.Allowlist["mxbai-embed-large-v1"].Dimensions);
        Assert.All(EmbeddingModelProvisioner.Allowlist.Values, e => Assert.Equal(64, e.ModelSha256.Length));
        Assert.All(EmbeddingModelProvisioner.Allowlist.Values, e => Assert.Equal(64, e.TokenizerSha256.Length));
    }

    private static EmbeddingModelManifestEntry DummyEntry(string id)
        => new(id, new Uri("http://127.0.0.1:1/model.onnx"), new Uri("http://127.0.0.1:1/vocab.txt"), new string('0', 64), new string('0', 64), 8, 1);
}

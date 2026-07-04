// -----------------------------------------------------------------------
// <copyright file="MemoryEmbeddingDoctorCheckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Netclaw.Actors.Memory;
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Netclaw.Embeddings;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

/// <summary>
/// Covers every severity branch of <see cref="MemoryEmbeddingDoctorCheck"/>
/// (memory-core-redesign spec: "Embedding coverage diagnostics"), using the tiny fixture ONNX
/// graph (linked from <c>Netclaw.Embeddings.Tests/Fixtures</c>) instead of the real allowlist —
/// no network access anywhere in these tests.
/// </summary>
public sealed class MemoryEmbeddingDoctorCheckTests
{
    private const string ModelId = "tiny-fixture";
    private static string FixturesDir => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    [Fact]
    public async Task Passes_with_embeddings_disabled_message_when_config_off()
    {
        var paths = CreateTempPaths();
        var config = WriteConfig(paths, enabled: false);
        var check = new MemoryEmbeddingDoctorCheck(paths, config, FixtureAllowlist());

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("disabled", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Errors_when_enabled_but_model_is_missing()
    {
        var paths = CreateTempPaths();
        var config = WriteConfig(paths, enabled: true);
        // No model files placed at paths.EmbeddingModelDirectory(ModelId).
        var check = new MemoryEmbeddingDoctorCheck(paths, config, FixtureAllowlist());

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains(ModelId, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Warns_when_items_lack_a_current_model_embedding()
    {
        var paths = CreateTempPaths();
        var config = WriteConfig(paths, enabled: true);
        PrePlaceValidModelFiles(paths);

        var store = new SQLiteMemoryStore(paths.MemorySqliteDbPath, TimeProvider.System);
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        await SeedDocumentAsync(store, "doc-unembedded", "Unembedded", "never embedded");

        var check = new MemoryEmbeddingDoctorCheck(paths, config, FixtureAllowlist());
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("lack a current-model embedding", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Warns_on_mixed_model_corpus()
    {
        var paths = CreateTempPaths();
        var config = WriteConfig(paths, enabled: true);
        PrePlaceValidModelFiles(paths);

        var store = new SQLiteMemoryStore(paths.MemorySqliteDbPath, TimeProvider.System);
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        await SeedDocumentAsync(store, "doc-1", "Doc", "body");
        var hash = MemoryContentHasher.ComputeHash("Doc", "body");
        await store.UpsertEmbeddingAsync("doc-1", "document", ModelId, hash, new float[] { 1f }, TestContext.Current.CancellationToken);
        await store.UpsertEmbeddingAsync("doc-1", "document", "some-other-model", hash, new float[] { 2f }, TestContext.Current.CancellationToken);

        var check = new MemoryEmbeddingDoctorCheck(paths, config, FixtureAllowlist());
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("another model id", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Passes_with_coverage_summary_when_fully_embedded()
    {
        var paths = CreateTempPaths();
        var config = WriteConfig(paths, enabled: true);
        PrePlaceValidModelFiles(paths);

        var store = new SQLiteMemoryStore(paths.MemorySqliteDbPath, TimeProvider.System);
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        await SeedDocumentAsync(store, "doc-1", "Doc", "body");
        var hash = MemoryContentHasher.ComputeHash("Doc", "body");
        await store.UpsertEmbeddingAsync("doc-1", "document", ModelId, hash, new float[] { 1f }, TestContext.Current.CancellationToken);

        var check = new MemoryEmbeddingDoctorCheck(paths, config, FixtureAllowlist());
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("healthy", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static NetclawPaths CreateTempPaths()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "netclaw-embedding-doctor-tests", Guid.NewGuid().ToString("N"));
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();
        return paths;
    }

    private static IConfiguration WriteConfig(NetclawPaths paths, bool enabled)
    {
        var config = new Dictionary<string, object>
        {
            ["Memory"] = new Dictionary<string, object>
            {
                ["Embeddings"] = new Dictionary<string, object>
                {
                    ["Enabled"] = enabled,
                    ["ModelId"] = ModelId,
                    ["AutoDownload"] = true,
                }
            }
        };

        File.WriteAllText(paths.NetclawConfigPath, JsonSerializer.Serialize(config));

        return new ConfigurationBuilder()
            .AddJsonFile(paths.NetclawConfigPath, optional: false)
            .Build();
    }

    private static void PrePlaceValidModelFiles(NetclawPaths paths)
    {
        var dir = paths.EmbeddingModelDirectory(ModelId);
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(FixturesDir, "tiny-embedder.onnx"), Path.Combine(dir, "model.onnx"), overwrite: true);
        File.Copy(Path.Combine(FixturesDir, "tiny-vocab.txt"), Path.Combine(dir, "vocab.txt"), overwrite: true);
    }

    private static async Task SeedDocumentAsync(SQLiteMemoryStore store, string id, string title, string body)
    {
        var anchor = store.CreateDefaultAnchor(id);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await store.UpsertDocumentAsync(new SQLiteMemoryDocument(
            DocumentId: id,
            Anchor: anchor,
            MemoryClass: "durable_fact",
            Title: title,
            MarkdownBody: body,
            AliasesJson: null,
            FacetsJson: null,
            SlotsJson: null,
            UpdateSemantics: "merge-document",
            Sensitivity: "normal",
            RecallMode: "auto",
            Confidence: 0.9,
            FreshnessAtMs: now,
            ExpiresAtMs: null,
            CreatedAtMs: now,
            UpdatedAtMs: now));
    }

    internal static IReadOnlyDictionary<string, EmbeddingModelManifestEntry> FixtureAllowlist()
    {
        var modelBytes = File.ReadAllBytes(Path.Combine(FixturesDir, "tiny-embedder.onnx"));
        var vocabBytes = File.ReadAllBytes(Path.Combine(FixturesDir, "tiny-vocab.txt"));

        return new Dictionary<string, EmbeddingModelManifestEntry>
        {
            [ModelId] = new(
                ModelId,
                ModelUrl: new Uri("http://127.0.0.1:1/unused-model.onnx"),
                TokenizerUrl: new Uri("http://127.0.0.1:1/unused-vocab.txt"),
                ModelSha256: Convert.ToHexStringLower(SHA256.HashData(modelBytes)),
                TokenizerSha256: Convert.ToHexStringLower(SHA256.HashData(vocabBytes)),
                Dimensions: 8,
                ModelByteSize: modelBytes.Length),
        };
    }
}

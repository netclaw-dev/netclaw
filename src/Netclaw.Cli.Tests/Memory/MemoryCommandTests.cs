// -----------------------------------------------------------------------
// <copyright file="MemoryCommandTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Netclaw.Actors.Memory;
using Netclaw.Cli.Memory;
using Netclaw.Configuration;
using Netclaw.Embeddings;
using Xunit;

namespace Netclaw.Cli.Tests.Memory;

/// <summary>
/// Covers the core loop of <c>netclaw memory backfill-embeddings</c>
/// (memory-core-redesign Slice 2, task 2.9): provisioning, embedding, and the final
/// embedded/skipped-hash-unchanged/failed summary. Uses the internal allowlist-injectable
/// overload of <see cref="MemoryCommand.RunAsync(string[], NetclawPaths, IConfiguration, System.Collections.Generic.IReadOnlyDictionary{string, EmbeddingModelManifestEntry}, TextWriter, TextWriter)"/>
/// pointed at the tiny fixture ONNX graph — no network access.
/// </summary>
public sealed class MemoryCommandTests
{
    private const string ModelId = "tiny-fixture";
    private static string FixturesDir => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    [Fact]
    public async Task BackfillEmbeddings_embeds_missing_documents_and_reports_a_summary()
    {
        var paths = CreateTempPaths(prePlaceValidModel: true);
        var config = BuildConfig(autoDownload: true);

        var store = new SQLiteMemoryStore(paths.MemorySqliteDbPath, TimeProvider.System);
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        await SeedDocumentAsync(store, "doc-1", "Doc One", "first body");
        await SeedDocumentAsync(store, "doc-2", "Doc Two", "second body");

        var (exitCode, stdout) = await RunCapturedAsync(["memory", "backfill-embeddings"], paths, config);

        Assert.Equal(0, exitCode);
        Assert.Contains("embedded=2 skipped-hash-unchanged=0 failed=0", stdout);

        var rows = await store.GetEmbeddingsForModelAsync(ModelId, TestContext.Current.CancellationToken);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task BackfillEmbeddings_is_a_no_op_when_nothing_is_missing()
    {
        var paths = CreateTempPaths(prePlaceValidModel: true);
        var config = BuildConfig(autoDownload: true);

        var store = new SQLiteMemoryStore(paths.MemorySqliteDbPath, TimeProvider.System);
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        await SeedDocumentAsync(store, "doc-1", "Doc One", "first body");
        var hash = MemoryContentHasher.ComputeHash("Doc One", "first body");
        await store.UpsertEmbeddingAsync("doc-1", "document", ModelId, hash, new float[] { 1f }, TestContext.Current.CancellationToken);

        var (exitCode, stdout) = await RunCapturedAsync(["memory", "backfill-embeddings"], paths, config);

        Assert.Equal(0, exitCode);
        Assert.Contains("Nothing to backfill", stdout);
    }

    [Fact]
    public async Task BackfillEmbeddings_fails_clearly_when_autodownload_is_false_and_model_is_missing()
    {
        var paths = CreateTempPaths(prePlaceValidModel: false);
        // AutoDownload=false and no pre-placed model files: the CLI must refuse, not download.
        var config = BuildConfig(autoDownload: false);

        var (exitCode, _, stderr) = await RunCapturedWithStderrAsync(["memory", "backfill-embeddings"], paths, config);

        Assert.Equal(1, exitCode);
        Assert.Contains("AutoDownload", stderr);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    public async Task BackfillEmbeddings_help_flag_prints_help_and_does_not_execute(string helpToken)
    {
        // Canary regression: `netclaw memory backfill-embeddings --help` was executing the real
        // provision-and-embed run (downloading models, writing embeddings) instead of printing
        // help, because only args[1] (the subcommand slot) was checked for a help token. Prove
        // the fix by seeding a document that WOULD be embedded if the command ran for real (as
        // in BackfillEmbeddings_embeds_missing_documents_and_reports_a_summary above) and
        // asserting nothing was written.
        var paths = CreateTempPaths(prePlaceValidModel: true);
        var config = BuildConfig(autoDownload: true);

        var store = new SQLiteMemoryStore(paths.MemorySqliteDbPath, TimeProvider.System);
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        await SeedDocumentAsync(store, "doc-1", "Doc One", "first body");

        var (exitCode, stdout) = await RunCapturedAsync(["memory", "backfill-embeddings", helpToken], paths, config);

        Assert.Equal(0, exitCode);
        Assert.Contains("Usage: netclaw memory <subcommand>", stdout);
        Assert.DoesNotContain("Embedding", stdout);

        var rows = await store.GetEmbeddingsForModelAsync(ModelId, TestContext.Current.CancellationToken);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task TopLevelHelp_still_prints_help()
    {
        var paths = CreateTempPaths(prePlaceValidModel: false);
        var config = BuildConfig(autoDownload: false);

        var (exitCode, stdout) = await RunCapturedAsync(["memory", "--help"], paths, config);

        Assert.Equal(0, exitCode);
        Assert.Contains("Usage: netclaw memory <subcommand>", stdout);
    }

    [Fact]
    public async Task BackfillEmbeddings_with_force_re_embeds_every_recallable_document()
    {
        var paths = CreateTempPaths(prePlaceValidModel: true);
        var config = BuildConfig(autoDownload: true);

        var store = new SQLiteMemoryStore(paths.MemorySqliteDbPath, TimeProvider.System);
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        await SeedDocumentAsync(store, "doc-1", "Doc One", "first body");
        var hash = MemoryContentHasher.ComputeHash("Doc One", "first body");
        await store.UpsertEmbeddingAsync("doc-1", "document", ModelId, hash, new float[] { 1f }, TestContext.Current.CancellationToken);

        var (exitCode, stdout) = await RunCapturedAsync(["memory", "backfill-embeddings", "--force"], paths, config);

        Assert.Equal(0, exitCode);
        // Already current-hash-embedded, so --force's candidate set still resolves to a no-op
        // write (UpsertEmbeddingAsync's own hash check), reported as skipped, not embedded.
        Assert.Contains("embedded=0 skipped-hash-unchanged=1 failed=0", stdout);
    }

    private static async Task<(int ExitCode, string Stdout)> RunCapturedAsync(string[] args, NetclawPaths paths, IConfiguration config)
    {
        var (exitCode, stdout, _) = await RunCapturedWithStderrAsync(args, paths, config);
        return (exitCode, stdout);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunCapturedWithStderrAsync(
        string[] args, NetclawPaths paths, IConfiguration config)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = await MemoryCommand.RunAsync(args, paths, config, FixtureAllowlist(), stdout, stderr);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static NetclawPaths CreateTempPaths(bool prePlaceValidModel)
    {
        var basePath = Path.Combine(Path.GetTempPath(), "netclaw-memory-command-tests", Guid.NewGuid().ToString("N"));
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();

        if (prePlaceValidModel)
        {
            // Pre-place a valid local copy so ProvisionAsync's skip-if-valid path never reaches
            // the network (the fixture allowlist's URLs are unreachable dummies).
            var dir = paths.EmbeddingModelDirectory(ModelId);
            Directory.CreateDirectory(dir);
            File.Copy(Path.Combine(FixturesDir, "tiny-embedder.onnx"), Path.Combine(dir, "model.onnx"), overwrite: true);
            File.Copy(Path.Combine(FixturesDir, "tiny-vocab.txt"), Path.Combine(dir, "vocab.txt"), overwrite: true);
        }

        return paths;
    }

    private static IConfiguration BuildConfig(bool autoDownload)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Memory:Embeddings:Enabled"] = "true",
            ["Memory:Embeddings:ModelId"] = ModelId,
            ["Memory:Embeddings:AutoDownload"] = autoDownload ? "true" : "false",
        };

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
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

    private static IReadOnlyDictionary<string, EmbeddingModelManifestEntry> FixtureAllowlist()
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
                ModelByteSize: modelBytes.Length,
                QueryPrefix: "search_query: ",
                CalibratedMinCosineSimilarity: 0.42),
        };
    }
}

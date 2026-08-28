// -----------------------------------------------------------------------
// <copyright file="MemoryCommand.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Netclaw.Actors.Memory;
using Netclaw.Configuration;
using Netclaw.Embeddings;

namespace Netclaw.Cli.Memory;

/// <summary>
/// Handles <c>netclaw memory &lt;subcommand&gt;</c> CLI subcommands
/// (memory-core-redesign Slice 2, task 2.9). All commands are offline — they operate directly
/// on the SQLite memory database and the embedding model files, no daemon required, following
/// the same direct-store-access convention as <c>MemoryCheckpointHealthDoctorCheck</c>.
/// </summary>
internal static class MemoryCommand
{
    public static Task<int> RunAsync(
        string[] args,
        NetclawPaths paths,
        IConfiguration configuration,
        TextWriter output,
        TextWriter error)
        => RunAsync(args, paths, configuration, EmbeddingModelProvisioner.Allowlist, output, error);

    /// <summary>
    /// Test-visible entry point: <paramref name="allowlist"/> is the same explicit, required
    /// dependency <see cref="EmbeddingModelProvisioner"/> and <c>MemoryEmbeddingDoctorCheck</c>
    /// take, so tests can point this command at a small fixture allowlist instead of the real
    /// ~100-300 MB HuggingFace artifacts. Production callers use the single-argument overload,
    /// which always passes <see cref="EmbeddingModelProvisioner.Allowlist"/>.
    /// </summary>
    internal static Task<int> RunAsync(
        string[] args,
        NetclawPaths paths,
        IConfiguration configuration,
        IReadOnlyDictionary<string, EmbeddingModelManifestEntry> allowlist,
        TextWriter output,
        TextWriter error)
    {
        var subcommand = args.Length > 1 ? args[1] : "help";

        if (subcommand is "help" or "-h" or "--help")
            return Task.FromResult(WriteHelp(output));

        // `backfill-embeddings` takes no required positional arguments (only the optional
        // `--force` flag), so a trailing `--help`/`-h` would otherwise be silently ignored
        // and the real provision-and-embed run would execute instead of printing help
        // (canary finding: `netclaw memory backfill-embeddings --help` downloaded/embedded
        // for real). Scan the full argument list, not just the subcommand slot.
        if (CliArgsParser.HasTrailingHelpToken(args, startIndex: 2))
            return Task.FromResult(WriteHelp(output));

        return subcommand switch
        {
            "backfill-embeddings" => RunBackfillEmbeddingsAsync(args, paths, configuration, allowlist, output, error),
            _ => Task.FromResult(WriteHelp(output))
        };
    }

    private static int WriteHelp(TextWriter output)
    {
        output.WriteLine("Usage: netclaw memory <subcommand>");
        output.WriteLine();
        output.WriteLine("Subcommands:");
        output.WriteLine("  backfill-embeddings [--force]   Provision the embedding model (if needed) and");
        output.WriteLine("                                   embed memories missing a current-model embedding.");
        output.WriteLine("                                   --force re-scans every recallable document instead");
        output.WriteLine("                                   of only ones missing a current-model embedding.");
        return 0;
    }

    private static async Task<int> RunBackfillEmbeddingsAsync(
        string[] args,
        NetclawPaths paths,
        IConfiguration configuration,
        IReadOnlyDictionary<string, EmbeddingModelManifestEntry> allowlist,
        TextWriter output,
        TextWriter error)
    {
        var force = args.Contains("--force", StringComparer.OrdinalIgnoreCase);
        var memoryConfig = configuration.GetSection("Memory").Get<MemoryConfig>() ?? new MemoryConfig();
        var modelId = memoryConfig.Embeddings.ModelId;
        var modelDirectory = paths.EmbeddingModelDirectory(modelId);

        ProvisionedEmbeddingModel provisioned;
        using (var httpClient = new HttpClient())
        {
            var provisioner = new EmbeddingModelProvisioner(httpClient, allowlist);
            try
            {
                if (memoryConfig.Embeddings.AutoDownload)
                {
                    output.WriteLine($"Provisioning embedding model '{modelId}'...");
                    provisioned = await provisioner.ProvisionAsync(modelId, modelDirectory);
                }
                else
                {
                    provisioned = await provisioner.TryLoadVerifiedAsync(modelId, modelDirectory)
                        ?? throw new InvalidOperationException(
                            $"Embedding model '{modelId}' is not provisioned (or fails hash verification) at " +
                            $"{modelDirectory}, and Memory.Embeddings.AutoDownload is false. Provision the model " +
                            "manually, or enable AutoDownload and re-run this command.");
                }
            }
            catch (Exception ex)
            {
                error.WriteLine($"[FAIL] unable to provision embedding model '{modelId}': {ex.Message}");
                return 1;
            }
        }

        output.WriteLine($"Loading embedder '{provisioned.ModelId}' ({provisioned.Dimensions} dims)...");
        using var embedder = await OnnxMemoryEmbedder.LoadAsync(
            provisioned.ModelPath, provisioned.VocabPath, provisioned.ModelId, provisioned.Dimensions, provisioned.QueryPrefix);

        // Direct SQLite access, same as the doctor checks: WAL mode (set by InitializeAsync's
        // idempotent DDL) plus Microsoft.Data.Sqlite's default busy-timeout keep each small
        // per-item upsert transaction below safe to interleave with a live daemon's own writes
        // (curation commits, embed-on-write) against the same database file.
        var store = new SQLiteMemoryStore(paths.MemorySqliteDbPath, TimeProvider.System);
        await store.InitializeAsync();

        var candidates = await store.GetDocumentsNeedingEmbeddingAsync(embedder.ModelId, force);
        if (candidates.Count == 0)
        {
            output.WriteLine("Nothing to backfill: all recallable documents already have a current-model embedding.");
            return 0;
        }

        output.WriteLine($"Embedding {candidates.Count} document(s){(force ? " (--force)" : "")}...");

        const int batchSize = 16;
        var embedded = 0;
        var skippedUnchanged = 0;
        var failed = 0;

        for (var offset = 0; offset < candidates.Count; offset += batchSize)
        {
            var batch = candidates.Skip(offset).Take(batchSize).ToArray();
            var texts = batch.Select(d => $"{d.Title}\n{d.Body}").ToArray();

            IReadOnlyList<ReadOnlyMemory<float>> vectors;
            try
            {
                vectors = await embedder.EmbedBatchAsync(texts, EmbeddingPurpose.Passage, CancellationToken.None);
            }
            catch (Exception ex)
            {
                failed += batch.Length;
                error.WriteLine($"[WARN] batch at offset {offset} failed to embed: {ex.Message}");
                continue;
            }

            for (var i = 0; i < batch.Length; i++)
            {
                try
                {
                    var hash = MemoryContentHasher.ComputeHash(batch[i].Title, batch[i].Body);

                    // UpsertEmbeddingAsync's own hash check (re-queried at call time) is what
                    // makes this safe against a concurrent live daemon: if the daemon's own
                    // embed-on-write already embedded this item between our candidate scan and
                    // now, this call correctly no-ops instead of double-writing.
                    var wrote = await store.UpsertEmbeddingAsync(
                        batch[i].DocumentId, MemoryEmbedOnWriteCoordinator.DocumentItemKind,
                        embedder.ModelId, hash, vectors[i]);

                    if (wrote)
                        embedded++;
                    else
                        skippedUnchanged++;
                }
                catch (Exception ex)
                {
                    failed++;
                    error.WriteLine($"[WARN] failed to store embedding for {batch[i].DocumentId}: {ex.Message}");
                }
            }

            output.WriteLine($"  ...{Math.Min(offset + batch.Length, candidates.Count)}/{candidates.Count}");
        }

        output.WriteLine();
        output.WriteLine($"Done: embedded={embedded} skipped-hash-unchanged={skippedUnchanged} failed={failed}");
        return failed > 0 ? 1 : 0;
    }
}

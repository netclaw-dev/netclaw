// -----------------------------------------------------------------------
// <copyright file="MemoryRelevanceGateDoctorCheckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Netclaw.Embeddings;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

/// <summary>
/// Covers every severity branch of <see cref="MemoryRelevanceGateDoctorCheck"/> (memory-
/// relevance-gate task 2.3), using the tiny fixture cross-encoder ONNX graph (linked from
/// <c>Netclaw.Embeddings.Tests/Fixtures</c>) instead of the real allowlist — no network access
/// anywhere in these tests. Mirrors <see cref="MemoryEmbeddingDoctorCheckTests"/>'s structure.
/// </summary>
public sealed class MemoryRelevanceGateDoctorCheckTests
{
    private static string FixturesDir => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    [Fact]
    public async Task Passes_with_disabled_message_when_embeddings_off_and_gate_not_overridden()
    {
        var paths = CreateTempPaths();
        var config = WriteConfig(paths, embeddingsEnabled: false, gateEnabled: null);
        var check = new MemoryRelevanceGateDoctorCheck(paths, config, FixtureAllowlist());

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("disabled", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("follows", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Passes_with_disabled_message_when_explicitly_disabled_despite_embeddings_on()
    {
        var paths = CreateTempPaths();
        var config = WriteConfig(paths, embeddingsEnabled: true, gateEnabled: false);
        var check = new MemoryRelevanceGateDoctorCheck(paths, config, FixtureAllowlist());

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("explicitly false", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Warns_when_gate_is_explicitly_enabled_but_embeddings_are_disabled()
    {
        var paths = CreateTempPaths();
        var config = WriteConfig(paths, embeddingsEnabled: false, gateEnabled: true);
        var check = new MemoryRelevanceGateDoctorCheck(paths, config, FixtureAllowlist());

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("cannot run", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Memory.Embeddings.Enabled is false", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Warns_when_gate_active_but_model_is_missing_and_auto_download_is_true()
    {
        var paths = CreateTempPaths();
        var config = WriteConfig(paths, embeddingsEnabled: true, gateEnabled: null, autoDownload: true);
        // No model files placed at paths.EmbeddingModelDirectory(DefaultRelevanceModelId).
        var check = new MemoryRelevanceGateDoctorCheck(paths, config, FixtureAllowlist());

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains(EmbeddingModelProvisioner.DefaultRelevanceModelId, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Errors_when_gate_active_but_model_is_missing_and_auto_download_is_false()
    {
        var paths = CreateTempPaths();
        var config = WriteConfig(paths, embeddingsEnabled: true, gateEnabled: null, autoDownload: false);
        // No model files placed at paths.EmbeddingModelDirectory(DefaultRelevanceModelId).
        var check = new MemoryRelevanceGateDoctorCheck(paths, config, FixtureAllowlist());

        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains(EmbeddingModelProvisioner.DefaultRelevanceModelId, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Passes_with_healthy_message_when_model_is_provisioned()
    {
        var paths = CreateTempPaths();
        var config = WriteConfig(paths, embeddingsEnabled: true, gateEnabled: null);
        PrePlaceValidModelFiles(paths);

        var check = new MemoryRelevanceGateDoctorCheck(paths, config, FixtureAllowlist());
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("healthy", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static NetclawPaths CreateTempPaths()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "netclaw-relevance-gate-doctor-tests", Guid.NewGuid().ToString("N"));
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();
        return paths;
    }

    private static IConfiguration WriteConfig(NetclawPaths paths, bool embeddingsEnabled, bool? gateEnabled, bool autoDownload = true)
    {
        var recall = new Dictionary<string, object>
        {
            ["RelevanceGate"] = gateEnabled is { } enabled
                ? new Dictionary<string, object> { ["Enabled"] = enabled }
                : new Dictionary<string, object>(),
        };

        var config = new Dictionary<string, object>
        {
            ["Memory"] = new Dictionary<string, object>
            {
                ["Embeddings"] = new Dictionary<string, object>
                {
                    ["Enabled"] = embeddingsEnabled,
                    ["AutoDownload"] = autoDownload,
                },
                ["Recall"] = recall,
            }
        };

        File.WriteAllText(paths.NetclawConfigPath, JsonSerializer.Serialize(config));

        return new ConfigurationBuilder()
            .AddJsonFile(paths.NetclawConfigPath, optional: false)
            .Build();
    }

    private static void PrePlaceValidModelFiles(NetclawPaths paths)
    {
        var dir = paths.EmbeddingModelDirectory(EmbeddingModelProvisioner.DefaultRelevanceModelId);
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(FixturesDir, "tiny-cross-encoder.onnx"), Path.Combine(dir, "model.onnx"), overwrite: true);
        File.Copy(Path.Combine(FixturesDir, "tiny-cross-encoder-vocab.txt"), Path.Combine(dir, "vocab.txt"), overwrite: true);
    }

    private static IReadOnlyDictionary<string, RelevanceModelManifestEntry> FixtureAllowlist()
    {
        var modelBytes = File.ReadAllBytes(Path.Combine(FixturesDir, "tiny-cross-encoder.onnx"));
        var vocabBytes = File.ReadAllBytes(Path.Combine(FixturesDir, "tiny-cross-encoder-vocab.txt"));

        return new Dictionary<string, RelevanceModelManifestEntry>
        {
            [EmbeddingModelProvisioner.DefaultRelevanceModelId] = new(
                EmbeddingModelProvisioner.DefaultRelevanceModelId,
                ModelUrl: new Uri("http://127.0.0.1:1/unused-model.onnx"),
                TokenizerUrl: new Uri("http://127.0.0.1:1/unused-vocab.txt"),
                ModelSha256: Convert.ToHexStringLower(SHA256.HashData(modelBytes)),
                TokenizerSha256: Convert.ToHexStringLower(SHA256.HashData(vocabBytes)),
                ModelByteSize: modelBytes.Length,
                CalibratedThreshold: 0.02),
        };
    }
}

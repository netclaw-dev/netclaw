// -----------------------------------------------------------------------
// <copyright file="ChatClientDoctorCheckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class ChatClientDoctorCheckTests
{
    [Fact]
    public async Task ReturnsWarning_WhenNoProvidersConfigured()
    {
        var paths = CreatePathsWithConfig("""
            {
              "configVersion": 1
            }
            """);

        var check = new ChatClientDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("No-Op chat client", result.Message);
        Assert.Contains("netclaw model", result.Remediation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("netclaw.json", result.Remediation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReturnsPass_WhenProvidersAndModelsAreValid()
    {
        var paths = CreatePathsWithConfig("""
            {
              "configVersion": 1,
              "Providers": {
                "openrouter": { "Type": "openrouter" }
              },
              "Models": {
                "Main": { "Provider": "openrouter", "ModelId": "anthropic/claude-haiku-4" }
              }
            }
            """);

        var check = new ChatClientDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("openrouter", result.Message);
    }

    [Fact]
    public async Task ReturnsWarning_WhenProviderExistsButMainModelMissing()
    {
        var paths = CreatePathsWithConfig("""
            {
              "configVersion": 1,
              "Providers": {
                "local-ollama": { "Type": "ollama" }
              }
            }
            """);

        var check = new ChatClientDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("Models:Main missing", result.Message);
        Assert.DoesNotContain("Real chat client configured", result.Message);
    }

    [Fact]
    public async Task ReturnsWarning_WhenMainModelSectionHasNoProviderOrModel()
    {
        var paths = CreatePathsWithConfig("""
            {
              "configVersion": 1,
              "Providers": {
                "local-ollama": { "Type": "ollama" }
              },
              "Models": {
                "Main": { }
              }
            }
            """);

        var check = new ChatClientDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("no model selected", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReturnsError_WhenReferencedProviderMissingType()
    {
        var paths = CreatePathsWithConfig("""
            {
              "configVersion": 1,
              "Providers": {
                "openrouter": { }
              },
              "Models": {
                "Main": { "Provider": "openrouter", "ModelId": "anthropic/claude-haiku-4" }
              }
            }
            """);

        var check = new ChatClientDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("missing required Type", result.Message);
    }

    [Fact]
    public async Task ReturnsError_WhenFallbackReferencesUnknownProvider()
    {
        var paths = CreatePathsWithConfig("""
            {
              "configVersion": 1,
              "Providers": {
                "openrouter": { "Type": "openrouter" }
              },
              "Models": {
                "Main": { "Provider": "openrouter", "ModelId": "anthropic/claude-haiku-4" },
                "Fallback": { "Provider": "missing", "ModelId": "qwen3:30b" }
              }
            }
            """);

        var check = new ChatClientDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("Fallback", result.Message);
        Assert.Contains("missing", result.Message);
    }

    [Fact]
    public async Task ReturnsWarning_WhenModelReferencesUnknownProvider()
    {
        // Regression: a typo in Models:Main.Provider (e.g. operator typed
        // "ollama-local1" instead of "ollama-local") used to crash the daemon
        // with an unhandled ProviderPluginFactory exception. We now treat it
        // as degraded mode — same remediation as genuinely-no-provider —
        // and surface the typo via the No-Op banner's available-providers
        // line and this doctor warning.
        var paths = CreatePathsWithConfig("""
            {
              "configVersion": 1,
              "Providers": {
                "ollama-local": { "Type": "ollama" }
              },
              "Models": {
                "Main": { "Provider": "ollama-local1", "ModelId": "qwen3:30b" }
              }
            }
            """);

        var check = new ChatClientDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("No-Op chat client", result.Message);
        Assert.Contains("ollama-local1", result.Message);
        Assert.Contains("ollama-local", result.Message);
    }

    [Fact]
    public async Task ReturnsWarning_WhenConfigFileMissing()
    {
        var basePath = CreateTempBasePath();
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();

        var check = new ChatClientDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        // Missing config short-circuits via DoctorJsonConfigReader and yields its own
        // warning — the chat-client check is not reached.
        Assert.Equal(DoctorSeverity.Warning, result.Severity);
    }

    private static NetclawPaths CreatePathsWithConfig(string configJson)
    {
        var basePath = CreateTempBasePath();
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();
        File.WriteAllText(paths.NetclawConfigPath, configJson);
        return paths;
    }

    private static string CreateTempBasePath()
    {
        var path = Path.Combine(Path.GetTempPath(), "netclaw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

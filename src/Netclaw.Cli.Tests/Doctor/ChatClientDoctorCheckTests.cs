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
    public async Task ReturnsError_WhenModelReferencesUnknownProvider()
    {
        var paths = CreatePathsWithConfig("""
            {
              "configVersion": 1,
              "Providers": {
                "openrouter": { "Type": "openrouter" }
              },
              "Models": {
                "Main": { "Provider": "anthropic", "ModelId": "claude-4" }
              }
            }
            """);

        var check = new ChatClientDoctorCheck(paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("anthropic", result.Message);
        Assert.DoesNotContain("No-Op", result.Message);
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

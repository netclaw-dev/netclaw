// -----------------------------------------------------------------------
// <copyright file="McpToolGrantDoctorCheckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class McpToolGrantDoctorCheckTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public McpToolGrantDoctorCheckTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task AllPostureCuratedSnapshot_WarnsWithOmittedTools()
    {
        WriteConfig("""
            {
              "configVersion": 1,
              "Tools": { "AudienceProfiles": { "Personal": {
                "McpServersMode": "All",
                "McpServerToolGrants": { "notion": ["search"] }
              } } },
              "McpServers": { "notion": { "Transport": "http", "Url": "https://mcp.example.test", "Enabled": true } }
            }
            """);

        var result = await CreateCheck(_ => ["search", "delete", "archive"])
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("Personal", result.Message);
        Assert.Contains("notion", result.Message);
        Assert.Contains("delete", result.Message);
        Assert.Contains("archive", result.Message);
        Assert.Contains("remain exposed", result.Message);
        Assert.Contains("Allowlist", result.Remediation);
        Assert.Contains("netclaw mcp tools <server> --revoke <tool> --audience <name>", result.Remediation);
    }

    [Fact]
    public async Task AllPostureEmptySnapshot_WarnsWithAllDiscoveredTools()
    {
        WriteConfig("""
            {
              "configVersion": 1,
              "Tools": { "AudienceProfiles": { "Personal": {
                "McpServersMode": "All", "McpServerToolGrants": { "notion": [] }
              } } },
              "McpServers": { "notion": { "Transport": "http", "Url": "https://mcp.example.test", "Enabled": true } }
            }
            """);
        var result = await CreateCheck(_ => ["search", "delete"])
            .RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("search", result.Message);
        Assert.Contains("delete", result.Message);
    }

    [Fact]
    public async Task AllPostureFullSnapshot_DoesNotWarn()
    {
        WriteConfig("""
            {
              "configVersion": 1,
              "Tools": { "AudienceProfiles": { "Personal": {
                "McpServersMode": "All", "McpServerToolGrants": { "notion": ["archive", "delete", "search"] }
              } } },
              "McpServers": { "notion": { "Transport": "http", "Url": "https://mcp.example.test", "Enabled": true } }
            }
            """);
        var result = await CreateCheck(_ => ["search", "delete", "archive"])
            .RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Fact]
    public async Task AllowlistSnapshot_DoesNotWarn()
    {
        WriteConfig("""
            {
              "configVersion": 1,
              "Tools": { "AudienceProfiles": { "Team": {
                "McpServersMode": "Allowlist", "AllowedMcpServers": ["notion"],
                "McpServerToolGrants": { "notion": ["search"] }
              } } },
              "McpServers": { "notion": { "Transport": "http", "Url": "https://mcp.example.test", "Enabled": true } }
            }
            """);
        var result = await CreateCheck(_ => ["search", "delete"])
            .RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Fact]
    public async Task UnavailableCatalog_WarnsThatVerificationIsIncomplete()
    {
        WriteConfig("""
            {
              "configVersion": 1,
              "Tools": { "AudienceProfiles": { "Personal": {
                "McpServersMode": "All", "McpServerToolGrants": { "notion": ["search"] }
              } } },
              "McpServers": { "notion": { "Transport": "http", "Url": "https://mcp.example.test", "Enabled": true } }
            }
            """);
        var check = new McpToolGrantDoctorCheck(
            _paths, (_, _) => throw new HttpRequestException("daemon unavailable"));
        var result = await check.RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("could not be checked", result.Message);
        Assert.Contains("notion", result.Message);
    }

    private McpToolGrantDoctorCheck CreateCheck(Func<string, IReadOnlyList<string>> getTools) =>
        new(_paths, (serverName, _) => Task.FromResult(getTools(serverName)));

    private void WriteConfig(string json) => File.WriteAllText(_paths.NetclawConfigPath, json);
}

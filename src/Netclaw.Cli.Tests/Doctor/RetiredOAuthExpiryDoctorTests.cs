// -----------------------------------------------------------------------
// <copyright file="RetiredOAuthExpiryDoctorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class RetiredOAuthExpiryDoctorTests : IDisposable
{
    private const string WithRetiredKeys = """
        {
          "configVersion": 1,
          "Providers": {
            "codex": { "Type": "openai", "AuthMethod": "OAuthDevice", "OAuthTokenExpiry": "2026-01-01T00:00:00+00:00" },
            "Plain": { "Type": "ollama" },
            "copilot": { "Type": "github-copilot", "oauthtokenexpiry": null }
          }
        }
        """;

    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public RetiredOAuthExpiryDoctorTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    [Fact]
    public async Task The_check_reports_each_provider_with_the_retired_key_and_does_not_change_the_exit_code()
    {
        File.WriteAllText(_paths.NetclawConfigPath, WithRetiredKeys);

        var result = await new RetiredOAuthExpiryDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("Providers.codex.OAuthTokenExpiry", result.Message);
        Assert.Contains("Providers.copilot.oauthtokenexpiry", result.Message);
        Assert.DoesNotContain("Plain", result.Message);
        Assert.Contains("netclaw doctor --fix", result.Message);
    }

    [Fact]
    public async Task The_check_reads_a_file_with_comments_and_trailing_commas()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """
            // The operator's note.
            { "Providers": { "codex": { "Type": "openai", "OAuthTokenExpiry": "2026-01-01T00:00:00+00:00", }, }, }
            """);

        var result = await new RetiredOAuthExpiryDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("Providers.codex.OAuthTokenExpiry", result.Message);
    }

    [Fact]
    public async Task The_check_has_nothing_to_report_without_the_key()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ "configVersion": 1, "Providers": { "codex": { "Type": "openai" } } }""");

        var result = await new RetiredOAuthExpiryDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.DoesNotContain("Providers.", result.Message);
    }

    [Fact]
    public async Task Fix_removes_the_key_after_it_copies_the_file_and_keeps_everything_else()
    {
        File.WriteAllText(_paths.NetclawConfigPath, WithRetiredKeys);
        File.WriteAllText(_paths.OAuthStatePath, """{ "Providers": { "codex": { "OAuthTokenExpiry": "2026-06-01T00:00:00+00:00" } } }""");
        var stateBefore = File.ReadAllText(_paths.OAuthStatePath);
        var service = ConfigOnlyService();

        var plan = await service.BuildPlanAsync(TestContext.Current.CancellationToken);

        var fix = Assert.Single(plan.Fixes);
        Assert.Equal(_paths.NetclawConfigPath, fix.FilePath);
        var backupPath = _paths.NetclawConfigPath + ".retired-oauth-expiry.bak";
        Assert.Equal([backupPath], DoctorFixService.PlannedBackups(fix));

        var backups = await service.ApplyAsync(plan, TestContext.Current.CancellationToken);

        Assert.Equal([backupPath], backups);
        Assert.Equal(WithRetiredKeys, File.ReadAllText(backupPath));
        using var config = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var providers = config.RootElement.GetProperty("Providers");
        Assert.Equal("openai", providers.GetProperty("codex").GetProperty("Type").GetString());
        Assert.Equal("OAuthDevice", providers.GetProperty("codex").GetProperty("AuthMethod").GetString());
        Assert.Equal("ollama", providers.GetProperty("Plain").GetProperty("Type").GetString());
        Assert.Equal("github-copilot", providers.GetProperty("copilot").GetProperty("Type").GetString());
        Assert.DoesNotContain("OAuthTokenExpiry", File.ReadAllText(_paths.NetclawConfigPath), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(stateBefore, File.ReadAllText(_paths.OAuthStatePath));

        var after = await new RetiredOAuthExpiryDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("Providers.", after.Message);
        Assert.Empty((await service.BuildPlanAsync(TestContext.Current.CancellationToken)).Fixes);
    }

    [Fact]
    public async Task Fix_moves_a_retired_expiry_to_oauth_state_json_unless_the_file_has_an_entry()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """
            { "configVersion": 1, "Providers": {
                "moved": { "Type": "github-copilot", "OAuthTokenExpiry": "2026-01-01T00:00:00+00:00" },
                "kept": { "Type": "github-copilot", "OAuthTokenExpiry": "2026-02-01T00:00:00+00:00" } } }
            """);
        File.WriteAllText(_paths.OAuthStatePath, """{ "Providers": { "kept": { "OAuthTokenExpiry": "2026-06-01T00:00:00+00:00" } } }""");
        var service = ConfigOnlyService();

        await service.ApplyAsync(await service.BuildPlanAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        Assert.True(OAuthStateStore.TryGetExpiry(_paths, "moved", out var moved));
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:00+00:00"), moved);
        Assert.True(OAuthStateStore.TryGetExpiry(_paths, "kept", out var kept));
        Assert.Equal(DateTimeOffset.Parse("2026-06-01T00:00:00+00:00"), kept);
        Assert.DoesNotContain("OAuthTokenExpiry", File.ReadAllText(_paths.NetclawConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fix_numbers_the_backup_when_the_name_is_taken()
    {
        File.WriteAllText(_paths.NetclawConfigPath, WithRetiredKeys);
        File.WriteAllText(_paths.NetclawConfigPath + ".retired-oauth-expiry.bak", "an earlier backup");
        var service = ConfigOnlyService();

        var plan = await service.BuildPlanAsync(TestContext.Current.CancellationToken);
        var backups = await service.ApplyAsync(plan, TestContext.Current.CancellationToken);

        Assert.Equal([_paths.NetclawConfigPath + ".retired-oauth-expiry.2.bak"], backups);
        Assert.Equal("an earlier backup", File.ReadAllText(_paths.NetclawConfigPath + ".retired-oauth-expiry.bak"));
        Assert.Equal(WithRetiredKeys, File.ReadAllText(backups[0]));
    }

    [Fact]
    public async Task Fix_leaves_a_file_with_comments_alone_because_the_CLI_would_drop_the_comments()
    {
        const string commented = """
            // The operator's note.
            { "Providers": { "codex": { "Type": "openai", "OAuthTokenExpiry": "2026-01-01T00:00:00+00:00" } } }
            """;
        File.WriteAllText(_paths.NetclawConfigPath, commented);

        var plan = await ConfigOnlyService().BuildPlanAsync(TestContext.Current.CancellationToken);

        Assert.Empty(plan.Fixes);
        Assert.True(CliConfigPreflight.TryDescribeUneditableConfig(_paths, out var message));
        Assert.Contains("comments", message);
        Assert.Equal(commented, File.ReadAllText(_paths.NetclawConfigPath));
    }

    private DoctorFixService ConfigOnlyService()
        => new(_paths, Path.Combine(_paths.BasePath, "unused.service"), systemdEnabled: false);

    public void Dispose() => _dir.Dispose();
}

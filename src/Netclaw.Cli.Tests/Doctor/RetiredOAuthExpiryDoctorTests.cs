// -----------------------------------------------------------------------
// <copyright file="RetiredOAuthExpiryDoctorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
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
    public async Task Fix_removes_the_key_after_it_copies_the_date_and_keeps_everything_else()
    {
        File.WriteAllText(_paths.NetclawConfigPath, WithRetiredKeys);
        // The provider already has an expiry in secrets.json, so the fix copies nothing and does not write the file.
        WriteSecrets("""{ "Providers": { "codex": { "OAuthAccessToken": "access-1", "OAuthTokenExpiry": "2026-06-01T00:00:00.0000000+00:00" } } }""");
        var secretsBefore = File.ReadAllText(_paths.SecretsPath);
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
        Assert.Equal(secretsBefore, File.ReadAllText(_paths.SecretsPath));

        var after = await new RetiredOAuthExpiryDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("Providers.", after.Message);
        Assert.Empty((await service.BuildPlanAsync(TestContext.Current.CancellationToken)).Fixes);
    }

    [Fact]
    public async Task Fix_moves_a_retired_expiry_to_secrets_json_unless_the_provider_already_has_one()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """
            { "configVersion": 1, "Providers": {
                "moved": { "Type": "github-copilot", "OAuthTokenExpiry": "2026-01-01T00:00:00+02:00" },
                "kept": { "Type": "github-copilot", "OAuthTokenExpiry": "2026-02-01T00:00:00+00:00" },
                "hidden": { "Type": "github-copilot", "OAuthTokenExpiry": "2026-03-01T00:00:00+00:00" } } }
            """);
        WriteSecrets("""
            { "Providers": {
                "moved": { "OAuthAccessToken": "access-1" },
                "kept": { "OAuthAccessToken": "access-2", "OAuthTokenExpiry": "2026-06-01T00:00:00.0000000+00:00" },
                "hidden": { "OAuthAccessToken": "access-3", "OAuthTokenExpiry": null } } }
            """);
        var service = ConfigOnlyService();

        await service.ApplyAsync(await service.BuildPlanAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        using var secrets = JsonDocument.Parse(File.ReadAllText(_paths.SecretsPath));
        var providers = secrets.RootElement.GetProperty("Providers");
        // Plaintext and canonical, as PersistTokens writes it. The tokens stay encrypted.
        Assert.Equal("2026-01-01T00:00:00.0000000+02:00", providers.GetProperty("moved").GetProperty("OAuthTokenExpiry").GetString());
        Assert.StartsWith("ENC:", providers.GetProperty("moved").GetProperty("OAuthAccessToken").GetString(), StringComparison.Ordinal);
        Assert.Equal("2026-06-01T00:00:00.0000000+00:00", providers.GetProperty("kept").GetProperty("OAuthTokenExpiry").GetString());
        Assert.Equal(JsonValueKind.Null, providers.GetProperty("hidden").GetProperty("OAuthTokenExpiry").ValueKind);
        Assert.DoesNotContain("OAuthTokenExpiry", File.ReadAllText(_paths.NetclawConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fix_does_not_add_an_expiry_for_a_provider_without_a_secrets_entry()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """
            { "configVersion": 1, "Providers": {
                "signed-out": { "Type": "github-copilot", "OAuthTokenExpiry": "2026-01-01T00:00:00+00:00" } } }
            """);
        WriteSecrets("""{ "Providers": { "other": { "ApiKey": "sk-1" } } }""");
        var secretsBefore = File.ReadAllText(_paths.SecretsPath);
        var service = ConfigOnlyService();

        await service.ApplyAsync(await service.BuildPlanAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        Assert.Equal(secretsBefore, File.ReadAllText(_paths.SecretsPath));
        Assert.DoesNotContain("OAuthTokenExpiry", File.ReadAllText(_paths.NetclawConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fix_keeps_a_retired_value_that_is_not_a_date()
    {
        const string config = """
            { "configVersion": 1, "Providers": {
                "codex": { "Type": "openai", "OAuthTokenExpiry": "soon" } } }
            """;
        File.WriteAllText(_paths.NetclawConfigPath, config);
        WriteSecrets("""{ "Providers": { "codex": { "OAuthAccessToken": "access-1" } } }""");
        var secretsBefore = File.ReadAllBytes(_paths.SecretsPath);

        var plan = await ConfigOnlyService().BuildPlanAsync(TestContext.Current.CancellationToken);
        await ConfigOnlyService().ApplyAsync(plan, TestContext.Current.CancellationToken);

        Assert.Empty(plan.Fixes);
        Assert.Equal(config, File.ReadAllText(_paths.NetclawConfigPath));
        Assert.Equal(secretsBefore, File.ReadAllBytes(_paths.SecretsPath));
    }

    [Fact]
    public async Task Fix_then_the_daemon_sources_bind_the_same_expiry()
    {
        // The old shape: the expiry in netclaw.json, the tokens in secrets.json. The fix must hand
        // the daemon binder the same date from its new home.
        var expiry = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00");
        File.WriteAllText(_paths.NetclawConfigPath, """
            { "configVersion": 1, "Providers": {
                "codex": { "Type": "openai", "AuthMethod": "OAuthDevice", "OAuthTokenExpiry": "2026-01-01T00:00:00+00:00" } } }
            """);
        WriteSecrets("""{ "Providers": { "codex": { "OAuthAccessToken": "access-1", "OAuthRefreshToken": "refresh-1" } } }""");
        var service = ConfigOnlyService();

        await service.ApplyAsync(await service.BuildPlanAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        var previous = SensitiveStringTypeConverter.Protector;
        try
        {
            SensitiveStringTypeConverter.Protector = SecretsProtection.CreateProtector(_paths);
            var configuration = new ConfigurationBuilder().AddNetclawDaemonSources(_paths).Build();
            var providers = ProviderConfigurationLoader.Load(configuration.GetSection("Providers"));

            Assert.Equal(expiry, providers["codex"].OAuthTokenExpiry);
            Assert.Equal("refresh-1", providers["codex"].OAuthRefreshToken?.Value);
        }
        finally
        {
            SensitiveStringTypeConverter.Protector = previous;
        }

        Assert.DoesNotContain("OAuthTokenExpiry", File.ReadAllText(_paths.NetclawConfigPath), StringComparison.Ordinal);
        using var secrets = JsonDocument.Parse(File.ReadAllText(_paths.SecretsPath));
        Assert.Equal(
            "2026-01-01T00:00:00.0000000+00:00",
            secrets.RootElement.GetProperty("Providers").GetProperty("codex").GetProperty("OAuthTokenExpiry").GetString());
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
        Assert.Equal(commented, File.ReadAllText(_paths.NetclawConfigPath));
    }

    // The real writer encrypts the tokens, so the file has the shape that the product saves.
    private void WriteSecrets(string json)
        => SecretsFileWriter.Write(_paths.SecretsPath, json, SecretsProtection.CreateProtector(_paths));

    private DoctorFixService ConfigOnlyService()
        => new(_paths, Path.Combine(_paths.BasePath, "unused.service"), systemdEnabled: false);

    public void Dispose() => _dir.Dispose();
}

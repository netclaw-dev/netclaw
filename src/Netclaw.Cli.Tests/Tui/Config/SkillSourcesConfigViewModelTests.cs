// -----------------------------------------------------------------------
// <copyright file="SkillSourcesConfigViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Tui.Config;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Config;

public sealed class SkillSourcesConfigViewModelTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public SkillSourcesConfigViewModelTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        File.WriteAllText(_paths.NetclawConfigPath, "{ \"configVersion\": 1 }");
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Skill_sources_dashboard_entry_routes_to_real_editor()
    {
        using var vm = new Netclaw.Cli.Tui.ConfigDashboardViewModel(new Netclaw.Cli.Tui.ConfigDashboardNavigationState());
        string? route = null;
        vm.RouteRequested = r => route = r;

        vm.Activate(vm.Items.Single(static item => item.Label == "Skill Sources"));

        Assert.Equal("/skill-sources", route);
    }

    [Fact]
    public void Save_persists_external_directory_and_skill_feed_for_runtime_binding()
    {
        var externalDir = Path.Combine(_dir.Path, "team-skills");
        Directory.CreateDirectory(externalDir);
        using var vm = new SkillSourcesConfigViewModel(_paths, new FakeSkillFeedProbe(true));

        vm.AppendText(externalDir);
        vm.MoveSelection(1);
        vm.AppendText("https://skills.example.test");
        vm.MoveSelection(1);
        vm.AppendText("secret-token");

        Assert.True(vm.Save());

        var external = Bind<ExternalSkillsConfig>("ExternalSkills");
        var resolved = external.ResolveEnabledSources();
        Assert.Contains(resolved, source => source.Name == "custom-skills" && source.Paths.Contains(externalDir));

        var feed = SingleFeedSection();
        Assert.Equal("custom-feed", feed["Name"]);
        Assert.Equal("https://skills.example.test", feed["Url"]);
        var storedApiKey = feed["ApiKey"];
        Assert.NotNull(storedApiKey);
        Assert.StartsWith("ENC:", storedApiKey!, StringComparison.Ordinal);
        Assert.Equal("secret-token", Decrypt(storedApiKey!));
        Assert.DoesNotContain("secret-token", File.ReadAllText(_paths.NetclawConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Save_rejects_url_as_external_directory_before_persistence()
    {
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        using var vm = new SkillSourcesConfigViewModel(_paths, new FakeSkillFeedProbe(true));

        vm.AppendText("https://example.test/skills");

        Assert.False(vm.Save());
        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Contains("local filesystem path", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public void Save_rejects_missing_external_directory_before_persistence()
    {
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        using var vm = new SkillSourcesConfigViewModel(_paths, new FakeSkillFeedProbe(true));

        vm.AppendText(Path.Combine(_dir.Path, "missing-skills"));

        Assert.False(vm.Save());
        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Contains("must already exist", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public void Save_external_directory_does_not_decrypt_unedited_feed_api_key()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            "{\"configVersion\":1,\"SkillFeeds\":{\"Feeds\":[{\"Name\":\"custom-feed\",\"Url\":\"https://old.example.test\",\"ApiKey\":\"ENC:not-valid-for-this-keyring\",\"Enabled\":true}]}}");
        var externalDir = Path.Combine(_dir.Path, "team-skills");
        Directory.CreateDirectory(externalDir);
        using var vm = new SkillSourcesConfigViewModel(_paths, new FakeSkillFeedProbe(true));

        vm.AppendText(externalDir);

        Assert.True(vm.Save());
        Assert.Equal(externalDir, Bind<ExternalSkillsConfig>("ExternalSkills").ResolveEnabledSources().Single().Paths.Single());
        Assert.Equal("ENC:not-valid-for-this-keyring", SingleFeedSection()["ApiKey"]);
    }

    [Fact]
    public void Save_rejects_invalid_skill_feed_url_before_persistence()
    {
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        using var vm = new SkillSourcesConfigViewModel(_paths, new FakeSkillFeedProbe(true));

        vm.MoveSelection(1);
        vm.AppendText("file:///tmp/skills");

        Assert.False(vm.Save());
        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Contains("HTTP or HTTPS", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public void Save_rejects_multiline_skill_feed_api_key_before_persistence()
    {
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        using var vm = new SkillSourcesConfigViewModel(_paths, new FakeSkillFeedProbe(true));
        vm.MoveSelection(1);
        vm.AppendText("https://skills.example.test");
        vm.MoveSelection(1);
        vm.AppendText("token\nnext");

        Assert.False(vm.Save());
        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Contains("single-line", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public void Save_blocks_unreachable_skill_feed_until_second_save_anyway()
    {
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        using var vm = new SkillSourcesConfigViewModel(_paths, new FakeSkillFeedProbe(false));
        vm.MoveSelection(1);
        vm.AppendText("https://skills.example.test");

        Assert.False(vm.Save());
        Assert.Equal(ConfigStatusTone.Warning, vm.Status.Value.Tone);
        Assert.Contains("save anyway", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));

        Assert.True(vm.Save());
        Assert.Equal("https://skills.example.test", SingleFeedSection()["Url"]);
    }

    [Fact]
    public void Save_preserves_existing_feed_api_key_and_unrelated_secrets()
    {
        var protector = SecretsProtection.CreateProtector(_paths);
        var encryptedApiKey = protector.Protect("old-token");
        File.WriteAllText(_paths.NetclawConfigPath,
            $"{{\"configVersion\":1,\"SkillFeeds\":{{\"Feeds\":[{{\"Name\":\"custom-feed\",\"Url\":\"https://old.example.test\",\"ApiKey\":\"{encryptedApiKey}\",\"Enabled\":true}}]}}}}");
        File.WriteAllText(_paths.SecretsPath, "{\"Providers\":{\"openrouter\":{\"ApiKey\":\"ENC:provider\"}}}");
        var beforeSecrets = File.ReadAllText(_paths.SecretsPath);
        using var vm = new SkillSourcesConfigViewModel(_paths, new FakeSkillFeedProbe(true));
        vm.MoveSelection(1);
        vm.AppendText("https://new.example.test");

        Assert.True(vm.Save());

        var feed = SingleFeedSection();
        Assert.Equal("https://new.example.test", feed["Url"]);
        Assert.Equal(encryptedApiKey, feed["ApiKey"]);
        Assert.Equal("old-token", protector.Unprotect(feed["ApiKey"]!));
        Assert.Equal(beforeSecrets, File.ReadAllText(_paths.SecretsPath));
    }

    private T Bind<T>(string sectionName) where T : new()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(_paths.NetclawConfigPath)
            .Build();
        return configuration.GetSection(sectionName).Get<T>() ?? new T();
    }

    private IConfigurationSection SingleFeedSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(_paths.NetclawConfigPath)
            .Build();
        return Assert.Single(configuration.GetSection("SkillFeeds:Feeds").GetChildren());
    }

    private string Decrypt(string encrypted)
        => SecretsProtection.CreateProtector(_paths).Unprotect(encrypted);

    private sealed class FakeSkillFeedProbe(bool success) : ISkillFeedReachabilityProbe
    {
        public SkillFeedReachabilityResult Probe(string baseUrl, string? apiKey, int timeoutSeconds)
            => success
                ? new SkillFeedReachabilityResult(true, "reachable")
                : new SkillFeedReachabilityResult(false, "unreachable");
    }
}

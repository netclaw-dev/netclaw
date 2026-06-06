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

        AddLocalFolder(vm, externalDir, "team-skills");
        AddRemoteServer(vm, "https://skills.example.test", "secret-token", "custom-feed");

        var external = Bind<ExternalSkillsConfig>("ExternalSkills");
        var resolved = external.ResolveEnabledSources();
        Assert.Contains(resolved, source => source.Name == "team-skills" && source.Paths.Contains(externalDir));

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

        BeginAddLocalFolder(vm);
        vm.AppendText("https://example.test/skills");
        vm.ActivateSelected();

        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Contains("local filesystem path", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public void Save_rejects_missing_external_directory_before_persistence()
    {
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        using var vm = new SkillSourcesConfigViewModel(_paths, new FakeSkillFeedProbe(true));

        BeginAddLocalFolder(vm);
        vm.AppendText(Path.Combine(_dir.Path, "missing-skills"));
        vm.ActivateSelected();

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

        AddLocalFolder(vm, externalDir, "team-skills");

        Assert.Equal(externalDir, Bind<ExternalSkillsConfig>("ExternalSkills").ResolveEnabledSources().Single().Paths.Single());
        Assert.Equal("ENC:not-valid-for-this-keyring", SingleFeedSection()["ApiKey"]);
    }

    [Fact]
    public void Save_rejects_invalid_skill_feed_url_before_persistence()
    {
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        using var vm = new SkillSourcesConfigViewModel(_paths, new FakeSkillFeedProbe(true));

        BeginAddRemoteServer(vm);
        vm.AppendText("file:///tmp/skills");
        vm.ActivateSelected();

        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Contains("HTTP or HTTPS", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public void Save_rejects_multiline_skill_feed_api_key_before_persistence()
    {
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        using var vm = new SkillSourcesConfigViewModel(_paths, new FakeSkillFeedProbe(true));

        BeginAddRemoteServer(vm);
        vm.AppendText("https://skills.example.test");
        vm.ActivateSelected();
        vm.MoveSelection(1);
        vm.ActivateSelected();
        vm.AppendText("token\nnext");
        vm.ActivateSelected();

        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Contains("single-line", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public void Save_blocks_unreachable_skill_feed_until_second_save_anyway()
    {
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        using var vm = new SkillSourcesConfigViewModel(_paths, new FakeSkillFeedProbe(false));

        BeginAddRemoteServer(vm);
        vm.AppendText("https://skills.example.test");
        vm.ActivateSelected();
        vm.ActivateSelected();

        Assert.Equal(ConfigStatusTone.Warning, vm.Status.Value.Tone);
        Assert.Contains("save anyway", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));

        vm.ActivateSelected();
        ReplaceDraft(vm, "custom-feed");
        vm.ActivateSelected();

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

        OpenRemoteDetail(vm, "custom-feed");
        MoveToDetailAction(vm, SkillSourceDetailAction.ChangeLocation);
        vm.ActivateSelected();
        ReplaceDraft(vm, "https://new.example.test");
        vm.ActivateSelected();

        var feed = SingleFeedSection();
        Assert.Equal("https://new.example.test", feed["Url"]);
        Assert.Equal(encryptedApiKey, feed["ApiKey"]);
        Assert.Equal("old-token", protector.Unprotect(feed["ApiKey"]!));
        Assert.Equal(beforeSecrets, File.ReadAllText(_paths.SecretsPath));
    }

    [Fact]
    public void Location_detail_row_opens_local_path_editor()
    {
        var externalDir = Path.Combine(_dir.Path, "team-skills");
        Directory.CreateDirectory(externalDir);
        using var vm = new SkillSourcesConfigViewModel(_paths, new FakeSkillFeedProbe(true));

        AddLocalFolder(vm, externalDir, "team-skills");
        MoveToDetailAction(vm, SkillSourceDetailAction.Location);
        vm.ActivateSelected();

        Assert.Equal(SkillSourcesScreen.ChangeLocation, vm.Screen.Value);
        Assert.Equal(externalDir, vm.Draft.Value);
    }

    [Fact]
    public void Location_detail_row_opens_remote_url_editor()
    {
        using var vm = new SkillSourcesConfigViewModel(_paths, new FakeSkillFeedProbe(true));

        AddRemoteServer(vm, "https://skills.example.test", "secret-token", "custom-feed");
        MoveToDetailAction(vm, SkillSourceDetailAction.Location);
        vm.ActivateSelected();

        Assert.Equal(SkillSourcesScreen.ChangeLocation, vm.Screen.Value);
        Assert.Equal("https://skills.example.test", vm.Draft.Value);
    }

    [Fact]
    public void Local_source_status_warns_when_runtime_scan_reports_issues()
    {
        var externalDir = Path.Combine(_dir.Path, "team-skills");
        var invalidSkillDir = Path.Combine(externalDir, "broken-skill");
        Directory.CreateDirectory(invalidSkillDir);
        File.WriteAllText(Path.Combine(invalidSkillDir, "SKILL.md"), "not frontmatter");
        File.WriteAllText(_paths.NetclawConfigPath,
            $"{{\"configVersion\":1,\"ExternalSkills\":{{\"Sources\":[{{\"Name\":\"team-skills\",\"Path\":\"{externalDir.Replace("\\", "\\\\", StringComparison.Ordinal)}\",\"Enabled\":true,\"AllowSymlinks\":false}}]}}}}");
        using var vm = new SkillSourcesConfigViewModel(_paths, new FakeSkillFeedProbe(true));

        var source = Assert.Single(vm.Sources);
        Assert.Equal(ConfigStatusTone.Warning, source.StatusTone);
        Assert.Contains("scan warning", source.StatusText, StringComparison.OrdinalIgnoreCase);

        OpenLocalDetail(vm, "team-skills");
        MoveToDetailAction(vm, SkillSourceDetailAction.Rescan);
        vm.ActivateSelected();

        Assert.Equal(ConfigStatusTone.Warning, vm.Status.Value.Tone);
        Assert.Contains("scan warning", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Remove_token_explicitly_deletes_feed_api_key()
    {
        var protector = SecretsProtection.CreateProtector(_paths);
        var encryptedApiKey = protector.Protect("old-token");
        File.WriteAllText(_paths.NetclawConfigPath,
            $"{{\"configVersion\":1,\"SkillFeeds\":{{\"Feeds\":[{{\"Name\":\"custom-feed\",\"Url\":\"https://old.example.test\",\"ApiKey\":\"{encryptedApiKey}\",\"Enabled\":true}}]}}}}");
        using var vm = new SkillSourcesConfigViewModel(_paths, new FakeSkillFeedProbe(true));

        OpenRemoteDetail(vm, "custom-feed");
        MoveToDetailAction(vm, SkillSourceDetailAction.RemoveToken);
        vm.ActivateSelected();

        Assert.Null(SingleFeedSection()["ApiKey"]);
        Assert.Equal(ConfigStatusTone.Success, vm.Status.Value.Tone);
        Assert.Contains("token removed", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
    }

    private static void BeginAddLocalFolder(SkillSourcesConfigViewModel vm)
    {
        EnsureInventory(vm);
        MoveToInventoryAction(vm, SkillSourcesInventoryAction.AddLocalFolder);
        vm.ActivateSelected();
        Assert.Equal(SkillSourcesScreen.AddLocalPath, vm.Screen.Value);
    }

    private static void AddLocalFolder(SkillSourcesConfigViewModel vm, string path, string name)
    {
        BeginAddLocalFolder(vm);
        vm.AppendText(path);
        vm.ActivateSelected();
        Assert.Equal(SkillSourcesScreen.AddLocalSymlinks, vm.Screen.Value);
        vm.ActivateSelected();
        Assert.Equal(SkillSourcesScreen.AddLocalName, vm.Screen.Value);
        ReplaceDraft(vm, name);
        vm.ActivateSelected();
        Assert.Equal(SkillSourcesScreen.SourceDetail, vm.Screen.Value);
    }

    private static void BeginAddRemoteServer(SkillSourcesConfigViewModel vm)
    {
        EnsureInventory(vm);
        MoveToInventoryAction(vm, SkillSourcesInventoryAction.AddSkillServer);
        vm.ActivateSelected();
        Assert.Equal(SkillSourcesScreen.AddRemoteUrl, vm.Screen.Value);
    }

    private static void AddRemoteServer(SkillSourcesConfigViewModel vm, string url, string token, string name)
    {
        BeginAddRemoteServer(vm);
        vm.AppendText(url);
        vm.ActivateSelected();
        Assert.Equal(SkillSourcesScreen.AddRemoteAuth, vm.Screen.Value);
        vm.MoveSelection(1);
        vm.ActivateSelected();
        Assert.Equal(SkillSourcesScreen.AddRemoteToken, vm.Screen.Value);
        vm.AppendText(token);
        vm.ActivateSelected();
        Assert.Equal(SkillSourcesScreen.AddRemoteName, vm.Screen.Value);
        ReplaceDraft(vm, name);
        vm.ActivateSelected();
        Assert.Equal(SkillSourcesScreen.SourceDetail, vm.Screen.Value);
    }

    private static void OpenRemoteDetail(SkillSourcesConfigViewModel vm, string name)
    {
        var index = vm.InventoryRows
            .Select((row, idx) => (row, idx))
            .Single(entry => entry.row.SourceKind == SkillSourceKind.RemoteSkillServer && entry.row.SourceName == name)
            .idx;
        vm.SelectedRow.Value = index;
        vm.ActivateSelected();
        Assert.Equal(SkillSourcesScreen.SourceDetail, vm.Screen.Value);
    }

    private static void OpenLocalDetail(SkillSourcesConfigViewModel vm, string name)
    {
        var index = vm.InventoryRows
            .Select((row, idx) => (row, idx))
            .Single(entry => entry.row.SourceKind == SkillSourceKind.LocalFolder && entry.row.SourceName == name)
            .idx;
        vm.SelectedRow.Value = index;
        vm.ActivateSelected();
        Assert.Equal(SkillSourcesScreen.SourceDetail, vm.Screen.Value);
    }

    private static void MoveToInventoryAction(SkillSourcesConfigViewModel vm, SkillSourcesInventoryAction action)
    {
        vm.SelectedRow.Value = vm.InventoryRows
            .Select((row, idx) => (row, idx))
            .Single(entry => entry.row.Action == action)
            .idx;
    }

    private static void EnsureInventory(SkillSourcesConfigViewModel vm)
    {
        while (vm.Screen.Value != SkillSourcesScreen.Inventory)
            vm.GoBack();
    }

    private static void MoveToDetailAction(SkillSourcesConfigViewModel vm, SkillSourceDetailAction action)
    {
        vm.SelectedRow.Value = vm.DetailRows
            .Select((row, idx) => (row, idx))
            .Single(entry => entry.row.Action == action)
            .idx;
    }

    private static void ReplaceDraft(SkillSourcesConfigViewModel vm, string value)
    {
        while (vm.Draft.Value.Length > 0)
            vm.Backspace();
        vm.AppendText(value);
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

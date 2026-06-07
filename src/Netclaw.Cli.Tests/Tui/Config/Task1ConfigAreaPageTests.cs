// -----------------------------------------------------------------------
// <copyright file="Task1ConfigAreaPageTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Cli.Tui.Config;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Termina;
using Termina.Hosting;
using Termina.Input;
using Termina.Terminal;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Config;

public sealed class Task1ConfigAreaPageTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public Task1ConfigAreaPageTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        File.WriteAllText(_paths.NetclawConfigPath, "{ \"configVersion\": 1 }");
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Workspaces_page_accepts_typed_and_pasted_path_input()
    {
        var app = CreateWorkspacesApp(out var input, out var vm);

        input.EnqueueString("/tmp/netclaw-");
        input.EnqueuePaste("workspace-test");
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal("/tmp/netclaw-workspace-test", vm.DirectoryDraft.Value);
    }

    [Fact]
    public async Task Inbound_webhooks_page_accepts_typed_timeout_input()
    {
        var app = CreateInboundWebhooksApp(out var input, out var vm);

        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueString("45");
        input.EnqueueKey(ConsoleKey.Backspace);
        input.EnqueuePaste("0");
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal("40", vm.TimeoutDraft.Value);
    }

    [Fact]
    public async Task Skill_sources_page_accepts_typed_and_pasted_path_input()
    {
        var app = CreateSkillSourcesApp(out var input, out var vm);

        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueString("/tmp/netclaw smoke-");
        input.EnqueuePaste("skills");
        input.EnqueueKey(ConsoleKey.LeftArrow);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(SkillSourcesScreen.AddLocalPath, vm.Screen.Value);
        Assert.Equal("/tmp/netclaw smoke-skills", vm.Draft.Value);
    }

    [Fact]
    public async Task Skill_sources_local_path_screen_renders_visible_input_box()
    {
        var app = CreateSkillSourcesApp(out var input, out _, out var terminal);

        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var screen = terminal.ToString();
        Assert.True(screen.Contains("Folder path", StringComparison.Ordinal),
            $"Expected folder path input label in terminal output. Screen:\n{terminal}");
        Assert.DoesNotContain("Type here...|", screen, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Skill_sources_local_path_enter_rejects_missing_directory_before_persistence()
    {
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        var app = CreateSkillSourcesApp(out var input, out var vm);

        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueString(Path.Combine(_dir.Path, "missing-skills"));
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(SkillSourcesScreen.AddLocalPath, vm.Screen.Value);
        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Contains("must already exist", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public async Task Skill_sources_local_path_enter_accepts_existing_directory_without_persisting_incomplete_flow()
    {
        var externalDir = Path.Combine(_dir.Path, "team-skills");
        Directory.CreateDirectory(externalDir);
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        var app = CreateSkillSourcesApp(out var input, out var vm);

        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueuePaste(externalDir);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(SkillSourcesScreen.AddLocalSymlinks, vm.Screen.Value);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public async Task Skill_sources_local_name_enter_persists_source_to_external_skills()
    {
        var externalDir = Path.Combine(_dir.Path, "team-skills");
        Directory.CreateDirectory(externalDir);
        var app = CreateSkillSourcesApp(out var input, out var vm);

        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueuePaste(externalDir);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(SkillSourcesScreen.SourceDetail, vm.Screen.Value);
        using var doc = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("SkillFeeds", out _));
        var source = Assert.Single(root.GetProperty("ExternalSkills").GetProperty("Sources").EnumerateArray());
        Assert.Equal("team-skills", source.GetProperty("Name").GetString());
        Assert.Equal(externalDir, source.GetProperty("Path").GetString());
        Assert.True(source.GetProperty("Enabled").GetBoolean());
        Assert.False(source.GetProperty("AllowSymlinks").GetBoolean());
    }

    [Fact]
    public async Task Skill_sources_remote_url_screen_explains_skill_server_project()
    {
        var app = CreateSkillSourcesApp(out var input, out _, out var terminal);

        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var screen = terminal.ToString();
        Assert.True(screen.Contains("Server URL", StringComparison.Ordinal),
            $"Expected server URL input label in terminal output. Screen:\n{terminal}");
        Assert.DoesNotContain("Type here...|", screen, StringComparison.Ordinal);
        Assert.True(screen.Contains("https://github.com/netclaw-dev/skill-server", StringComparison.Ordinal),
            $"Expected skill-server project callout in terminal output. Screen:\n{terminal}");
    }

    [Fact]
    public async Task Skill_sources_remote_inventory_row_uses_readable_metadata_lines()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            "{\"configVersion\":1,\"SkillFeeds\":{\"Feeds\":[{\"Name\":\"skillserver-testlab-petabridge-net\",\"Url\":\"https://skillserver.testlab.petabridge.net\",\"Enabled\":true,\"TimeoutSeconds\":30}]}} ");
        var app = CreateSkillSourcesApp(out var input, out _, out var terminal);

        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var screen = terminal.ToString();
        Assert.Contains("Skillserver Testlab Petabridge", screen, StringComparison.Ordinal);
        Assert.Contains("skillserver.testlab.petabridge.net  |  No auth", screen, StringComparison.Ordinal);
        Assert.DoesNotContain("server https://skillserver", screen, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Skill_sources_remote_url_enter_rejects_invalid_url_before_persistence()
    {
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        var app = CreateSkillSourcesApp(out var input, out var vm);

        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueString("file:///tmp/skills");
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(SkillSourcesScreen.AddRemoteUrl, vm.Screen.Value);
        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Contains("HTTP or HTTPS", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public async Task Skill_sources_remote_url_enter_accepts_valid_url_without_persisting_incomplete_flow()
    {
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        var app = CreateSkillSourcesApp(out var input, out var vm);

        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueString("https://");
        input.EnqueuePaste("skills.example.test");
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(SkillSourcesScreen.AddRemoteAuth, vm.Screen.Value);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public async Task Skill_sources_remote_auth_enter_blocks_unreachable_probe_before_persistence()
    {
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        var app = CreateSkillSourcesApp(out var input, out var vm, out var terminal, new FakeSkillFeedProbe(false, "probe failed"));

        BeginRemoteUrlEntry(input, "https://skills.example.test");
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(SkillSourcesScreen.AddRemoteAuth, vm.Screen.Value);
        Assert.NotNull(vm.ActiveValidationDialog.Value);
        Assert.Equal(string.Empty, vm.Status.Value.Text);
        var screen = terminal.ToString();
        Assert.True(screen.Contains("Skill Server Validation Warning", StringComparison.Ordinal),
            $"Expected validation warning dialog. Screen:\n{terminal}");
        Assert.True(screen.Contains("probe failed", StringComparison.OrdinalIgnoreCase),
            $"Expected probe failure in dialog. Screen:\n{terminal}");
        Assert.Equal(1, CountOccurrences(screen, "probe failed", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public async Task Skill_sources_remote_auth_dialog_retry_keeps_source_unpersisted()
    {
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        var app = CreateSkillSourcesApp(out var input, out var vm, new FakeSkillFeedProbe(false, "probe failed"));

        BeginRemoteUrlEntry(input, "https://skills.example.test");
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(SkillSourcesScreen.AddRemoteAuth, vm.Screen.Value);
        Assert.NotNull(vm.ActiveValidationDialog.Value);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public async Task Skill_sources_remote_auth_dialog_save_anyway_reviews_name_without_persisting_incomplete_flow()
    {
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        var app = CreateSkillSourcesApp(out var input, out var vm, out var terminal, new FakeSkillFeedProbe(false, "probe failed"));

        BeginRemoteUrlEntry(input, "https://skills.example.test");
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(SkillSourcesScreen.AddRemoteName, vm.Screen.Value);
        Assert.Null(vm.ActiveValidationDialog.Value);
        var screen = terminal.ToString();
        Assert.True(screen.Contains("Review remote skill server source", StringComparison.Ordinal),
            $"Expected remote source name confirmation screen. Screen:\n{terminal}");
        Assert.True(screen.Contains("skills-example-test", StringComparison.Ordinal),
            $"Expected suggested source name in terminal output. Screen:\n{terminal}");
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public async Task Skill_sources_remote_auth_bearer_token_selection_advances_to_token_entry_without_probe()
    {
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        var app = CreateSkillSourcesApp(out var input, out var vm, new FakeSkillFeedProbe(false, "probe failed"));

        BeginRemoteUrlEntry(input, "https://skills.example.test");
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(SkillSourcesScreen.AddRemoteToken, vm.Screen.Value);
        Assert.NotEqual(ConfigStatusTone.Warning, vm.Status.Value.Tone);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public async Task Skill_sources_remote_token_enter_blocks_unreachable_probe_before_persistence_then_second_enter_reviews_name()
    {
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        var app = CreateSkillSourcesApp(out var input, out var vm, new FakeSkillFeedProbe(false, "probe failed"));

        BeginRemoteUrlEntry(input, "https://skills.example.test");
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueString("secret-token");
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(SkillSourcesScreen.AddRemoteName, vm.Screen.Value);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public async Task Skill_sources_remote_bearer_name_enter_persists_encrypted_token_to_skill_feeds()
    {
        var app = CreateSkillSourcesApp(out var input, out var vm);

        BeginRemoteUrlEntry(input, "https://skills.example.test");
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueString("secret-token");
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(SkillSourcesScreen.SourceDetail, vm.Screen.Value);
        var contents = File.ReadAllText(_paths.NetclawConfigPath);
        Assert.DoesNotContain("secret-token", contents, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(contents);
        var feed = Assert.Single(doc.RootElement.GetProperty("SkillFeeds").GetProperty("Feeds").EnumerateArray());
        Assert.Equal("https://skills.example.test", feed.GetProperty("Url").GetString());
        Assert.StartsWith("ENC:", feed.GetProperty("ApiKey").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Skill_sources_remote_name_enter_persists_no_auth_source_to_skill_feeds()
    {
        var app = CreateSkillSourcesApp(out var input, out var vm);

        BeginRemoteUrlEntry(input, "https://skills.example.test");
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(SkillSourcesScreen.SourceDetail, vm.Screen.Value);
        using var doc = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("ExternalSkills", out _));
        var feeds = root.GetProperty("SkillFeeds").GetProperty("Feeds");
        var feed = Assert.Single(feeds.EnumerateArray());
        Assert.Equal("https://skills.example.test", feed.GetProperty("Url").GetString());
        Assert.True(feed.GetProperty("Enabled").GetBoolean());
        Assert.Equal(30, feed.GetProperty("TimeoutSeconds").GetInt32());
        Assert.False(feed.TryGetProperty("ApiKey", out _));
    }

    [Fact]
    public async Task Skill_sources_remote_name_enter_after_save_anyway_persists_source_to_skill_feeds()
    {
        var app = CreateSkillSourcesApp(out var input, out var vm, new FakeSkillFeedProbe(false, "probe failed"));

        BeginRemoteUrlEntry(input, "https://example.invalid");
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(SkillSourcesScreen.SourceDetail, vm.Screen.Value);
        Assert.Equal(ConfigStatusTone.Success, vm.Status.Value.Tone);
        Assert.Contains("Added skill server", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var feeds = doc.RootElement.GetProperty("SkillFeeds").GetProperty("Feeds");
        var feed = Assert.Single(feeds.EnumerateArray());
        Assert.Equal("https://example.invalid", feed.GetProperty("Url").GetString());
        Assert.False(feed.TryGetProperty("ApiKey", out _));
    }

    [Fact]
    public async Task Skill_sources_remote_change_url_second_enter_saves_anyway_to_skill_feeds()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            "{\"configVersion\":1,\"SkillFeeds\":{\"Feeds\":[{\"Name\":\"custom-feed\",\"Url\":\"https://old.example.test\",\"Enabled\":true,\"TimeoutSeconds\":30}]}}");
        var app = CreateSkillSourcesApp(out var input, out var vm, new FakeSkillFeedProbe(false, "probe failed"));

        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter);
        EnqueueBackspaces(input, "https://old.example.test".Length);
        input.EnqueueString("https://new.example.test");
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(SkillSourcesScreen.SourceDetail, vm.Screen.Value);
        using var doc = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var feed = Assert.Single(doc.RootElement.GetProperty("SkillFeeds").GetProperty("Feeds").EnumerateArray());
        Assert.Equal("https://new.example.test", feed.GetProperty("Url").GetString());
    }

    [Fact]
    public async Task Skill_sources_inventory_space_toggles_source_enabled()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            "{\"configVersion\":1,\"SkillFeeds\":{\"Feeds\":[{\"Name\":\"custom-feed\",\"Url\":\"https://old.example.test\",\"Enabled\":true,\"TimeoutSeconds\":30}]}}");
        var app = CreateSkillSourcesApp(out var input, out _);

        input.EnqueueKey(ConsoleKey.Spacebar);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        using var doc = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var feed = Assert.Single(doc.RootElement.GetProperty("SkillFeeds").GetProperty("Feeds").EnumerateArray());
        Assert.False(feed.GetProperty("Enabled").GetBoolean());
    }

    [Fact]
    public async Task Skill_sources_local_detail_space_toggles_symlink_policy()
    {
        var externalDir = Path.Combine(_dir.Path, "team-skills");
        Directory.CreateDirectory(externalDir);
        File.WriteAllText(_paths.NetclawConfigPath,
            $"{{\"configVersion\":1,\"ExternalSkills\":{{\"Sources\":[{{\"Name\":\"team-skills\",\"Path\":\"{externalDir.Replace("\\", "\\\\", StringComparison.Ordinal)}\",\"Enabled\":true,\"AllowSymlinks\":false}}]}}}}");
        var app = CreateSkillSourcesApp(out var input, out _);

        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Spacebar);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        using var doc = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var source = Assert.Single(doc.RootElement.GetProperty("ExternalSkills").GetProperty("Sources").EnumerateArray());
        Assert.True(source.GetProperty("AllowSymlinks").GetBoolean());
    }

    [Fact]
    public async Task Skill_sources_remote_detail_enter_cycles_timeout()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            "{\"configVersion\":1,\"SkillFeeds\":{\"Feeds\":[{\"Name\":\"custom-feed\",\"Url\":\"https://old.example.test\",\"Enabled\":true,\"TimeoutSeconds\":30}]}}");
        var app = CreateSkillSourcesApp(out var input, out _);

        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        using var doc = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var feed = Assert.Single(doc.RootElement.GetProperty("SkillFeeds").GetProperty("Feeds").EnumerateArray());
        Assert.Equal(60, feed.GetProperty("TimeoutSeconds").GetInt32());
    }

    [Fact]
    public async Task Skill_sources_remote_detail_enter_removes_token()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            "{\"configVersion\":1,\"SkillFeeds\":{\"Feeds\":[{\"Name\":\"custom-feed\",\"Url\":\"https://old.example.test\",\"ApiKey\":\"plain-token\",\"Enabled\":true,\"TimeoutSeconds\":30}]}}");
        var app = CreateSkillSourcesApp(out var input, out _);

        input.EnqueueKey(ConsoleKey.Enter);
        for (var i = 0; i < 8; i++)
            input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        using var doc = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var feed = Assert.Single(doc.RootElement.GetProperty("SkillFeeds").GetProperty("Feeds").EnumerateArray());
        Assert.False(feed.TryGetProperty("ApiKey", out _));
    }

    [Fact]
    public async Task Skill_sources_remove_confirm_enter_removes_source()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            "{\"configVersion\":1,\"SkillFeeds\":{\"Feeds\":[{\"Name\":\"custom-feed\",\"Url\":\"https://old.example.test\",\"Enabled\":true,\"TimeoutSeconds\":30}]}}");
        var app = CreateSkillSourcesApp(out var input, out var vm);

        input.EnqueueKey(ConsoleKey.Delete);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(SkillSourcesScreen.Inventory, vm.Screen.Value);
        using var doc = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        Assert.False(doc.RootElement.TryGetProperty("SkillFeeds", out _));
    }

    [Fact]
    public async Task Telemetry_alerting_page_accepts_typed_and_pasted_values()
    {
        var app = CreateTelemetryAlertingApp(out var input, out var vm);

        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueString("http://");
        input.EnqueuePaste("127.0.0.1:4318");
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueuePaste("https://alerts.example.test/hook");
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal("http://127.0.0.1:4318", vm.OtlpEndpointDraft.Value);
        Assert.Equal("https://alerts.example.test/hook", vm.OutboundWebhookUrlDraft.Value);
    }

    private TerminaApplication CreateWorkspacesApp(out VirtualInputSource input, out WorkspacesConfigViewModel vm)
    {
        var terminal = new VirtualTerminal(120, 40);
        var virtualInput = new VirtualInputSource();
        input = virtualInput;
        var capturedVm = new WorkspacesConfigViewModel(_paths);

        var services = new ServiceCollection();
        services.AddSingleton<IAnsiTerminal>(terminal);
        services.AddTerminaVirtualInput(virtualInput);
        services.AddTermina("/workspaces", builder =>
        {
            builder.RegisterRoute<WorkspacesConfigPage, WorkspacesConfigViewModel>(
                "/workspaces",
                _ => new WorkspacesConfigPage(),
                _ => capturedVm);
        });

        var sp = services.BuildServiceProvider();
        vm = capturedVm!;
        return sp.GetRequiredService<TerminaApplication>();
    }

    private TerminaApplication CreateInboundWebhooksApp(out VirtualInputSource input, out InboundWebhooksConfigViewModel vm)
    {
        var terminal = new VirtualTerminal(120, 40);
        var virtualInput = new VirtualInputSource();
        input = virtualInput;
        var capturedVm = new InboundWebhooksConfigViewModel(_paths);

        var services = new ServiceCollection();
        services.AddSingleton<IAnsiTerminal>(terminal);
        services.AddTerminaVirtualInput(virtualInput);
        services.AddTermina("/inbound-webhooks", builder =>
        {
            builder.RegisterRoute<InboundWebhooksConfigPage, InboundWebhooksConfigViewModel>(
                "/inbound-webhooks",
                _ => new InboundWebhooksConfigPage(),
                _ => capturedVm);
        });

        var sp = services.BuildServiceProvider();
        vm = capturedVm!;
        return sp.GetRequiredService<TerminaApplication>();
    }

    private TerminaApplication CreateSkillSourcesApp(out VirtualInputSource input, out SkillSourcesConfigViewModel vm)
        => CreateSkillSourcesApp(out input, out vm, out _);

    private TerminaApplication CreateSkillSourcesApp(
        out VirtualInputSource input,
        out SkillSourcesConfigViewModel vm,
        ISkillFeedReachabilityProbe probe)
        => CreateSkillSourcesApp(out input, out vm, out _, probe);

    private TerminaApplication CreateSkillSourcesApp(
        out VirtualInputSource input,
        out SkillSourcesConfigViewModel vm,
        out VirtualTerminal terminal,
        ISkillFeedReachabilityProbe? probe = null)
    {
        terminal = new VirtualTerminal(120, 40);
        var virtualInput = new VirtualInputSource();
        input = virtualInput;
        var capturedVm = new SkillSourcesConfigViewModel(_paths, probe ?? new FakeSkillFeedProbe());

        var services = new ServiceCollection();
        services.AddSingleton<IAnsiTerminal>(terminal);
        services.AddTerminaVirtualInput(virtualInput);
        services.AddTermina("/skill-sources", builder =>
        {
            builder.RegisterRoute<SkillSourcesConfigPage, SkillSourcesConfigViewModel>(
                "/skill-sources",
                _ => new SkillSourcesConfigPage(),
                _ => capturedVm);
        });

        var sp = services.BuildServiceProvider();
        vm = capturedVm!;
        return sp.GetRequiredService<TerminaApplication>();
    }

    private TerminaApplication CreateTelemetryAlertingApp(out VirtualInputSource input, out TelemetryAlertingConfigViewModel vm)
    {
        var terminal = new VirtualTerminal(120, 40);
        var virtualInput = new VirtualInputSource();
        input = virtualInput;
        var capturedVm = new TelemetryAlertingConfigViewModel(_paths);

        var services = new ServiceCollection();
        services.AddSingleton<IAnsiTerminal>(terminal);
        services.AddTerminaVirtualInput(virtualInput);
        services.AddTermina("/telemetry-alerting", builder =>
        {
            builder.RegisterRoute<TelemetryAlertingConfigPage, TelemetryAlertingConfigViewModel>(
                "/telemetry-alerting",
                _ => new TelemetryAlertingConfigPage(),
                _ => capturedVm);
        });

        var sp = services.BuildServiceProvider();
        vm = capturedVm!;
        return sp.GetRequiredService<TerminaApplication>();
    }

    private sealed class FakeSkillFeedProbe : ISkillFeedReachabilityProbe
    {
        private readonly bool _success;
        private readonly string _message;

        public FakeSkillFeedProbe(bool success = true, string message = "reachable")
        {
            _success = success;
            _message = message;
        }

        public SkillFeedReachabilityResult Probe(string baseUrl, string? apiKey, int timeoutSeconds)
            => new(_success, _message);
    }

    private static void BeginRemoteUrlEntry(VirtualInputSource input, string url)
    {
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueString(url);
        input.EnqueueKey(ConsoleKey.Enter);
    }

    private static void EnqueueBackspaces(VirtualInputSource input, int count)
    {
        for (var i = 0; i < count; i++)
            input.EnqueueKey(ConsoleKey.Backspace);
    }

    private static int CountOccurrences(string value, string pattern, StringComparison comparison)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(pattern, index, comparison)) >= 0)
        {
            count++;
            index += pattern.Length;
        }

        return count;
    }
}

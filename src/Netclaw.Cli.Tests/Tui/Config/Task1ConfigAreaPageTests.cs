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
using Termina.Layout;
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
    public async Task Workspaces_page_choosing_a_directory_in_the_picker_saves_it()
    {
        // The page is the directory picker (no Tab, no typed form): Space chooses the highlighted
        // directory, which saves it as the workspaces directory.
        var target = Path.Combine(_dir.Path, "chosen-workspaces");
        Directory.CreateDirectory(target);
        var start = _paths.WorkspacesDirectory;
        var fileSystem = new StubFileSystemProvider(
            existingDirectories: [start, target],
            entries: new Dictionary<string, IReadOnlyList<FileSystemEntry>>
            {
                [start] = [StubFileSystemProvider.Dir(target)],
            });
        var app = CreateWorkspacesApp(out var input, out var vm, fileSystem);

        input.EnqueueKey(ConsoleKey.Spacebar); // choose the highlighted directory -> save.
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.True(vm.IsSaved.Value);
        Assert.Equal(target, vm.CurrentDirectory.Value);
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
    public async Task Skill_sources_local_path_screen_renders_directory_picker()
    {
        var app = CreateSkillSourcesApp(out var input, out _, out var terminal,
            fileSystem: SkillFolderPickerFs(out _));

        input.EnqueueKey(ConsoleKey.Enter); // Inventory -> Add local folder -> directory picker.
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var screen = terminal.ToString();
        Assert.Contains("Add a local skill folder.", screen, StringComparison.Ordinal);
        Assert.Contains("[Ctrl+N] new folder", screen, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Skill_sources_choosing_existing_directory_advances_without_persisting_incomplete_flow()
    {
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        var app = CreateSkillSourcesApp(out var input, out var vm, out _,
            fileSystem: SkillFolderPickerFs(out _));

        input.EnqueueKey(ConsoleKey.Enter);     // -> directory picker (the folder is highlighted).
        input.EnqueueKey(ConsoleKey.Spacebar);  // choose the folder -> AddLocalSymlinks.
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(SkillSourcesScreen.AddLocalSymlinks, vm.Screen.Value);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public async Task Skill_sources_local_name_enter_persists_source_to_external_skills()
    {
        var app = CreateSkillSourcesApp(out var input, out var vm, out _,
            fileSystem: SkillFolderPickerFs(out var externalDir));

        input.EnqueueKey(ConsoleKey.Enter);     // -> directory picker.
        input.EnqueueKey(ConsoleKey.Spacebar);  // choose the folder -> AddLocalSymlinks.
        input.EnqueueKey(ConsoleKey.Enter);     // symlinks default (No) -> AddLocalName.
        input.EnqueueKey(ConsoleKey.Enter);     // default name (folder basename) -> persist -> SourceDetail.
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
        // Default probe reports success. The reachability probe now runs off-loop in two phases:
        // the first Enter on AddRemoteUrl kicks it off (completes inline for the synchronous fake);
        // the second Enter acts on the success result and advances to the name/review step (open
        // servers never see the bearer-token field).
        var app = CreateSkillSourcesApp(out var input, out var vm);

        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueString("https://");
        input.EnqueuePaste("skills.example.test");
        input.EnqueueKey(ConsoleKey.Enter);     // phase 1: kick off probe
        input.EnqueueKey(ConsoleKey.Enter);     // phase 2: success -> AddRemoteName
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(SkillSourcesScreen.AddRemoteName, vm.Screen.Value);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public async Task Skill_sources_remote_url_unreachable_probe_fingerprints_without_dialog_before_persistence()
    {
        // Repurposed from a URL-step override-dialog test: the URL step no longer raises the
        // override dialog. An unreachable (non-auth) probe now fingerprints the URL and surfaces
        // a "save anyway" warning status while staying on AddRemoteUrl — no dialog, no persistence.
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        var app = CreateSkillSourcesApp(out var input, out var vm, out var terminal, new FakeSkillFeedProbe(false, "probe failed"));

        // BeginRemoteUrlEntry runs the first URL commit, which fires the no-auth probe once.
        BeginRemoteUrlEntry(input, "https://skills.example.test");
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(SkillSourcesScreen.AddRemoteUrl, vm.Screen.Value);
        Assert.Null(vm.ActiveValidationDialog.Value);
        Assert.Equal(ConfigStatusTone.Warning, vm.Status.Value.Tone);
        Assert.Contains("save anyway", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
        var screen = terminal.ToString();
        Assert.DoesNotContain("Skill Server Validation Warning", screen, StringComparison.Ordinal);
        Assert.True(screen.Contains("probe failed", StringComparison.OrdinalIgnoreCase),
            $"Expected probe failure in warning status. Screen:\n{terminal}");
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public async Task Skill_sources_remote_url_unreachable_open_server_second_enter_saves_anyway_reviews_name()
    {
        // For an OPEN (non-auth) server that probes unreachable, the first Enter on AddRemoteUrl
        // fingerprints the URL and warns "save anyway" (no dialog). A second Enter on the same URL
        // matches the fingerprint, skips the probe, and advances to AddRemoteName — nothing
        // persisted until the name commits.
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        var app = CreateSkillSourcesApp(out var input, out var vm, new FakeSkillFeedProbe(false, "probe failed"));

        // First URL commit (inside BeginRemoteUrlEntry) warns; a second Enter saves anyway.
        BeginRemoteUrlEntry(input, "https://skills.example.test");
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(SkillSourcesScreen.AddRemoteName, vm.Screen.Value);
        Assert.Null(vm.ActiveValidationDialog.Value);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public async Task Skill_sources_remote_url_unreachable_probe_preserves_url_draft_for_editing()
    {
        // Repurposed from a URL-step "back to edit" dialog test: the URL step no longer raises a
        // dialog, so there is no "back to edit" action. The equivalent guarantee under the
        // fingerprint model is that the typed URL is preserved on AddRemoteUrl so the user can
        // edit it after an unreachable probe instead of retyping.
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        var app = CreateSkillSourcesApp(out var input, out var vm, new FakeSkillFeedProbe(false, "probe failed"));

        BeginRemoteUrlEntry(input, "https://skills.example.test");
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(SkillSourcesScreen.AddRemoteUrl, vm.Screen.Value);
        Assert.Null(vm.ActiveValidationDialog.Value);
        Assert.Equal("https://skills.example.test", vm.Draft.Value);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public async Task Skill_sources_remote_token_commit_advances_to_name_without_blocking_on_reachability()
    {
        // Persist-now, validate-async: committing a bearer token no longer runs a blocking probe
        // (which froze the loop) and no longer raises an override dialog. The token screen is reached
        // via a 401 on the off-loop no-token URL probe (two-phase); a structurally-valid token then
        // advances straight to the name review. Reachability is validated later (Test action / review),
        // so an unreachable token does NOT block here. Nothing is persisted until the name commits.
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        var app = CreateSkillSourcesApp(out var input, out var vm,
            new FakeSkillFeedProbe(message: "probe failed", requiresAuth: true, failWithToken: true));

        BeginRemoteUrlEntry(input, "https://skills.example.test");
        input.EnqueueKey(ConsoleKey.Enter);     // URL phase 2: RequiresAuth -> reveal token field
        input.EnqueueString("secret-token");
        input.EnqueueKey(ConsoleKey.Enter);     // token commit -> advance to name (no block, no dialog)
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(SkillSourcesScreen.AddRemoteName, vm.Screen.Value);
        Assert.Null(vm.ActiveValidationDialog.Value);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public async Task Skill_sources_remote_url_success_probe_reviews_name_without_persisting_incomplete_flow()
    {
        // Repurposed from a URL-step "save anyway" dialog test: there is no URL-step override.
        // A reachable open server advances straight to the name/review screen with the suggested
        // name prefilled, still without persisting until the name commits. This preserves the
        // AddRemoteName render and suggested-name coverage the old dialog test asserted.
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        var app = CreateSkillSourcesApp(out var input, out var vm, out var terminal, new FakeSkillFeedProbe(true, "reachable"));

        // Two-phase off-loop probe: BeginRemoteUrlEntry's Enter kicks it off (phase 1, completes
        // inline for the synchronous fake); a second Enter advances to the name review (phase 2).
        BeginRemoteUrlEntry(input, "https://skills.example.test");
        input.EnqueueKey(ConsoleKey.Enter);     // phase 2: success -> AddRemoteName
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
    public async Task Skill_sources_remote_url_requiring_auth_reveals_token_entry()
    {
        // Repurposed from the auth-choice "pick Bearer" test: there is no auth-choice screen.
        // The bearer-token field is revealed only when the no-token probe reports RequiresAuth
        // (HTTP 401/403), with a warning prompting the user to enter a token.
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        var app = CreateSkillSourcesApp(out var input, out var vm,
            new FakeSkillFeedProbe(message: "auth required", requiresAuth: true));

        // Two-phase off-loop probe: the first Enter kicks off the no-auth probe (phase 1, 401);
        // the second Enter acts on the RequiresAuth result and reveals the bearer-token field.
        BeginRemoteUrlEntry(input, "https://skills.example.test");
        input.EnqueueKey(ConsoleKey.Enter);     // phase 2: RequiresAuth -> reveal token field
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(SkillSourcesScreen.AddRemoteToken, vm.Screen.Value);
        Assert.Equal(ConfigStatusTone.Warning, vm.Status.Value.Tone);
        Assert.Contains("bearer token to continue", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public async Task Skill_sources_remote_unreachable_token_feed_can_still_be_added_and_persists()
    {
        // Preserves the original intent (an unreachable auth feed can still be added) under the new
        // persist-now/validate-async model: the token re-probe no longer blocks with an override
        // dialog. The token screen is reached via the off-loop 401 URL probe (two-phase); a valid
        // token advances to the name review even though the feed is unreachable (failWithToken), and
        // committing the name persists the encrypted token.
        var app = CreateSkillSourcesApp(out var input, out var vm,
            new FakeSkillFeedProbe(message: "probe failed", requiresAuth: true, failWithToken: true));

        BeginRemoteUrlEntry(input, "https://skills.example.test");
        input.EnqueueKey(ConsoleKey.Enter);     // URL phase 2: RequiresAuth -> reveal token field
        input.EnqueueString("secret-token");
        input.EnqueueKey(ConsoleKey.Enter);     // token commit -> advance to name (no block, no dialog)
        input.EnqueueKey(ConsoleKey.Enter);     // name -> persist
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(SkillSourcesScreen.SourceDetail, vm.Screen.Value);
        Assert.Null(vm.ActiveValidationDialog.Value);
        var contents = File.ReadAllText(_paths.NetclawConfigPath);
        Assert.DoesNotContain("secret-token", contents, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(contents);
        var feed = Assert.Single(doc.RootElement.GetProperty("SkillFeeds").GetProperty("Feeds").EnumerateArray());
        Assert.Equal("https://skills.example.test", feed.GetProperty("Url").GetString());
        Assert.StartsWith("ENC:", feed.GetProperty("ApiKey").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Skill_sources_remote_bearer_name_enter_persists_encrypted_token_to_skill_feeds()
    {
        // requiresAuth probe: URL probe 401s and reveals the token field; the token re-probe
        // succeeds, advances to name, and the entered token is persisted encrypted. Each off-loop
        // probe is two-phase (kick off, then act on the inline-completed result on the next Enter).
        var app = CreateSkillSourcesApp(out var input, out var vm,
            new FakeSkillFeedProbe(message: "auth required", requiresAuth: true));

        BeginRemoteUrlEntry(input, "https://skills.example.test");
        input.EnqueueKey(ConsoleKey.Enter);     // URL phase 2: RequiresAuth -> reveal token field
        input.EnqueueString("secret-token");
        input.EnqueueKey(ConsoleKey.Enter);     // token phase 1: re-probe with token
        input.EnqueueKey(ConsoleKey.Enter);     // token phase 2: success -> AddRemoteName
        input.EnqueueKey(ConsoleKey.Enter);     // name -> persist
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
        // Default probe reports success. The off-loop probe is two-phase: the URL Enter kicks it
        // off, a second Enter advances to the name screen, and a third Enter on the name persists
        // an open feed with no ApiKey.
        var app = CreateSkillSourcesApp(out var input, out var vm);

        BeginRemoteUrlEntry(input, "https://skills.example.test");
        input.EnqueueKey(ConsoleKey.Enter);     // URL phase 2: success -> AddRemoteName
        input.EnqueueKey(ConsoleKey.Enter);     // name -> persist
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
        // OPEN-URL save-anyway path: the no-auth probe reports unreachable, so the first Enter on
        // AddRemoteUrl fingerprints the URL and warns "save anyway". A second Enter on the same URL
        // skips the probe and advances to AddRemoteName, and Enter on the name persists the feed
        // with no token (open server, null ApiKey).
        var app = CreateSkillSourcesApp(out var input, out var vm, new FakeSkillFeedProbe(false, "probe failed"));

        // URL + Enter (inside BeginRemoteUrlEntry) warns; a second Enter saves anyway -> AddRemoteName.
        BeginRemoteUrlEntry(input, "https://example.invalid");
        input.EnqueueKey(ConsoleKey.Enter);
        // Now on AddRemoteName -> Enter persists.
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
    public async Task Skill_sources_remote_change_url_persists_immediately_even_when_unreachable()
    {
        // Persist-now, validate-async: changing a remote feed URL no longer blocks on a "save anyway"
        // override (the probe ran synchronously and froze the loop). The new URL is persisted on the
        // first Enter and an off-loop warn-probe surfaces a non-blocking warning when unreachable.
        File.WriteAllText(_paths.NetclawConfigPath,
            "{\"configVersion\":1,\"SkillFeeds\":{\"Feeds\":[{\"Name\":\"custom-feed\",\"Url\":\"https://old.example.test\",\"Enabled\":true,\"TimeoutSeconds\":30}]}}");
        var app = CreateSkillSourcesApp(out var input, out var vm, new FakeSkillFeedProbe(false, "probe failed"));

        input.EnqueueKey(ConsoleKey.Enter);          // open the feed's detail
        input.EnqueueKey(ConsoleKey.DownArrow);      // move to the Change Location action
        input.EnqueueKey(ConsoleKey.Enter);          // open the URL editor
        EnqueueBackspaces(input, "https://old.example.test".Length);
        input.EnqueueString("https://new.example.test");
        input.EnqueueKey(ConsoleKey.Enter);          // persists now (unreachable warn-probe is off-loop)
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

        // Edit and save the OTLP endpoint on row 1, then open the "+ Add webhook"
        // row, type a URL into the form, and save it.
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueString("http://");
        input.EnqueuePaste("127.0.0.1:4318");
        input.EnqueueKey(ConsoleKey.Enter);     // save OTLP endpoint.
        input.EnqueueKey(ConsoleKey.DownArrow); // -> + Add webhook row (no webhooks yet).
        input.EnqueueKey(ConsoleKey.Enter);     // open the add form.
        input.EnqueueKey(ConsoleKey.DownArrow); // Name -> URL field.
        input.EnqueueString("https://");
        input.EnqueuePaste("alerts.example.test/hook");
        input.EnqueueKey(ConsoleKey.Enter);     // save webhook.
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal("http://127.0.0.1:4318", vm.OtlpEndpointDraft.Value);
        Assert.Equal(TelemetryConfigScreen.List, vm.Screen.Value);
        var webhook = Assert.Single(vm.Webhooks.Value);
        Assert.Equal("https://alerts.example.test/hook", webhook.Url);
    }

    private TerminaApplication CreateWorkspacesApp(
        out VirtualInputSource input,
        out WorkspacesConfigViewModel vm,
        IFileSystemProvider? fileSystem = null)
    {
        var terminal = new VirtualTerminal(120, 40);
        var virtualInput = new VirtualInputSource();
        input = virtualInput;
        var capturedVm = new WorkspacesConfigViewModel(_paths, fileSystem);

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
        ISkillFeedReachabilityProbe? probe = null,
        IFileSystemProvider? fileSystem = null)
    {
        terminal = new VirtualTerminal(120, 40);
        var virtualInput = new VirtualInputSource();
        input = virtualInput;
        var capturedVm = new SkillSourcesConfigViewModel(_paths, probe ?? new FakeSkillFeedProbe(), fileSystem);

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

    // A fake filesystem whose home directory contains exactly one real temp folder, so the
    // "add local folder" directory picker highlights it and Space chooses it deterministically.
    private StubFileSystemProvider SkillFolderPickerFs(out string externalDir)
    {
        externalDir = Path.Combine(_dir.Path, "team-skills");
        Directory.CreateDirectory(externalDir);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new StubFileSystemProvider(
            existingDirectories: [home, externalDir],
            entries: new Dictionary<string, IReadOnlyList<FileSystemEntry>>
            {
                [home] = [StubFileSystemProvider.Dir(externalDir)],
            });
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
        private readonly bool _requiresAuth;
        private readonly bool _failWithToken;

        public FakeSkillFeedProbe(
            bool success = true,
            string message = "reachable",
            bool requiresAuth = false,
            bool failWithToken = false)
        {
            _success = success;
            _message = message;
            _requiresAuth = requiresAuth;
            _failWithToken = failWithToken;
        }

        // Returns a synchronously-completed Task so RunProbeAsync runs inline on the loop thread
        // (a completed-task await never suspends): the probe result is applied before the event
        // loop pulls the next scripted key, keeping these full-loop tests deterministic.
        public Task<SkillFeedReachabilityResult> ProbeAsync(string baseUrl, string? apiKey, int timeoutSeconds, CancellationToken ct = default)
        {
            // Simulate an auth-gated server: the no-token probe returns 401 (RequiresAuth),
            // which reveals the bearer-token field. This is the only way to reach the
            // AddRemoteToken screen now. With a token supplied the re-probe either succeeds
            // (default) or, when failWithToken is set, fails with a non-auth error so the
            // token-step override dialog appears.
            if (_requiresAuth)
            {
                if (string.IsNullOrEmpty(apiKey))
                    return Task.FromResult(new SkillFeedReachabilityResult(false, _message, RequiresAuth: true));

                return Task.FromResult(_failWithToken
                    ? new SkillFeedReachabilityResult(false, _message)
                    : new SkillFeedReachabilityResult(true, _message));
            }

            return Task.FromResult(new SkillFeedReachabilityResult(_success, _message));
        }
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

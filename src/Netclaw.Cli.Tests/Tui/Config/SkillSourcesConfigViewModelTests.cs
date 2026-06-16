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
    public async Task Save_persists_external_directory_and_skill_feed_for_runtime_binding()
    {
        var externalDir = Path.Combine(_dir.Path, "team-skills");
        Directory.CreateDirectory(externalDir);
        using var vm = new SkillSourcesConfigViewModel(_paths, new FakeSkillFeedProbe(true, requiresAuth: true));

        AddLocalFolder(vm, externalDir, "team-skills");
        await AddRemoteServer(vm, "https://skills.example.test", "secret-token", "custom-feed");

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

    // The picker can't produce these inputs, but CommitAddLocalPath still validates them: a URL is
    // not a local path, and a bare name resolves to a well-formed but non-existent directory under
    // the temp dir. Both must surface an error and persist nothing.
    [Theory]
    [InlineData("https://example.test/skills", "local filesystem path")]
    [InlineData("missing-skills", "must already exist")]
    public void Save_rejects_invalid_external_directory_before_persistence(string draftInput, string expectedError)
    {
        var target = draftInput.Contains("://", StringComparison.Ordinal)
            ? draftInput
            : Path.Combine(_dir.Path, draftInput);
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        using var vm = new SkillSourcesConfigViewModel(_paths, new FakeSkillFeedProbe(true));

        BeginAddLocalFolder(vm);
        vm.CommitAddLocalPath(target);

        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Contains(expectedError, vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public void Adding_a_source_surfaces_config_write_failure_without_crashing()
    {
        var externalDir = Path.Combine(_dir.Path, "team-skills");
        Directory.CreateDirectory(externalDir);
        using var vm = new SkillSourcesConfigViewModel(_paths, new FakeSkillFeedProbe(true));

        // Force the config write to fail like a disk-full / permission-denied failure: AtomicFile
        // cannot replace a path that is a directory. LoadJsonDict treats it as missing, so the save's
        // read returns a skeleton and only the write throws — exercising the TryEditConfig guard that
        // now brackets the save read+write as one unit.
        File.Delete(_paths.NetclawConfigPath);
        Directory.CreateDirectory(_paths.NetclawConfigPath);

        // Drive the full add-local-folder flow (path -> symlinks -> name); the final commit persists
        // via SaveExternalConfig. (We inline rather than call AddLocalFolder, which asserts the
        // success-path screen transition that does not happen when the save fails.)
        BeginAddLocalFolder(vm);
        vm.CommitAddLocalPath(externalDir);
        vm.ActivateSelected();
        ReplaceDraft(vm, "team-skills");
        vm.ActivateSelected();

        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
    }

    [Fact]
    public async Task Adding_a_remote_source_surfaces_keyring_failure_without_crashing()
    {
        using var vm = new SkillSourcesConfigViewModel(_paths, new FakeSkillFeedProbe(true, requiresAuth: true));

        // Drive the remote-add flow up to (but not through) the final commit, which encrypts the token.
        BeginAddRemoteServer(vm);
        vm.AppendText("https://skills.example.test");
        vm.ActivateSelected();
        await vm.PendingProbe!;
        vm.ActivateSelected(); // RequiresAuth -> token field
        vm.AppendText("secret-token");
        vm.ActivateSelected();
        await vm.PendingProbe!;
        vm.ActivateSelected(); // success -> name review
        ReplaceDraft(vm, "custom-feed");

        // Make the DataProtection keys directory unusable (a file, not a directory) so the commit's
        // ProtectApiKeyForConfig().Protect() throws the way an unavailable / rotated key ring would.
        if (Directory.Exists(_paths.KeysDirectory))
            Directory.Delete(_paths.KeysDirectory, recursive: true);
        File.WriteAllText(_paths.KeysDirectory, "not a directory");

        vm.ActivateSelected(); // commit -> key-ring failure must surface, not crash the loop

        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
    }

    [Fact]
    public void Malformed_config_does_not_crash_construction_or_a_source_mutation()
    {
        File.WriteAllText(_paths.NetclawConfigPath, "{ this is not valid json ");

        // Construction (ReloadSources) must not throw on a malformed config — it degrades to an empty
        // source list with an error Status instead of leaving the page permanently inaccessible.
        using var vm = new SkillSourcesConfigViewModel(_paths, new FakeSkillFeedProbe(true));
        Assert.Empty(vm.Sources);

        // A mutation's pre-save read (LoadExternalConfig, now guarded by TryLoadExternalConfig) must
        // likewise surface an error rather than throwing into the Termina event loop.
        var externalDir = Path.Combine(_dir.Path, "team-skills");
        Directory.CreateDirectory(externalDir);
        BeginAddLocalFolder(vm);
        vm.CommitAddLocalPath(externalDir);
        vm.ActivateSelected();
        ReplaceDraft(vm, "team-skills");
        vm.ActivateSelected();

        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
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
    public async Task Save_rejects_multiline_skill_feed_api_key_before_persistence()
    {
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        using var vm = new SkillSourcesConfigViewModel(_paths, new FakeSkillFeedProbe(true, requiresAuth: true));

        // Two-phase add-remote review: the URL probe (off-loop) reports RequiresAuth; phase 2 reveals
        // the token field. A multiline token is then rejected by structural validation before any
        // re-probe or persistence.
        BeginAddRemoteServer(vm);
        vm.AppendText("https://skills.example.test");
        vm.ActivateSelected();              // phase 1: no-auth probe -> 401
        await vm.PendingProbe!;
        vm.ActivateSelected();              // phase 2: RequiresAuth -> reveal token field
        Assert.Equal(SkillSourcesScreen.AddRemoteToken, vm.Screen.Value);
        vm.AppendText("token\nnext");
        vm.ActivateSelected();

        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Contains("single-line", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public async Task Save_blocks_unreachable_skill_feed_until_second_save_anyway()
    {
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        using var vm = new SkillSourcesConfigViewModel(_paths, new FakeSkillFeedProbe(false));

        // Two-phase: the first Enter on AddRemoteUrl kicks off the off-loop probe (phase 1). Once it
        // completes (unreachable, non-auth) the status warns "save anyway" and the screen stays on
        // AddRemoteUrl — nothing persisted. A second Enter (phase 2) acts on that result and advances
        // to the name review. This preserves the original intent: an unreachable open server can still
        // be added via a deliberate second Enter.
        BeginAddRemoteServer(vm);
        vm.AppendText("https://skills.example.test");
        vm.ActivateSelected();              // phase 1: kick off probe
        await vm.PendingProbe!;

        Assert.Equal(SkillSourcesScreen.AddRemoteUrl, vm.Screen.Value);
        Assert.Equal(ConfigStatusTone.Warning, vm.Status.Value.Tone);
        Assert.Contains("save anyway", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));

        vm.ActivateSelected();              // phase 2: save anyway -> name review
        Assert.Equal(SkillSourcesScreen.AddRemoteName, vm.Screen.Value);
        ReplaceDraft(vm, "custom-feed");
        vm.ActivateSelected();

        var feed = SingleFeedSection();
        Assert.Equal("https://skills.example.test", feed["Url"]);
        Assert.Null(feed["ApiKey"]);
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
    public async Task Location_detail_row_opens_remote_url_editor()
    {
        using var vm = new SkillSourcesConfigViewModel(_paths, new FakeSkillFeedProbe(true, requiresAuth: true));

        await AddRemoteServer(vm, "https://skills.example.test", "secret-token", "custom-feed");
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

    [Fact]
    public void Rotate_token_persists_new_encrypted_token_and_invalidates_old()
    {
        var protector = SecretsProtection.CreateProtector(_paths);
        var oldEncrypted = protector.Protect("old-token");
        File.WriteAllText(_paths.NetclawConfigPath,
            $"{{\"configVersion\":1,\"SkillFeeds\":{{\"Feeds\":[{{\"Name\":\"custom-feed\",\"Url\":\"https://old.example.test\",\"ApiKey\":\"{oldEncrypted}\",\"Enabled\":true}}]}}}}");
        using var vm = new SkillSourcesConfigViewModel(_paths, new FakeSkillFeedProbe(true));

        BeginRotateToken(vm, "custom-feed");
        ReplaceDraft(vm, "new-token");
        vm.ActivateSelected();

        var rotated = SingleFeedSection()["ApiKey"];
        Assert.NotNull(rotated);
        Assert.StartsWith("ENC:", rotated!, StringComparison.Ordinal);
        Assert.NotEqual(oldEncrypted, rotated);
        Assert.Equal("new-token", protector.Unprotect(rotated!));
        Assert.NotEqual("old-token", protector.Unprotect(rotated!));
        Assert.DoesNotContain("new-token", File.ReadAllText(_paths.NetclawConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Rotate_token_rejects_blank_and_leaves_existing_token_untouched()
    {
        var protector = SecretsProtection.CreateProtector(_paths);
        var oldEncrypted = protector.Protect("old-token");
        File.WriteAllText(_paths.NetclawConfigPath,
            $"{{\"configVersion\":1,\"SkillFeeds\":{{\"Feeds\":[{{\"Name\":\"custom-feed\",\"Url\":\"https://old.example.test\",\"ApiKey\":\"{oldEncrypted}\",\"Enabled\":true}}]}}}}");
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        using var vm = new SkillSourcesConfigViewModel(_paths, new FakeSkillFeedProbe(true));

        BeginRotateToken(vm, "custom-feed");
        ReplaceDraft(vm, "   ");
        vm.ActivateSelected();

        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Contains("New bearer token is required", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));
        Assert.Equal(oldEncrypted, SingleFeedSection()["ApiKey"]);
    }

    [Fact]
    public async Task Rotate_token_persists_immediately_then_warns_when_feed_unreachable()
    {
        // Persist-now, validate-async: rotating a token no longer blocks on a reachability gate
        // (a blocking probe froze the loop). The new token is persisted on the first Enter and an
        // off-loop warn-probe surfaces a non-blocking Warning when the feed is unreachable — the
        // rotation is NOT reverted.
        var protector = SecretsProtection.CreateProtector(_paths);
        var oldEncrypted = protector.Protect("old-token");
        File.WriteAllText(_paths.NetclawConfigPath,
            $"{{\"configVersion\":1,\"SkillFeeds\":{{\"Feeds\":[{{\"Name\":\"custom-feed\",\"Url\":\"https://old.example.test\",\"ApiKey\":\"{oldEncrypted}\",\"Enabled\":true}}]}}}}");
        var probe = new CountingSkillFeedProbe(success: false);
        using var vm = new SkillSourcesConfigViewModel(_paths, probe);

        BeginRotateToken(vm, "custom-feed");
        ReplaceDraft(vm, "new-token");
        vm.ActivateSelected();              // persists now, then kicks off the off-loop warn-probe
        await vm.PendingProbe!;

        // The rotation is persisted (not reverted), and the warn-probe flagged the unreachable feed.
        var rotated = SingleFeedSection()["ApiKey"];
        Assert.NotNull(rotated);
        Assert.Equal("new-token", protector.Unprotect(rotated!));
        Assert.NotEqual(oldEncrypted, rotated);
        Assert.Equal(1, probe.ProbeCount);
        Assert.Equal(ConfigStatusTone.Warning, vm.Status.Value.Tone);
        Assert.Contains("unreachable", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddLocalPath_is_a_directory_picker_so_typing_does_not_change_the_draft()
    {
        // The add-local-folder step is an interactive directory picker now, not a text field:
        // keystrokes route to the picker, so AppendText must be inert and IsTextEntryActive false.
        using var vm = new SkillSourcesConfigViewModel(_paths, new FakeSkillFeedProbe(true));
        BeginAddLocalFolder(vm);

        vm.AppendText("/tmp/should-be-ignored");

        Assert.False(vm.IsTextEntryActive);
        Assert.Equal(string.Empty, vm.Draft.Value);
        Assert.Equal(SkillSourcesScreen.AddLocalPath, vm.Screen.Value);
    }

    [Fact]
    public void CommitAddLocalPath_from_picker_advances_to_symlinks()
    {
        // CommitAddLocalPath is the picker's SelectionConfirmed target: a chosen (existing)
        // directory validates and advances to the symlink-security step.
        var folder = Path.Combine(_dir.Path, "picked-skill-folder");
        Directory.CreateDirectory(folder);
        using var vm = new SkillSourcesConfigViewModel(_paths, new FakeSkillFeedProbe(true));
        BeginAddLocalFolder(vm);

        vm.CommitAddLocalPath(folder);

        Assert.Equal(SkillSourcesScreen.AddLocalSymlinks, vm.Screen.Value);
    }

    [Fact]
    public void CreateAndSelectFolder_creates_a_new_folder_and_advances()
    {
        // The inline "new folder" affordance: create a subdir under the picker's location, then
        // commit it (it now exists, so it advances to the symlink-security step).
        var parent = Path.Combine(_dir.Path, "parent");
        Directory.CreateDirectory(parent);
        using var vm = new SkillSourcesConfigViewModel(_paths, new FakeSkillFeedProbe(true));
        BeginAddLocalFolder(vm);

        vm.CreateAndSelectFolder(parent, "fresh-skills");

        Assert.True(Directory.Exists(Path.Combine(parent, "fresh-skills")));
        Assert.Equal(SkillSourcesScreen.AddLocalSymlinks, vm.Screen.Value);
    }

    [Fact]
    public void CreateAndSelectFolder_rejects_an_invalid_name_and_stays_on_the_picker()
    {
        var parent = Path.Combine(_dir.Path, "parent");
        Directory.CreateDirectory(parent);
        using var vm = new SkillSourcesConfigViewModel(_paths, new FakeSkillFeedProbe(true));
        BeginAddLocalFolder(vm);

        vm.CreateAndSelectFolder(parent, "bad/name");

        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Equal(SkillSourcesScreen.AddLocalPath, vm.Screen.Value);
    }

    [Fact]
    public async Task Probe_runs_off_the_loop_and_does_not_block_input()
    {
        // Regression for the deep-review finding: the reachability probe used to run synchronously
        // on the single-threaded TUI loop (HttpClient.Send), freezing input for up to 10s. It now
        // runs off-loop. Gate the fake so the probe is still in flight when the triggering call
        // returns: PendingProbe must be non-null and NOT complete (the call did NOT block), and the
        // status shows the in-progress "Testing…" message.
        File.WriteAllText(_paths.NetclawConfigPath,
            "{\"configVersion\":1,\"SkillFeeds\":{\"Feeds\":[{\"Name\":\"custom-feed\",\"Url\":\"https://feed.example.test\",\"Enabled\":true}]}}");
        var gate = new TaskCompletionSource();
        var probe = new FakeSkillFeedProbe(success: true) { Gate = gate };
        using var vm = new SkillSourcesConfigViewModel(_paths, probe);

        OpenRemoteDetail(vm, "custom-feed");
        MoveToDetailAction(vm, SkillSourceDetailAction.TestConnection);
        vm.ActivateSelected();              // kicks off the probe and returns WITHOUT blocking

        Assert.NotNull(vm.PendingProbe);
        Assert.False(vm.PendingProbe!.IsCompleted); // proof the call did not block the loop
        Assert.Equal(ConfigStatusTone.Neutral, vm.Status.Value.Tone);
        Assert.Contains("Testing skill server", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);

        gate.SetResult();
        await vm.PendingProbe!;

        Assert.Equal(ConfigStatusTone.Success, vm.Status.Value.Tone);
        Assert.Contains("reachable", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Probe_is_cancelled_on_dispose()
    {
        // Disposing the VM while a probe is in flight cancels it: the gated continuation must NOT
        // apply its result (status stays on "Testing…") and awaiting the task must not throw.
        File.WriteAllText(_paths.NetclawConfigPath,
            "{\"configVersion\":1,\"SkillFeeds\":{\"Feeds\":[{\"Name\":\"custom-feed\",\"Url\":\"https://feed.example.test\",\"Enabled\":true}]}}");
        var gate = new TaskCompletionSource();
        var probe = new FakeSkillFeedProbe(success: true) { Gate = gate };
        var vm = new SkillSourcesConfigViewModel(_paths, probe);

        OpenRemoteDetail(vm, "custom-feed");
        MoveToDetailAction(vm, SkillSourceDetailAction.TestConnection);
        vm.ActivateSelected();              // probe in flight (gated)
        var pending = vm.PendingProbe;
        Assert.NotNull(pending);
        // Snapshot the status before Dispose (Dispose disposes the R3 ReactiveProperty).
        var statusBeforeDispose = vm.Status.Value;

        vm.Dispose();                       // cancels the in-flight probe
        gate.SetResult();                   // release the gate; cancellation should win
        await pending!;                     // completes without throwing (cancellation swallowed)

        // The continuation dropped the result quietly — the status was never advanced past "Testing…".
        Assert.Equal(ConfigStatusTone.Neutral, statusBeforeDispose.Tone);
        Assert.Contains("Testing skill server", statusBeforeDispose.Text, StringComparison.OrdinalIgnoreCase);
    }

    // A migrated/hand-edited config may store a bearer token unencrypted. The editor must NOT silently
    // accept and use it: a plaintext token is flagged (so the operator can rotate/re-encrypt it),
    // while an ENC:-protected token is not flagged.
    [Theory]
    [InlineData("raw-plaintext-token", true)]
    [InlineData("ENC:protected-blob", false)]
    public void Feed_token_stored_as_plaintext_is_flagged_not_silently_accepted(string storedApiKey, bool expectedPlaintext)
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            $"{{\"configVersion\":1,\"SkillFeeds\":{{\"Feeds\":[{{\"Name\":\"feed-x\",\"Url\":\"https://feed.example.test\",\"ApiKey\":\"{storedApiKey}\",\"Enabled\":true}}]}}}}");
        using var vm = new SkillSourcesConfigViewModel(_paths, new FakeSkillFeedProbe(true));

        var feed = vm.Sources.Single(s => s.Kind == SkillSourceKind.RemoteSkillServer);
        Assert.Equal(expectedPlaintext, feed.ApiKeyIsPlaintext);

        OpenRemoteDetail(vm, "feed-x");
        var authRow = vm.DetailRows.Single(r => r.Action == SkillSourceDetailAction.Authentication);
        Assert.Equal(expectedPlaintext ? ConfigStatusTone.Warning : ConfigStatusTone.Neutral, authRow.Tone);
        if (expectedPlaintext)
            Assert.Contains("PLAINTEXT", authRow.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void Config_write_io_failure_surfaces_an_error_and_persists_nothing()
    {
        // Inject a real disk-write failure (config dir made read-only) and confirm the save surfaces
        // an error status, does not advance to the detail screen, and persists nothing — instead of
        // throwing IOException into the Termina event loop. chmod-based injection is Unix-only.
        if (OperatingSystem.IsWindows())
            return;

        var externalDir = Path.Combine(_dir.Path, "team-skills");
        Directory.CreateDirectory(externalDir);
        using var vm = new SkillSourcesConfigViewModel(_paths, new FakeSkillFeedProbe(true));

        BeginAddLocalFolder(vm);
        vm.CommitAddLocalPath(externalDir);
        vm.ActivateSelected();              // symlinks → name screen (no write yet)
        ReplaceDraft(vm, "team-skills");

        var configDir = Path.GetDirectoryName(_paths.NetclawConfigPath)!;
        var originalMode = File.GetUnixFileMode(configDir);
        var before = File.ReadAllText(_paths.NetclawConfigPath);
        File.SetUnixFileMode(configDir, UnixFileMode.UserRead | UnixFileMode.UserExecute); // no write
        try
        {
            vm.ActivateSelected();          // CommitAddLocalName → SaveNewLocalSource → write fails
        }
        finally
        {
            File.SetUnixFileMode(configDir, originalMode);
        }

        Assert.Equal(ConfigStatusTone.Error, vm.Status.Value.Tone);
        Assert.Contains("Could not save", vm.Status.Value.Text, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(SkillSourcesScreen.SourceDetail, vm.Screen.Value); // did not falsely advance
        Assert.Equal(before, File.ReadAllText(_paths.NetclawConfigPath));  // nothing persisted
    }

    private static void BeginRotateToken(SkillSourcesConfigViewModel vm, string name)
    {
        OpenRemoteDetail(vm, name);
        MoveToDetailAction(vm, SkillSourceDetailAction.RotateToken);
        vm.ActivateSelected();
        Assert.Equal(SkillSourcesScreen.AddRemoteToken, vm.Screen.Value);
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
        // AddLocalPath is a directory picker; CommitAddLocalPath is what its SelectionConfirmed
        // calls with the chosen path (replaces the former type-the-path-then-Enter flow).
        vm.CommitAddLocalPath(path);
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

    // Drives the probe-driven add flow for an auth-gated server. The reachability probe now runs
    // OFF the loop, so each probe is two-phase: the first ActivateSelected kicks it off (await
    // PendingProbe), the second ActivateSelected acts on the completed result (reveal token / advance
    // to name). The vm must be constructed with a requiresAuth FakeSkillFeedProbe.
    private static async Task AddRemoteServer(SkillSourcesConfigViewModel vm, string url, string token, string name)
    {
        BeginAddRemoteServer(vm);
        vm.AppendText(url);
        vm.ActivateSelected();              // phase 1: no-auth probe -> 401
        await vm.PendingProbe!;
        vm.ActivateSelected();              // phase 2: RequiresAuth -> reveal token field
        Assert.Equal(SkillSourcesScreen.AddRemoteToken, vm.Screen.Value);
        vm.AppendText(token);
        vm.ActivateSelected();              // phase 1: re-probe with token -> success
        await vm.PendingProbe!;
        vm.ActivateSelected();              // phase 2: success -> advance to name review
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

    private sealed class FakeSkillFeedProbe(bool success, bool requiresAuth = false, bool failWithToken = false)
        : ISkillFeedReachabilityProbe
    {
        // When set, ProbeAsync blocks on this gate before returning so tests can stage an in-flight
        // probe (proving the call returned without blocking the loop, and that it is cancellable).
        public TaskCompletionSource? Gate { get; set; }

        public async Task<SkillFeedReachabilityResult> ProbeAsync(string baseUrl, string? apiKey, int timeoutSeconds, CancellationToken ct = default)
        {
            if (Gate is not null)
                await Gate.Task.WaitAsync(ct);

            // Simulate an auth-gated server: 401 (RequiresAuth) until a bearer token is
            // supplied. Drives the probe-driven token disclosure path. With a token the
            // re-probe either succeeds (default) or, when failWithToken is set, fails with a
            // non-auth error so the token-step override dialog appears.
            if (requiresAuth)
            {
                if (string.IsNullOrEmpty(apiKey))
                    return new SkillFeedReachabilityResult(false, "auth required", RequiresAuth: true);

                return failWithToken
                    ? new SkillFeedReachabilityResult(false, "unreachable")
                    : new SkillFeedReachabilityResult(true, "reachable");
            }

            return success
                ? new SkillFeedReachabilityResult(true, "reachable")
                : new SkillFeedReachabilityResult(false, "unreachable");
        }
    }

    private sealed class CountingSkillFeedProbe(bool success) : ISkillFeedReachabilityProbe
    {
        public int ProbeCount { get; private set; }

        public Task<SkillFeedReachabilityResult> ProbeAsync(string baseUrl, string? apiKey, int timeoutSeconds, CancellationToken ct = default)
        {
            ProbeCount++;
            return Task.FromResult(success
                ? new SkillFeedReachabilityResult(true, "reachable")
                : new SkillFeedReachabilityResult(false, "unreachable"));
        }
    }
}

// -----------------------------------------------------------------------
// <copyright file="ApprovalsCommandTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Approvals;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Approvals;

public sealed class ApprovalsCommandTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly StringWriter _output = new();
    private readonly ToolApprovalStore _store;

    public ApprovalsCommandTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        _store = new ToolApprovalStore(_paths.ToolApprovalsPath);
    }

    public void Dispose()
    {
        _output.Dispose();
        _dir.Dispose();
    }

    private static ApprovalEntry Verb(string verb) => new() { Verb = verb, Directory = null };
    private static ApprovalEntry InDir(string verb, string dir) => new() { Verb = verb, Directory = dir };

    private void SeedDefault()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("git push"));
        _store.AddApproval(TrustAudience.Personal, "shell_execute", InDir("grep", "/home/user/logs"));
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("npm install"));
        _store.AddApproval(TrustAudience.Personal, "file_write", InDir("file_write", "/tmp/scratch"));
        _store.AddApproval(TrustAudience.Public, "shell_execute", Verb("ls"));
    }

    [Fact]
    public async Task List_empty_file_prints_message_and_exits_zero()
    {
        var exit = await ApprovalsCommand.RunAsync(["approvals", "list"], _paths, _output);

        Assert.Equal(0, exit);
        Assert.Contains("No persistent approvals.", _output.ToString());
    }

    [Fact]
    public async Task List_with_entries_groups_by_audience_and_tool()
    {
        SeedDefault();

        var exit = await ApprovalsCommand.RunAsync(["approvals", "list"], _paths, _output);

        Assert.Equal(0, exit);
        var text = _output.ToString();
        Assert.Contains("personal / shell_execute", text);
        Assert.Contains("personal / file_write", text);
        Assert.Contains("public / shell_execute", text);
        Assert.Contains("git push anywhere", text);
        Assert.Contains("grep in /home/user/logs", text);
    }

    [Fact]
    public async Task List_json_emits_typed_entry_shape()
    {
        SeedDefault();

        await ApprovalsCommand.RunAsync(["approvals", "list", "--json"], _paths, _output);

        using var doc = JsonDocument.Parse(_output.ToString());
        var audiences = doc.RootElement.GetProperty("audiences");
        var personalShell = audiences.GetProperty("personal").GetProperty("shell_execute");
        var verbs = personalShell.EnumerateArray().Select(e => e.GetProperty("verb").GetString()).ToList();
        Assert.Contains("git push", verbs);
        Assert.Contains("grep", verbs);
        Assert.Contains("npm install", verbs);

        // The grep entry should carry its directory; "git push" should not.
        var grep = personalShell.EnumerateArray().Single(e => e.GetProperty("verb").GetString() == "grep");
        Assert.Equal("/home/user/logs", grep.GetProperty("directory").GetString());

        var gitPush = personalShell.EnumerateArray().Single(e => e.GetProperty("verb").GetString() == "git push");
        Assert.False(gitPush.TryGetProperty("directory", out _));
    }

    [Fact]
    public async Task List_filters_by_audience_and_tool()
    {
        SeedDefault();

        await ApprovalsCommand.RunAsync(
            ["approvals", "list", "--audience", "personal", "--tool", "shell_execute"],
            _paths, _output);

        var text = _output.ToString();
        Assert.Contains("personal / shell_execute", text);
        Assert.DoesNotContain("file_write", text);
        Assert.DoesNotContain("public / shell_execute", text);
    }

    [Fact]
    public async Task Revoke_global_wildcard_by_anywhere_form_removes_entry()
    {
        SeedDefault();

        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "revoke", "git push anywhere", "--audience", "personal", "--tool", "shell_execute"],
            _paths, _output);

        Assert.Equal(0, exit);
        var remaining = _store.GetApprovedEntries(TrustAudience.Personal, "shell_execute");
        Assert.DoesNotContain(remaining, e => e.Verb == "git push" && e.Directory is null);
        Assert.Contains("Removed 'git push anywhere'", _output.ToString());
    }

    [Fact]
    public async Task Revoke_no_match_exits_one_and_does_not_modify_file()
    {
        SeedDefault();
        var beforeCount = _store.GetApprovedEntries(TrustAudience.Personal, "shell_execute").Count;

        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "revoke", "git pull anywhere", "--audience", "personal", "--tool", "shell_execute"],
            _paths, _output);

        Assert.Equal(1, exit);
        Assert.Equal(beforeCount, _store.GetApprovedEntries(TrustAudience.Personal, "shell_execute").Count);
        Assert.Contains("No matching approval found.", _output.ToString());
    }

    [Fact]
    public async Task Revoke_tool_all_clears_every_entry_for_tool()
    {
        SeedDefault();

        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "revoke", "--tool", "shell_execute", "--all"],
            _paths, _output);

        Assert.Equal(0, exit);
        Assert.Empty(_store.GetApprovedEntries(TrustAudience.Personal, "shell_execute"));
        Assert.Empty(_store.GetApprovedEntries(TrustAudience.Public, "shell_execute"));
        Assert.Single(_store.GetApprovedEntries(TrustAudience.Personal, "file_write"));
    }

    [Fact]
    public async Task Revoke_tool_all_scoped_by_audience_leaves_others_alone()
    {
        SeedDefault();

        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "revoke", "--tool", "shell_execute", "--all", "--audience", "personal"],
            _paths, _output);

        Assert.Equal(0, exit);
        Assert.Empty(_store.GetApprovedEntries(TrustAudience.Personal, "shell_execute"));

        var publicShell = _store.GetApprovedEntries(TrustAudience.Public, "shell_execute");
        Assert.Single(publicShell);
        Assert.Equal("ls", publicShell[0].Verb);
    }

    [Fact]
    public async Task Revoke_all_without_tool_exits_one_and_does_not_modify_file()
    {
        SeedDefault();
        var beforeCount = _store.GetApprovedEntries(TrustAudience.Personal, "shell_execute").Count;

        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "revoke", "--all"],
            _paths, _output);

        Assert.Equal(1, exit);
        Assert.Equal(beforeCount, _store.GetApprovedEntries(TrustAudience.Personal, "shell_execute").Count);
        Assert.Contains("--all requires --tool", _output.ToString());
    }

    [Fact]
    public async Task Unknown_audience_flag_exits_one()
    {
        SeedDefault();

        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "list", "--audience", "foo"],
            _paths, _output);

        Assert.Equal(1, exit);
        Assert.Contains("Unknown audience 'foo'", _output.ToString());
    }

    [Fact]
    public async Task Audience_flag_without_value_exits_one_with_specific_message()
    {
        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "list", "--audience"],
            _paths, _output);

        Assert.Equal(1, exit);
        Assert.Contains("--audience requires a value", _output.ToString());
    }

    [Fact]
    public async Task Tool_flag_without_value_exits_one_with_specific_message()
    {
        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "revoke", "git push", "--tool"],
            _paths, _output);

        Assert.Equal(1, exit);
        Assert.Contains("--tool requires a value", _output.ToString());
    }

    [Fact]
    public async Task Help_subcommand_exits_zero_and_prints_usage()
    {
        var exit = await ApprovalsCommand.RunAsync(["approvals", "help"], _paths, _output);

        Assert.Equal(0, exit);
        Assert.Contains("Usage: netclaw approvals", _output.ToString());
    }

    [Fact]
    public async Task Revoke_unscoped_removes_match_across_audiences()
    {
        // Same global wildcard stored under two audiences; unscoped revoke should hit both.
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("ls"));
        _store.AddApproval(TrustAudience.Public, "shell_execute", Verb("ls"));

        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "revoke", "ls anywhere"],
            _paths, _output);

        Assert.Equal(0, exit);
        Assert.Empty(_store.GetApprovedEntries(TrustAudience.Personal, "shell_execute"));
        Assert.Empty(_store.GetApprovedEntries(TrustAudience.Public, "shell_execute"));
    }

    // ── Folder-scoped revoke ──

    [Fact]
    public async Task Revoke_folder_scoped_form_removes_entry_with_matching_directory()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", InDir("git remote", "/home/user/repos/foo"));

        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "revoke", "git remote in /home/user/repos/foo"],
            _paths, _output);

        Assert.Equal(0, exit);
        Assert.Empty(_store.GetApprovedEntries(TrustAudience.Personal, "shell_execute"));
    }

    [Fact]
    public async Task Revoke_folder_scoped_form_does_not_match_global_wildcard()
    {
        // The store has a (verb, null) entry; a folder-scoped revoke should not remove it.
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("git remote"));

        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "revoke", "git remote in /home/user/repos/foo"],
            _paths, _output);

        Assert.Equal(1, exit);
        var remaining = _store.GetApprovedEntries(TrustAudience.Personal, "shell_execute");
        Assert.Single(remaining);
        Assert.Null(remaining[0].Directory);
    }

    [Fact]
    public async Task Revoke_unrecognized_pattern_exits_one_with_clear_message()
    {
        // No "anywhere" suffix and no " in " separator — not a valid revoke pattern.
        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "revoke", "git remote"],
            _paths, _output);

        Assert.Equal(1, exit);
        var output = _output.ToString();
        Assert.Contains("Could not parse revoke pattern", output);
        Assert.Contains("'<verb> in <directory>' or '<verb> anywhere'", output);
    }

    // ── trust-verb ──

    [Fact]
    public async Task TrustVerb_adds_global_wildcard_with_default_audience_and_tool()
    {
        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "trust-verb", "freshdesk"],
            _paths, _output);

        Assert.Equal(0, exit);
        var entries = _store.GetApprovedEntries(TrustAudience.Personal, "shell_execute");
        Assert.Single(entries);
        Assert.Equal("freshdesk", entries[0].Verb);
        Assert.Null(entries[0].Directory);
        Assert.Contains("Trusted 'freshdesk anywhere'", _output.ToString());
    }

    [Fact]
    public async Task TrustVerb_is_idempotent_on_repeated_invocation()
    {
        await ApprovalsCommand.RunAsync(["approvals", "trust-verb", "freshdesk"], _paths, _output);
        _output.GetStringBuilder().Clear();

        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "trust-verb", "freshdesk"],
            _paths, _output);

        Assert.Equal(0, exit);
        var entries = _store.GetApprovedEntries(TrustAudience.Personal, "shell_execute");
        Assert.Single(entries);
        Assert.Contains("No changes", _output.ToString());
    }

    [Fact]
    public async Task TrustVerb_honors_audience_and_tool_flags()
    {
        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "trust-verb", "freshdesk", "--audience", "team", "--tool", "shell_execute"],
            _paths, _output);

        Assert.Equal(0, exit);
        Assert.Single(_store.GetApprovedEntries(TrustAudience.Team, "shell_execute"));
        Assert.Empty(_store.GetApprovedEntries(TrustAudience.Personal, "shell_execute"));
    }

    [Fact]
    public async Task TrustVerb_without_verb_argument_exits_one_with_usage()
    {
        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "trust-verb"],
            _paths, _output);

        Assert.Equal(1, exit);
        Assert.Contains("Usage: netclaw approvals trust-verb", _output.ToString());
    }

    [Fact]
    public async Task TrustVerb_unknown_audience_exits_one()
    {
        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "trust-verb", "freshdesk", "--audience", "bogus"],
            _paths, _output);

        Assert.Equal(1, exit);
        Assert.Contains("Unknown audience 'bogus'", _output.ToString());
    }

    // ── help mentions trust-verb ──

    [Fact]
    public async Task Help_lists_trust_verb_subcommand()
    {
        await ApprovalsCommand.RunAsync(["approvals", "help"], _paths, _output);

        var output = _output.ToString();
        Assert.Contains("trust-verb", output);
        Assert.Contains("global-wildcard", output);
    }
}

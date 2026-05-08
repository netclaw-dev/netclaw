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

    private void SeedDefault()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", "git push");
        _store.AddApproval(TrustAudience.Personal, "shell_execute", "/home/user/logs/");
        _store.AddApproval(TrustAudience.Personal, "shell_execute", "npm install");
        _store.AddApproval(TrustAudience.Personal, "file_write", "/tmp/scratch/");
        _store.AddApproval(TrustAudience.Public, "shell_execute", "ls");
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
        Assert.Contains("git push", text);
        Assert.Contains("/tmp/scratch/", text);
    }

    [Fact]
    public async Task List_json_emits_audience_tool_pattern_shape()
    {
        SeedDefault();

        await ApprovalsCommand.RunAsync(["approvals", "list", "--json"], _paths, _output);

        using var doc = JsonDocument.Parse(_output.ToString());
        var audiences = doc.RootElement.GetProperty("audiences");
        var personalShell = audiences.GetProperty("personal").GetProperty("shell_execute");
        var patterns = personalShell.EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("git push", patterns);
        Assert.Contains("/home/user/logs/", patterns);
        Assert.Contains("npm install", patterns);
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
    public async Task Revoke_exact_match_removes_entry_and_returns_zero()
    {
        SeedDefault();

        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "revoke", "git push", "--audience", "personal", "--tool", "shell_execute"],
            _paths, _output);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("git push", _store.GetApprovedPatterns(TrustAudience.Personal, "shell_execute"));
        Assert.Contains("Removed 'git push'", _output.ToString());
    }

    [Fact]
    public async Task Revoke_no_match_exits_one_and_does_not_modify_file()
    {
        SeedDefault();
        var beforeCount = _store.GetApprovedPatterns(TrustAudience.Personal, "shell_execute").Count;

        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "revoke", "git pull", "--audience", "personal", "--tool", "shell_execute"],
            _paths, _output);

        Assert.Equal(1, exit);
        Assert.Equal(beforeCount, _store.GetApprovedPatterns(TrustAudience.Personal, "shell_execute").Count);
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
        Assert.Empty(_store.GetApprovedPatterns(TrustAudience.Personal, "shell_execute"));
        Assert.Empty(_store.GetApprovedPatterns(TrustAudience.Public, "shell_execute"));
        Assert.Single(_store.GetApprovedPatterns(TrustAudience.Personal, "file_write"));
    }

    [Fact]
    public async Task Revoke_tool_all_scoped_by_audience_leaves_others_alone()
    {
        SeedDefault();

        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "revoke", "--tool", "shell_execute", "--all", "--audience", "personal"],
            _paths, _output);

        Assert.Equal(0, exit);
        Assert.Empty(_store.GetApprovedPatterns(TrustAudience.Personal, "shell_execute"));
        Assert.Equal(["ls"], _store.GetApprovedPatterns(TrustAudience.Public, "shell_execute"));
    }

    [Fact]
    public async Task Revoke_all_without_tool_exits_one_and_does_not_modify_file()
    {
        SeedDefault();
        var beforeCount = _store.GetApprovedPatterns(TrustAudience.Personal, "shell_execute").Count;

        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "revoke", "--all"],
            _paths, _output);

        Assert.Equal(1, exit);
        Assert.Equal(beforeCount, _store.GetApprovedPatterns(TrustAudience.Personal, "shell_execute").Count);
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
        // Same pattern stored under two audiences; unscoped revoke should hit both.
        _store.AddApproval(TrustAudience.Personal, "shell_execute", "ls");
        _store.AddApproval(TrustAudience.Public, "shell_execute", "ls");

        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "revoke", "ls"],
            _paths, _output);

        Assert.Equal(0, exit);
        Assert.Empty(_store.GetApprovedPatterns(TrustAudience.Personal, "shell_execute"));
        Assert.Empty(_store.GetApprovedPatterns(TrustAudience.Public, "shell_execute"));
    }
}

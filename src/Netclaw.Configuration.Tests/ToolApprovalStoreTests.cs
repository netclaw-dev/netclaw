// -----------------------------------------------------------------------
// <copyright file="ToolApprovalStoreTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class ToolApprovalStoreTests : IDisposable
{
    private readonly string _file;
    private readonly ToolApprovalStore _store;

    public ToolApprovalStoreTests()
    {
        _file = Path.Combine(Path.GetTempPath(), $"netclaw-approvals-{Guid.NewGuid():N}.json");
        _store = new ToolApprovalStore(_file);
    }

    public void Dispose()
    {
        if (File.Exists(_file)) File.Delete(_file);
        var invalid = _file + ".invalid";
        if (File.Exists(invalid)) File.Delete(invalid);
    }

    [Fact]
    public void RemoveApproval_returns_false_when_file_is_empty()
    {
        Assert.False(_store.RemoveApproval(TrustAudience.Personal, "shell_execute", "git push"));
    }

    [Fact]
    public void RemoveApproval_removes_exact_match_and_returns_true()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", "git push");
        _store.AddApproval(TrustAudience.Personal, "shell_execute", "/home/user/logs/");

        Assert.True(_store.RemoveApproval(TrustAudience.Personal, "shell_execute", "git push"));

        var remaining = _store.GetApprovedPatterns(TrustAudience.Personal, "shell_execute");
        Assert.Equal(["/home/user/logs/"], remaining);
    }

    [Fact]
    public void RemoveApproval_returns_false_for_unknown_pattern()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", "git push");
        Assert.False(_store.RemoveApproval(TrustAudience.Personal, "shell_execute", "git pull"));
        Assert.Single(_store.GetApprovedPatterns(TrustAudience.Personal, "shell_execute"));
    }

    [Fact]
    public void RemoveApproval_uses_platform_case_sensitivity()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", "git push");

        var caseDifferent = _store.RemoveApproval(TrustAudience.Personal, "shell_execute", "GIT PUSH");

        if (OperatingSystem.IsWindows())
        {
            Assert.True(caseDifferent);
            Assert.Empty(_store.GetApprovedPatterns(TrustAudience.Personal, "shell_execute"));
        }
        else
        {
            Assert.False(caseDifferent);
            Assert.Single(_store.GetApprovedPatterns(TrustAudience.Personal, "shell_execute"));
        }
    }

    [Fact]
    public void RemoveApproval_prunes_empty_tool_and_audience_sections()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", "git push");
        Assert.True(_store.RemoveApproval(TrustAudience.Personal, "shell_execute", "git push"));

        var snapshot = _store.Snapshot();
        Assert.Empty(snapshot);
    }

    [Fact]
    public void RemoveApproval_does_not_disturb_other_audiences_or_tools()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", "git push");
        _store.AddApproval(TrustAudience.Public, "shell_execute", "git push");
        _store.AddApproval(TrustAudience.Personal, "file_write", "/tmp/scratch/");

        Assert.True(_store.RemoveApproval(TrustAudience.Personal, "shell_execute", "git push"));

        Assert.Empty(_store.GetApprovedPatterns(TrustAudience.Personal, "shell_execute"));
        Assert.Equal(["git push"], _store.GetApprovedPatterns(TrustAudience.Public, "shell_execute"));
        Assert.Equal(["/tmp/scratch/"], _store.GetApprovedPatterns(TrustAudience.Personal, "file_write"));
    }

    [Fact]
    public void RemoveAllForTool_clears_every_entry_and_returns_count()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", "git push");
        _store.AddApproval(TrustAudience.Personal, "shell_execute", "/home/user/logs/");
        _store.AddApproval(TrustAudience.Personal, "file_write", "/tmp/scratch/");

        var removed = _store.RemoveAllForTool(TrustAudience.Personal, "shell_execute");

        Assert.Equal(2, removed);
        Assert.Empty(_store.GetApprovedPatterns(TrustAudience.Personal, "shell_execute"));
        Assert.Equal(["/tmp/scratch/"], _store.GetApprovedPatterns(TrustAudience.Personal, "file_write"));
    }

    [Fact]
    public void RemoveAllForTool_returns_zero_when_tool_absent()
    {
        Assert.Equal(0, _store.RemoveAllForTool(TrustAudience.Personal, "shell_execute"));
    }

    [Fact]
    public void Snapshot_returns_deep_clone_independent_of_subsequent_writes()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", "git push");
        var snapshot = _store.Snapshot();

        _store.AddApproval(TrustAudience.Personal, "shell_execute", "git pull");

        var personalShell = snapshot["personal"]["shell_execute"];
        Assert.Equal(["git push"], personalShell);
    }
}

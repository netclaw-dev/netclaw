// -----------------------------------------------------------------------
// <copyright file="WorkingContextSnapshotTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;

namespace Netclaw.Actors.Tests.Sessions;

public class WorkingContextSnapshotTests
{
    [Fact]
    public void ParseStatus_reads_branch_divergence_and_dirty_counts()
    {
        var snapshot = WorkingContextSnapshotProvider.ParseStatus(
            "/worktrees/feature",
            "/repos/app/.git",
            """
            # branch.oid 0123456789abcdef
            # branch.head feature/context
            # branch.upstream origin/dev
            # branch.ab +2 -1
            1 M. N... 100644 100644 100644 aaaaaaa bbbbbbb src/Staged.cs
            1 .M N... 100644 100644 100644 aaaaaaa bbbbbbb src/Modified.cs
            ? src/New.cs
            """);

        Assert.Equal("feature/context", snapshot.Branch);
        Assert.Equal("0123456789abcdef", snapshot.Head);
        Assert.Equal("origin/dev", snapshot.Upstream);
        Assert.Equal(2, snapshot.Ahead);
        Assert.Equal(1, snapshot.Behind);
        Assert.Equal(1, snapshot.Staged);
        Assert.Equal(1, snapshot.Modified);
        Assert.Equal(1, snapshot.Untracked);
        Assert.Equal(3, snapshot.ChangedFiles.Count);
    }

    [Fact]
    public void ParseStatus_uses_rename_destination_as_changed_file()
    {
        var snapshot = WorkingContextSnapshotProvider.ParseStatus(
            "/worktrees/feature",
            "/repos/app/.git",
            "2 R. N... 100644 100644 100644 aaaaaaa bbbbbbb R100 src/New Name.cs\tsrc/Old Name.cs");

        Assert.Equal(["src/New Name.cs"], snapshot.ChangedFiles);
    }

    [Fact]
    public void Render_nests_git_under_working_context_without_remote_url()
    {
        var snapshot = new WorkingContextSnapshot
        {
            WorkingContext = WorkingContext.Empty
                .WithProjectDirectory("/worktrees/feature")
                .AddRecentFile("src/App.cs"),
            Git = new GitWorkingContextSnapshot
            {
                Worktree = "/worktrees/feature",
                CommonDirectory = "/repos/app/.git",
                Branch = "feature/context",
                Head = "01234567",
                Upstream = "origin/dev",
                Staged = 1,
                Modified = 2,
                Untracked = 3
            }
        };

        var block = snapshot.ToContextBlock();

        Assert.Contains("[working-context]", block);
        Assert.Contains("recent_files:\n  - src/App.cs", block);
        Assert.Contains("git:\n  worktree: /worktrees/feature", block);
        Assert.Contains("branch: feature/context", block);
        Assert.DoesNotContain("https://", block, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Public_audience_does_not_inspect_or_render_git()
    {
        var provider = new WorkingContextSnapshotProvider(
            NullLogger<WorkingContextSnapshotProvider>.Instance);
        var context = WorkingContext.Empty.WithProjectDirectory("/path/that/does/not/exist");

        var snapshot = provider.Create(context, TrustAudience.Public);

        Assert.Null(snapshot.Git);
        Assert.Null(snapshot.GitUnavailableReason);
        Assert.Equal(string.Empty, snapshot.ToContextBlock());
    }

    [Fact]
    public void Missing_project_directory_reports_unavailable_for_personal_audience()
    {
        var provider = new WorkingContextSnapshotProvider(
            NullLogger<WorkingContextSnapshotProvider>.Instance);
        var context = WorkingContext.Empty.WithProjectDirectory("/path/that/does/not/exist");

        var snapshot = provider.Create(context, TrustAudience.Personal);

        Assert.Equal("project directory does not exist", snapshot.GitUnavailableReason);
        Assert.Contains("status: unavailable", snapshot.ToContextBlock());
    }
}

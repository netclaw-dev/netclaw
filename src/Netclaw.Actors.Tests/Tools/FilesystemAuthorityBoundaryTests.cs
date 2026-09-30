// -----------------------------------------------------------------------
// <copyright file="FilesystemAuthorityBoundaryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Boundary cases for filesystem authority rules that no earlier case pinned.
/// Each case goes through the production registration and the tool executor.
/// </summary>
[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class FilesystemAuthorityBoundaryTests(ShellApprovalMatrixFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // R1: only the file-tool root source can make a path boundary unrestricted.
    // The widest shell grant, "always everywhere", must not widen an unattended
    // file read. The control shows that the grant is live for the shell.
    [Fact]
    public async Task Everywhere_shell_grant_does_not_widen_unattended_file_reads()
    {
        await using var harness = await ShellApprovalHarness.CreateAsync(
            "everywhere-grant-file-read",
            new ShellApprovalInvocation("true", Interactive: false),
            Approvals.PersistentAnywhere("cat"),
            fixture.ActorSystem,
            Ct);
        var outside = Path.Combine(Path.GetDirectoryName(harness.ProjectDirectory)!, "workspaces", "external", "notes.txt");
        await File.WriteAllTextAsync(outside, "outside data", Ct);
        await File.WriteAllTextAsync(Path.Combine(harness.SessionDirectory, "notes.txt"), "session data", Ct);

        var shell = await harness.EvaluateShellAsync("cat notes.txt", Ct, harness.SessionDirectory);
        var read = await harness.RunToolAsync("file_read", ToolInput.Create("Path", outside), Ct);

        Assert.Equal(ApprovalOutcome.Allowed, shell.Outcome);
        Assert.Equal(ApprovalAllowReason.StoredApproval, shell.AllowReason);
        Assert.Equal(ApprovalOutcome.Denied, read.Outcome);
        Assert.Equal("path_access_denied", read.DenyReason);
        Assert.Null(read.Output);
    }
}

// -----------------------------------------------------------------------
// <copyright file="ApprovalDirectoryMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;

namespace Netclaw.Actors.MutationTests;

public sealed class ApprovalDirectoryMutationTests : IDisposable
{
    private readonly string _basePath = Path.Combine(
        Path.GetTempPath(), "netclaw-approval-mutations", Guid.NewGuid().ToString("N"));
    private readonly string _grantRoot;
    private readonly string _outside;
    private readonly ApprovalShell _shell = OperatingSystem.IsWindows() ? ApprovalShell.PowerShell : ApprovalShell.Bash;

    public ApprovalDirectoryMutationTests()
    {
        _grantRoot = Path.Combine(_basePath, "app");
        _outside = Path.Combine(_basePath, "app-other");
        Directory.CreateDirectory(Path.Combine(_grantRoot, "src"));
        Directory.CreateDirectory(Path.Combine(_outside, "nested"));
    }

    [Theory]
    [InlineData(".", true)]
    [InlineData("src", true)]
    [InlineData("src/../src", true)]
    [InlineData("../app-other", false)]
    [InlineData("src/../../app-other", false)]
    public void Folder_grant_requires_normalized_containment(string relativePath, bool allowed)
    {
        var candidate = Path.Combine(_grantRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.Equal(allowed, Matches(candidate, _grantRoot));
    }

    [Fact]
    public void Candidate_directory_owns_scope_even_when_cwd_disagrees()
    {
        Assert.False(Matches(_outside, _grantRoot));
        Assert.True(Matches(Path.Combine(_grantRoot, "src"), _outside));
    }

    [Fact]
    public void Relative_scope_uses_cwd_without_widening_the_grant()
    {
        Assert.False(Matches("../app-other", _grantRoot));
        Assert.True(Matches("src", _grantRoot));
        Assert.False(Matches(null, _outside));
        Assert.True(Matches(null, Path.Combine(_grantRoot, "src")));
        Assert.False(Matches(null, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Folder_grant_rejects_a_link_to_a_sibling_directory(bool nested)
    {
        var link = Path.Combine(_grantRoot, "link");
        var linkInfo = Directory.CreateSymbolicLink(link, _outside);
        Assert.Equal(_outside, linkInfo.ResolveLinkTarget(returnFinalTarget: true)!.FullName);
        var candidate = nested ? Path.Combine(link, "nested") : link;

        Assert.False(Matches(candidate, _grantRoot));
        Assert.True(Matches(Path.Combine(_grantRoot, "src"), _grantRoot));
    }

    [Theory]
    [InlineData(@"C:\repo\app", true)]
    [InlineData(@"c:\REPO\APP\src", true)]
    [InlineData(@"C:\repo\app-other", false)]
    [InlineData(@"C:\repo\app\..\app-other", false)]
    [InlineData(@"D:\repo\app\src", false)]
    [InlineData(@"..\app-other", false)]
    public void PowerShell_scope_preserves_windows_path_boundaries(string directory, bool allowed)
    {
        var grant = ApprovalEntry.CreateTokenPrefix(ApprovalShell.PowerShell, ["git", "status"], @"C:\repo\app");
        var candidate = CreateCandidate(ApprovalShell.PowerShell, directory);

        Assert.Equal(allowed, ApprovalPatternMatching.MatchesShellApproval(candidate, @"C:\repo\app", [grant]));
    }

    public void Dispose() => Directory.Delete(_basePath, recursive: true);

    private bool Matches(string? directory, string? cwd)
    {
        var grant = ApprovalEntry.CreateTokenPrefix(_shell, ["git", "status"], _grantRoot);
        return ApprovalPatternMatching.MatchesShellApproval(CreateCandidate(_shell, directory), cwd, [grant]);
    }

    private static ApprovalCandidate CreateCandidate(ApprovalShell shell, string? directory) =>
        new("git status", directory)
        {
            Shell = shell,
            VerbTokens = Array.AsReadOnly(["git", "status"])
        };
}

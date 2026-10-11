// -----------------------------------------------------------------------
// <copyright file="FileSystemAuthorityTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Security.Authorization.Filesystem;
using Xunit;

namespace Netclaw.Security.Tests;

/// <summary>
/// Pins the published contract of the filesystem authority for the path risks
/// that no tool-level case can isolate: the distinct link rules (R3), the
/// temporary alias (R7), protection over an unrestricted boundary (R1, R2),
/// the declared path style (R5), and the literal tilde (R6).
/// </summary>
public sealed class FileSystemAuthorityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"netclaw-filesystem-authority-{Guid.NewGuid():N}");

    public FileSystemAuthorityTests() => Directory.CreateDirectory(_root);

    public static bool IsPosix => !OperatingSystem.IsWindows();

    public static bool IsMacOS => OperatingSystem.IsMacOS();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [SlopwatchSuppress("SW001", "The case creates POSIX symbolic links, which Windows CI cannot create.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The case creates POSIX symbolic links.")]
    public void Link_rules_differ_at_the_anchor()
    {
        var real = Directory.CreateDirectory(Path.Combine(_root, "real", "child")).Parent!.FullName;
        var alias = Path.Combine(_root, "alias");
        Directory.CreateSymbolicLink(alias, real);
        var root = Host(alias);
        var child = Host(Path.Combine(alias, "child"));

        Assert.Equal(PathDecision.Allowed, Membership(child, new PathBoundary.Folder(root, LinkRule.BelowRoot)));
        Assert.Equal(PathDecision.CrossesLink, Membership(child, new PathBoundary.Folder(root, LinkRule.IncludingRoot)));
        Assert.Equal(PathDecision.CrossesLink, Membership(child, new PathBoundary.Folder(root, LinkRule.FromVolumeRoot)));
    }

    [SlopwatchSuppress("SW001", "The case creates POSIX symbolic links, which Windows CI cannot create.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The case creates POSIX symbolic links.")]
    public void Every_link_rule_refuses_a_link_below_the_root()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        var grant = Directory.CreateDirectory(Path.Combine(_root, "grant")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(grant, "escape"), outside);
        var escaped = Host(Path.Combine(grant, "escape", "file.txt"));

        foreach (var rule in new[] { LinkRule.BelowRoot, LinkRule.IncludingRoot, LinkRule.FromVolumeRoot })
            Assert.Equal(PathDecision.CrossesLink, Membership(escaped, new PathBoundary.Folder(Host(grant), rule)));
        Assert.Equal(PathDecision.Outside, Membership(Host(outside), new PathBoundary.Folder(Host(grant), LinkRule.BelowRoot)));
    }

    [Fact]
    public void Link_anchor_must_contain_the_root()
    {
        var anchor = Host(Path.Combine(_root, "anchor"));
        var root = Host(Path.Combine(_root, "elsewhere"));

        Assert.Throws<ArgumentException>(() =>
            new PathBoundary.Folder(root, LinkRule.IncludingRoot) { LinkAnchor = anchor });
    }

    [SlopwatchSuppress("SW001", "The case creates POSIX symbolic links, which Windows CI cannot create.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The case creates POSIX symbolic links.")]
    public void Temporary_alias_maps_to_its_resolved_root_and_refuses_a_planted_link()
    {
        var resolvedTemp = Directory.CreateDirectory(Path.Combine(_root, "private-tmp")).FullName;
        var authoredTemp = Path.Combine(_root, "tmp");
        Directory.CreateSymbolicLink(authoredTemp, resolvedTemp);
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(resolvedTemp, "escape"), outside);
        var otherAlias = Path.Combine(_root, "other-alias");
        Directory.CreateSymbolicLink(otherAlias, outside);
        var roots = PlatformTemporaryRoot.ResolveAll([authoredTemp]);

        Assert.True(FileSystemAuthority.IsBelowTemporaryAlias(Host(authoredTemp), roots));
        Assert.True(FileSystemAuthority.IsBelowTemporaryAlias(Host(Path.Combine(authoredTemp, "work", "result.log")), roots));
        Assert.True(FileSystemAuthority.IsBelowTemporaryAlias(Host(Path.Combine(resolvedTemp, "work", "result.log")), roots));
        Assert.False(FileSystemAuthority.IsBelowTemporaryAlias(Host(Path.Combine(authoredTemp, "escape", "result.log")), roots));
        Assert.False(FileSystemAuthority.IsBelowTemporaryAlias(Host(Path.Combine(otherAlias, "result.log")), roots));
    }

    [SlopwatchSuppress("SW001", "Only a macOS host has the native /tmp to /private/tmp alias.")]
    [Fact(SkipUnless = nameof(IsMacOS), Skip = "The case needs the native macOS /tmp alias.")]
    public void MacOS_tmp_alias_passes_only_the_alias_link_rule()
    {
        var path = Host("/tmp/netclaw-alias-probe/result.log");

        Assert.True(FileSystemAuthority.IsLinkFreeFromVolumeRoot(path, LinkRule.FromVolumeRootExceptTemporaryAlias));
        Assert.False(FileSystemAuthority.IsLinkFreeFromVolumeRoot(path, LinkRule.FromVolumeRoot));
    }

    [Fact]
    public void Unrestricted_boundary_keeps_protection_for_its_operation()
    {
        var protectedDirectory = Path.Combine(_root, "config");
        var authority = new FileSystemAuthority(
            writeProtected: [protectedDirectory],
            readProtected: [],
            shellProtected: [protectedDirectory]);
        var file = Host(Path.Combine(protectedDirectory, "netclaw.json"));
        PathBoundary[] unrestricted = [new PathBoundary.Unrestricted()];

        Assert.Equal(PathDecision.Protected, authority.Evaluate(file, PathOperation.Write, unrestricted));
        Assert.Equal(PathDecision.Protected, authority.Evaluate(file, PathOperation.Shell, unrestricted));
        Assert.Equal(PathDecision.Allowed, authority.Evaluate(file, PathOperation.Read, unrestricted));
        Assert.Equal(PathDecision.Protected, authority.Evaluate(
            Host(Path.Combine(_root, "CONFIG", "netclaw.json")), PathOperation.Write, unrestricted));
    }

    [Fact]
    public void Path_keeps_its_declared_style_on_every_host()
    {
        Assert.True(CanonicalPath.TryCreate(@"C:\work\..\repo\src", relativeBase: null, ShellPathStyle.Windows, out var windows));
        Assert.True(CanonicalPath.TryCreate(@"c:\REPO", relativeBase: null, ShellPathStyle.Windows, out var windowsRoot));
        Assert.True(CanonicalPath.TryCreate("/repo/src", relativeBase: null, ShellPathStyle.Posix, out var posix));
        Assert.True(CanonicalPath.TryCreate("/repo", relativeBase: null, ShellPathStyle.Posix, out var posixRoot));

        Assert.Equal(@"C:\repo\src", windows.Value);
        Assert.True(windowsRoot.Contains(windows));
        Assert.False(windowsRoot.Contains(posix));
        Assert.False(posixRoot.Contains(windows));
        Assert.Equal(PathDecision.Outside, Membership(windows, new PathBoundary.Folder(posixRoot, LinkRule.BelowRoot)));
    }

    [Fact]
    public void Allow_side_containment_keeps_case_for_posix_paths()
    {
        // R4: allow checks compare with case except for the Windows style. A grant
        // for /work must not cover /WORK. Protected sets ignore case instead.
        Assert.True(CanonicalPath.TryCreate("/work", relativeBase: null, ShellPathStyle.Posix, out var root));
        Assert.True(CanonicalPath.TryCreate("/WORK/src", relativeBase: null, ShellPathStyle.Posix, out var upper));
        Assert.True(CanonicalPath.TryCreate("/work/src", relativeBase: null, ShellPathStyle.Posix, out var lower));

        Assert.False(root.Contains(upper));
        Assert.False(root.IsSamePath(Posix("/WORK")));
        Assert.True(root.Contains(lower));
        Assert.Equal(PathDecision.Outside, Membership(upper, new PathBoundary.Folder(root, LinkRule.BelowRoot)));
    }

    [Fact]
    public void Host_path_keeps_a_tilde_as_a_literal_segment()
    {
        var project = Host(Path.Combine(_root, "project"));

        Assert.True(CanonicalPath.TryCreateHost("~/notes.txt", project.Value, out var path));
        Assert.Equal(Path.Combine(project.Value, "~", "notes.txt"), path.Value);
        Assert.False(CanonicalPath.TryCreate("~/notes.txt", project.Value, CanonicalPath.HostStyle, out _));
    }

    // Issue #2375: a link word gets the scope of its final target. The target
    // keeps the lexical frame, so an alias above a grant root stays in it.
    [Fact]
    public void Link_chain_ends_at_its_final_target_in_the_lexical_frame()
    {
        var real = Directory.CreateDirectory(Path.Combine(_root, "real")).FullName;
        File.WriteAllText(Path.Combine(real, "a.txt"), "synthetic test data");
        var alias = Path.Combine(_root, "alias");
        CreateLinkOrSkip(() => Directory.CreateSymbolicLink(alias, real));
        CreateLinkOrSkip(() => File.CreateSymbolicLink(Path.Combine(real, "hop.txt"), "a.txt"));
        CreateLinkOrSkip(() => File.CreateSymbolicLink(Path.Combine(real, "start.txt"), "hop.txt"));

        Assert.Equal(LinkChainEnd.Target, FileSystemAuthority.FollowLinkChain(Path.Combine(alias, "start.txt"), out var target));
        Assert.Equal(Path.Combine(alias, "a.txt"), target);
    }

    [Fact]
    public void Link_chain_ends_at_a_directory_or_a_missing_target()
    {
        var project = Directory.CreateDirectory(Path.Combine(_root, "project")).FullName;
        var other = Directory.CreateDirectory(Path.Combine(_root, "other")).FullName;
        CreateLinkOrSkip(() => Directory.CreateSymbolicLink(Path.Combine(project, "out"), Path.Combine("..", "other")));
        CreateLinkOrSkip(() => File.CreateSymbolicLink(Path.Combine(project, "dangling.txt"), Path.Combine(other, "missing.txt")));

        Assert.Equal(LinkChainEnd.Target, FileSystemAuthority.FollowLinkChain(Path.Combine(project, "out"), out var directory));
        Assert.Equal(other, directory);
        Assert.Equal(LinkChainEnd.Target, FileSystemAuthority.FollowLinkChain(Path.Combine(project, "dangling.txt"), out var missing));
        Assert.Equal(Path.Combine(other, "missing.txt"), missing);
    }

    [Fact]
    public void Path_that_is_not_a_link_has_no_chain()
    {
        var file = Path.Combine(_root, "file.txt");
        File.WriteAllText(file, "synthetic test data");

        Assert.Equal(LinkChainEnd.NotALink, FileSystemAuthority.FollowLinkChain(file, out _));
        Assert.Equal(LinkChainEnd.NotALink, FileSystemAuthority.FollowLinkChain(_root, out _));
        Assert.Equal(LinkChainEnd.NotALink, FileSystemAuthority.FollowLinkChain(Path.Combine(_root, "missing.txt"), out _));
        // The host cannot name a path that is not a full path. It is not a link
        // that the check can read, so the word keeps its decision.
        Assert.Equal(LinkChainEnd.NotALink, FileSystemAuthority.FollowLinkChain("file.txt", out _));
    }

    // SECURITY: the OS follows a link before it applies "..", so a ".." that
    // leaves a link has no lexical target. A loop never ends.
    [Fact]
    public void Link_chain_with_an_unknown_target_fails_closed()
    {
        var project = Directory.CreateDirectory(Path.Combine(_root, "project")).FullName;
        var deep = Directory.CreateDirectory(Path.Combine(_root, "other", "deep")).FullName;
        CreateLinkOrSkip(() => Directory.CreateSymbolicLink(Path.Combine(project, "outlnk"), deep));
        CreateLinkOrSkip(() => File.CreateSymbolicLink(Path.Combine(project, "dotdot.txt"), Path.Combine("outlnk", "..", "notes.txt")));
        CreateLinkOrSkip(() => File.CreateSymbolicLink(Path.Combine(project, "loop1.txt"), "loop2.txt"));
        CreateLinkOrSkip(() => File.CreateSymbolicLink(Path.Combine(project, "loop2.txt"), "loop1.txt"));

        Assert.Equal(LinkChainEnd.Unknown, FileSystemAuthority.FollowLinkChain(Path.Combine(project, "dotdot.txt"), out var target));
        Assert.Equal(string.Empty, target);
        Assert.Equal(LinkChainEnd.Unknown, FileSystemAuthority.FollowLinkChain(Path.Combine(project, "loop1.txt"), out _));
    }

    [Fact]
    public void Link_chain_follows_at_most_the_hop_limit()
    {
        var file = Path.Combine(_root, "end.txt");
        File.WriteAllText(file, "synthetic test data");
        var previous = "end.txt";
        for (var link = 1; link <= FileSystemAuthority.MaximumLinkHops + 1; link++)
        {
            var name = $"link{link}.txt";
            var target = previous;
            CreateLinkOrSkip(() => File.CreateSymbolicLink(Path.Combine(_root, name), target));
            previous = name;
        }

        var atLimit = Path.Combine(_root, $"link{FileSystemAuthority.MaximumLinkHops}.txt");
        var overLimit = Path.Combine(_root, $"link{FileSystemAuthority.MaximumLinkHops + 1}.txt");
        Assert.Equal(LinkChainEnd.Target, FileSystemAuthority.FollowLinkChain(atLimit, out var end));
        Assert.Equal(file, end);
        Assert.Equal(LinkChainEnd.Unknown, FileSystemAuthority.FollowLinkChain(overLimit, out _));
    }

    private static PathDecision Membership(CanonicalPath path, PathBoundary boundary)
        => FileSystemAuthority.EvaluateMembership(path, [boundary]);

    private static CanonicalPath Posix(string path)
    {
        Assert.True(CanonicalPath.TryCreate(path, relativeBase: null, ShellPathStyle.Posix, out var canonical));
        return canonical;
    }

    private static CanonicalPath Host(string path)
    {
        Assert.True(CanonicalPath.TryCreateHost(path, relativeBase: null, out var canonical));
        return canonical;
    }

    // Windows creates a symbolic link only with developer mode or administrator
    // rights. A POSIX host always can, so the skip applies to Windows only.
    private static void CreateLinkOrSkip(Action create)
    {
        try
        {
            create();
        }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is UnauthorizedAccessException or IOException)
        {
            Assert.Skip($"This Windows host cannot create a symbolic link: {ex.Message}");
        }
    }
}

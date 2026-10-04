// -----------------------------------------------------------------------
// <copyright file="GlobPolicyMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Security;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Actors.MutationTests;

/// <summary>
/// ShellSyntaxTree 0.4.0-beta.11 to beta.17 facts on the Bash 5.2 host: glob
/// words, bound values, and the per-command judgment of a glob.
/// </summary>
public sealed class GlobPolicyMutationTests
{
    private static ShellExecutionEnvironment Bash52 =>
        ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux, new Version(5, 2));

    // Decision D5, option A: a glob word gets the decision of each literal path
    // that its segments can match. A match that only contains a protected path
    // keeps the decision of that literal directory.
    [Theory]
    [InlineData("cat /srv/netclaw/k*/id.xml", true)]
    [InlineData("cat /srv/netclaw/K*/id.xml", true)]
    [InlineData("cat /srv/netclaw/?eys/id.xml", true)]
    [InlineData("cat /srv/netclaw/[kx]eys/id.xml", true)]
    [InlineData("cat /srv/netclaw/[!x]eys/id.xml", true)]
    [InlineData("cat /srv/netclaw/[]k]eys/id.xml", true)]
    [InlineData("cat /srv/netclaw/[k/id.xml", true)]
    [InlineData("ln -s /srv/netclaw/k* link", true)]
    [InlineData("ls /srv/net*/keys", true)]
    [InlineData("ls /srv/net*/Keys", true)]
    [InlineData("cat /srv/netclaw/*/secrets.json", true)]
    [InlineData("cat /srv/netclaw/*/token", true)]
    [InlineData("cat /srv/data/.*", true)]
    [InlineData("cat /srv/data/*", false)]
    [InlineData("cat /srv/netclaw/l*/id.xml", false)]
    [InlineData("cat /srv/netclaw/*.json", false)]
    [InlineData("cat /srv/netclawx/*/id.xml", false)]
    [InlineData("ls -d /srv/*", false)]
    [InlineData("ls -d /srv/*/*", true)]
    [InlineData("ls /srv/*/README.md", false)]
    [InlineData("ls /*/netclaw/keys", true)]
    [InlineData("ls /opt/*/netclaw", false)]
    public void Glob_word_gets_the_decision_of_each_path_it_can_match(string command, bool denied)
    {
        // The glob and binding facts belong to the POSIX Bash 5.2 host.
        if (OperatingSystem.IsWindows())
            return;

        var policy = new ToolPathPolicy(
            Bash52,
            ["/srv/netclaw/keys", "/srv/netclaw/config/secrets.json", "/srv/data/.hidden"]);

        Assert.Equal(denied, policy.CommandReferencesDeniedPath(command, "/work"));
    }

    // The default credential store of the home directory applies to a glob even
    // when the protected list has another root.
    [Theory]
    [InlineData("cat ~/.netclaw/k*/*.xml", true)]
    [InlineData("cat ~/.netclaw/config/secret?.json", true)]
    [InlineData("cat ~/.netclaw/sessions/*.yaml", false)]
    [InlineData("cat ~/.net*/c*/s*.json", true)]
    [InlineData("cat ~/.net*/c*/n*.json", false)]
    [InlineData("cat ~/.netclaw/l*/*.xml", false)]
    public void Glob_word_gets_the_default_credential_store_decision(string command, bool denied)
    {
        // The glob and binding facts belong to the POSIX Bash 5.2 host.
        if (OperatingSystem.IsWindows())
            return;

        var policy = new ToolPathPolicy(Bash52, ["/srv/other"]);

        Assert.Equal(denied, policy.CommandReferencesDeniedPath(command, "/work"));
    }

    // ShellSyntaxTree 0.4.0-beta.17 proves the value of a word that reads a
    // binding. The deny list checks each proved value, as it checks the literal twin.
    [Theory]
    [InlineData("x=/; rm -rf \"$x\"", false)]
    [InlineData("for d in /tmp /; do rm -rf \"$d\"; done", false)]
    [InlineData("for d in / /tmp; do rm -rf \"$d\"; done", false)]
    [InlineData("x=netclawd; pkill \"$x\"", false)]
    [InlineData("x=/tmp/build; rm -rf \"$x\"", true)]
    [InlineData("for d in /tmp/a /tmp/b; do rm -rf \"$d\"; done", true)]
    [InlineData("x=jekyll; pkill \"$x\"", true)]
    [InlineData("rm -rf /tmp/build", true)]
    [InlineData("rm -rf '/;'", true)]
    [InlineData("rm -rf \"$UNSET_NAME\"", true)]
    [InlineData("s=netclaw; systemctl stop \"$s\"", false)]
    [InlineData("v=daemon; netclaw \"$v\" stop", false)]
    public void Bound_value_gets_the_decision_of_its_literal_twin(string command, bool allowed)
    {
        // The glob and binding facts belong to the POSIX Bash 5.2 host.
        if (OperatingSystem.IsWindows())
            return;

        var policy = new ShellCommandPolicy(Bash52);

        Assert.Equal(allowed, policy.Evaluate(command, "/work").Allowed);
    }

    [Fact]
    public void Bound_value_combinations_above_the_bound_deny()
    {
        // The glob and binding facts belong to the POSIX Bash 5.2 host.
        if (OperatingSystem.IsWindows())
            return;

        var policy = new ShellCommandPolicy(Bash52);
        var values = string.Join(' ', Enumerable.Range(0, 17).Select(static index => $"v{index}"));

        var atBound = policy.Evaluate(
            $"for a in {string.Join(' ', Enumerable.Range(0, 16).Select(static index => $"v{index}"))}; do " +
            $"for b in {string.Join(' ', Enumerable.Range(0, 16).Select(static index => $"w{index}"))}; do " +
            "touch \"/tmp/$a\" \"/tmp/$b\"; done; done",
            "/work");
        var aboveBound = policy.Evaluate(
            $"for a in {values}; do for b in {string.Join(' ', Enumerable.Range(0, 16).Select(static index => $"w{index}"))}; do " +
            "touch \"/tmp/$a\" \"/tmp/$b\"; done; done",
            "/work");

        Assert.True(atBound.Allowed);
        Assert.False(aboveBound.Allowed);
        Assert.Equal(DenyCategory.Unknown, aboveBound.DenyCategory);
    }

    // A glob in a directory segment has a fixed reach only with the glob fact.
    // When it can expand to an option word, decision D1 applies to it.
    [Theory]
    [InlineData("rm src/*/stale.tmp", true, ShellUnresolvedPartName.None)]
    [InlineData("rm */stale.tmp", true, ShellUnresolvedPartName.Operand)]
    [InlineData("echo */stale.tmp", true, ShellUnresolvedPartName.None)]
    [InlineData("rm src/*/stale.tmp", false, ShellUnresolvedPartName.Command)]
    [InlineData("rm *.tmp", true, ShellUnresolvedPartName.None)]
    public void Glob_in_a_directory_segment_needs_the_glob_fact(
        string command,
        bool bash52,
        ShellUnresolvedPartName expected)
    {
        // The glob and binding facts belong to the POSIX Bash 5.2 host.
        if (OperatingSystem.IsWindows())
            return;

        var environment = bash52 ? Bash52 : ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux);
        var analysis = new ShellCommandAnalyzer(environment).Analyze(command, "/work");

        Assert.Equal(
            expected.ToString(),
            analysis.GetUnresolvedPart(Assert.Single(analysis.Commands)).ToString());
    }

    // A link must not take a match of the glob out of its covering directory.
    // The walk visits only the directories that a segment can match.
    [Theory]
    [InlineData("*/x", "", true)]
    [InlineData("*", "a>outside", false)]
    [InlineData("*", "a>b", true)]
    [InlineData("*/x", "a>b", false)]
    [InlineData("*/x", "a>outside", false)]
    [InlineData("*/x", "b/l>outside", false)]
    [InlineData("b*/x", "zzz/l>outside", true)]
    [InlineData("*/x", ".hidden/l>outside", true)]
    [InlineData(".*/x", ".hidden/l>outside", false)]
    [InlineData("*/*/x", "b/c/l>outside", false)]
    public void Glob_link_walk_keeps_matches_inside_the_covering_directory(
        string pattern,
        string link,
        bool contained)
    {
        // The glob and binding facts belong to the POSIX Bash 5.2 host.
        if (OperatingSystem.IsWindows())
            return;

        using var tree = new TemporaryTree();
        Directory.CreateDirectory(Path.Combine(tree.Root, "cover", "b", "c"));
        Directory.CreateDirectory(Path.Combine(tree.Root, "cover", "zzz"));
        Directory.CreateDirectory(Path.Combine(tree.Root, "cover", ".hidden"));
        Directory.CreateDirectory(Path.Combine(tree.Root, "outside"));
        if (link.Length > 0)
        {
            var parts = link.Split('>');
            var target = parts[1] == "outside"
                ? Path.Combine(tree.Root, "outside")
                : Path.Combine(tree.Root, "cover", parts[1]);
            Directory.CreateSymbolicLink(Path.Combine(tree.Root, "cover", parts[0]), target);
        }

        Assert.Equal(contained, IsLinkContained(Path.Combine(tree.Root, "cover"), pattern));
    }

    [Theory]
    [InlineData(4096, true)]
    [InlineData(4097, false)]
    public void Glob_link_walk_has_a_directory_bound(int directories, bool contained)
    {
        // The glob and binding facts belong to the POSIX Bash 5.2 host.
        if (OperatingSystem.IsWindows())
            return;

        using var tree = new TemporaryTree();
        for (var index = 0; index < directories; index++)
            Directory.CreateDirectory(Path.Combine(tree.Root, "cover", $"d{index}"));

        Assert.Equal(contained, IsLinkContained(Path.Combine(tree.Root, "cover"), "*/x"));
    }

    [Fact]
    public void Glob_link_walk_accepts_a_missing_covering_directory()
    {
        // The glob and binding facts belong to the POSIX Bash 5.2 host.
        if (OperatingSystem.IsWindows())
            return;

        using var tree = new TemporaryTree();

        Assert.True(IsLinkContained(Path.Combine(tree.Root, "missing"), "*/x"));
    }

    private static bool IsLinkContained(string coveringDirectory, string pattern)
    {
        var analysis = new ShellCommandAnalyzer(Bash52).Analyze($"ls {coveringDirectory}/{pattern}", "/work");
        var occurrence = Assert.Single(analysis.Commands);
        var glob = Assert.IsType<ShellValueDomain.PathPattern>(
            ShellGlobScope.FindGlobPattern(occurrence, occurrence.Clause.Args.Last()));
        Assert.True(Netclaw.Security.Authorization.Filesystem.CanonicalPath.TryCreateHost(
            glob.CoveringDirectory, null, out var covering));
        return ShellGlobScope.IsLinkContained(covering, glob.Glob!);
    }

    private sealed class TemporaryTree : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("netclaw-glob-walk-").FullName;

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    public enum ShellUnresolvedPartName
    {
        None,
        Operand,
        Command
    }
}

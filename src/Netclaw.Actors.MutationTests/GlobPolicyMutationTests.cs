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
/// ShellSyntaxTree 0.4.0-beta.11 to beta.19 facts on the Bash 5.2 host: glob
/// words, bound and decoded values, and the per-command judgment of a glob.
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

    // ShellSyntaxTree 0.4.0-beta.19 decodes an ANSI-C word. The decoded value
    // has no text hint in the authored word, so the proved value gets the
    // protected list and the default credential store text hints.
    [Theory]
    [InlineData("cat /srv/x/.netclaw/$'\\x6beys'/id.xml", true)]
    [InlineData("cat /srv/x/.NETCLAW/$'\\x4bEYS'/id.xml", true)]
    [InlineData("cat /srv/x/config/$'\\x73ecrets.json'", true)]
    [InlineData("cat $'/srv/x/.netclaw\\x5ckeys/id.xml'", true)]
    [InlineData("cat /srv/netclaw/$'\\x6beys'/id.xml", true)]
    [InlineData("cat /srv/x/.netclaw/$'\\x6cogs'/id.xml", false)]
    [InlineData("cat /srv/x/$'\\x6beys'/id.xml", false)]
    public void Decoded_word_gets_the_decision_of_its_proved_value(string command, bool denied)
    {
        // The decoded value is a fact of the POSIX Bash 5.2 host.
        if (OperatingSystem.IsWindows())
            return;

        var policy = new ToolPathPolicy(Bash52, ["/srv/netclaw/keys"]);

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
    [InlineData("*/x", "a>b", true)]
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

    [Fact]
    public void Glob_link_walk_has_a_directory_bound()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var tree = new TemporaryTree();
        var covering = Directory.CreateDirectory(Path.Combine(tree.Root, "cover")).FullName;
        for (var index = 0; index < 4096; index++)
            Directory.CreateDirectory(Path.Combine(covering, $"d{index}"));

        Assert.True(IsLinkContained(covering, "*/x"));
        Directory.CreateDirectory(Path.Combine(covering, "d4096"));
        Assert.False(IsLinkContained(covering, "*/x"));
    }

    [Fact]
    public void Glob_work_bound_counts_alias_hops_and_directory_visits()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var tree = new TemporaryTree();
        var covering = Directory.CreateDirectory(Path.Combine(tree.Root, "cover")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(covering, "target")).FullName;
        for (var index = 0; index < 4094; index++)
            Directory.CreateDirectory(Path.Combine(covering, $"a{index}"));
        Directory.CreateSymbolicLink(Path.Combine(covering, "alias"), target);

        Assert.True(IsLinkContained(covering, "a*/x"));
        Directory.CreateDirectory(Path.Combine(covering, "a4094"));
        Assert.False(IsLinkContained(covering, "a*/x"));
    }

    [Fact]
    public void Glob_leaf_alias_hops_retain_both_sides_of_the_work_bound()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var tree = new TemporaryTree();
        var covering = Directory.CreateDirectory(Path.Combine(tree.Root, "cover")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(covering, "target")).FullName;
        for (var index = 0; index < 4096; index++)
            Directory.CreateSymbolicLink(Path.Combine(covering, $"a{index}"), target);

        Assert.True(IsLinkContained(covering, "a*/"));
        Directory.CreateSymbolicLink(Path.Combine(covering, "a4096"), target);
        Assert.False(IsLinkContained(covering, "a*/"));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Known_alias_protection_survives_an_unresolved_sibling(bool brokenFirst)
    {
        if (OperatingSystem.IsWindows())
            return;

        using var tree = new TemporaryTree();
        var covering = Directory.CreateDirectory(Path.Combine(tree.Root, "cover")).FullName;
        var protectedDirectory = Directory.CreateDirectory(Path.Combine(covering, "private")).FullName;
        var protectedFile = Path.Combine(protectedDirectory, "key.xml");
        File.WriteAllText(protectedFile, "test");
        void AddBroken() => Directory.CreateSymbolicLink(Path.Combine(covering, "broken"), Path.Combine(covering, "missing"));
        void AddProtected() => Directory.CreateSymbolicLink(Path.Combine(covering, "alias"), protectedDirectory);
        if (brokenFirst)
        {
            AddBroken();
            AddProtected();
        }
        else
        {
            AddProtected();
            AddBroken();
        }
        var policy = new ToolPathPolicy(Bash52, [protectedFile]);

        Assert.False(IsLinkContained(covering, "*/key.xml"));
        Assert.True(policy.CommandReferencesDeniedPath($"cat {covering}/*/key.xml", tree.Root));
    }

    [Fact]
    public void A_broken_alias_retains_its_known_protected_suffix()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var tree = new TemporaryTree();
        var covering = Directory.CreateDirectory(Path.Combine(tree.Root, "cover")).FullName;
        var missing = Path.Combine(covering, "missing");
        Directory.CreateSymbolicLink(Path.Combine(covering, "alias"), missing);
        var policy = new ToolPathPolicy(Bash52, [Path.Combine(missing, "key.xml")]);

        Assert.False(IsLinkContained(covering, "a*/key.xml"));
        Assert.True(policy.CommandReferencesDeniedPath($"cat {covering}/a*/key.xml", tree.Root));
    }

    [Fact]
    public void Alias_residual_pattern_reaches_a_protected_descendant()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var tree = new TemporaryTree();
        var covering = Directory.CreateDirectory(Path.Combine(tree.Root, "cover")).FullName;
        var ordinary = Directory.CreateDirectory(Path.Combine(covering, "ordinary")).FullName;
        var protectedDirectory = Directory.CreateDirectory(Path.Combine(ordinary, "keys")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(covering, "alias"), ordinary);
        var policy = new ToolPathPolicy(Bash52, [protectedDirectory]);

        Assert.True(IsLinkContained(covering, "a*/*/key.xml"));
        Assert.True(policy.CommandReferencesDeniedPath($"cat {covering}/a*/*/key.xml", tree.Root));
        Assert.False(policy.CommandReferencesDeniedPath($"cat {covering}/a*/logs/key.xml", tree.Root));
    }

    [Fact]
    public void Alias_residual_directory_keeps_the_literal_parent_decision()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var tree = new TemporaryTree();
        var covering = Directory.CreateDirectory(Path.Combine(tree.Root, "cover")).FullName;
        var ordinary = Directory.CreateDirectory(Path.Combine(covering, "ordinary")).FullName;
        var keys = Directory.CreateDirectory(Path.Combine(ordinary, "keys")).FullName;
        var protectedFile = Path.Combine(keys, "key.xml");
        File.WriteAllText(protectedFile, "test");
        Directory.CreateSymbolicLink(Path.Combine(covering, "alias"), ordinary);
        var policy = new ToolPathPolicy(Bash52, [protectedFile]);

        Assert.False(policy.CommandReferencesDeniedPath($"ls -d {covering}/a*/*", tree.Root));
        Assert.True(policy.CommandReferencesDeniedPath($"cat {covering}/a*/*/*.xml", tree.Root));
    }

    [Fact]
    public void Incomplete_alias_residual_reach_keeps_the_default_credential_store_protection()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var tree = new TemporaryTree();
        var covering = Directory.CreateDirectory(Path.Combine(tree.Root, "cover")).FullName;
        var environment = Bash52;
        Assert.NotNull(environment.HomeDirectory);
        Directory.CreateSymbolicLink(Path.Combine(covering, "alias"), environment.HomeDirectory);
        var policy = new ToolPathPolicy(environment, [Path.Combine(tree.Root, "unrelated")]);

        Assert.False(IsLinkContained(covering, "a*/.netclaw/k*/*.xml"));
        Assert.True(policy.CommandReferencesDeniedPath($"cat {covering}/a*/.netclaw/k*/*.xml", tree.Root));
    }

    [Theory]
    [InlineData("self")]
    [InlineData("cycle")]
    [InlineData("broken")]
    [InlineData("outside-then-back")]
    public void Unproved_alias_reach_cannot_supply_reusable_scope(string kind)
    {
        if (OperatingSystem.IsWindows())
            return;

        using var tree = new TemporaryTree();
        var covering = Directory.CreateDirectory(Path.Combine(tree.Root, "cover")).FullName;
        var ordinary = Directory.CreateDirectory(Path.Combine(covering, "ordinary")).FullName;
        var alias = Path.Combine(covering, "alias");
        switch (kind)
        {
            case "self":
                Directory.CreateSymbolicLink(alias, alias);
                break;
            case "cycle":
                Directory.CreateSymbolicLink(alias, Path.Combine(covering, "other"));
                Directory.CreateSymbolicLink(Path.Combine(covering, "other"), alias);
                break;
            case "broken":
                Directory.CreateSymbolicLink(alias, Path.Combine(covering, "missing"));
                break;
            case "outside-then-back":
                var outside = Path.Combine(tree.Root, "outside");
                Directory.CreateSymbolicLink(outside, ordinary);
                Directory.CreateSymbolicLink(alias, outside);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Assert.False(IsLinkContained(covering, "*/x"));
    }

    [Fact]
    public void An_alias_cannot_return_to_a_physical_ancestor_that_the_first_alias_skipped()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var tree = new TemporaryTree();
        var covering = Directory.CreateDirectory(Path.Combine(tree.Root, "cover")).FullName;
        var parent = Directory.CreateDirectory(Path.Combine(covering, "parent")).FullName;
        var child = Directory.CreateDirectory(Path.Combine(parent, "child")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(covering, "alias"), child);
        Directory.CreateSymbolicLink(Path.Combine(child, "back"), parent);

        Assert.False(IsLinkContained(covering, "a*/b*/x"));
    }

    [Theory]
    [InlineData("local", true)]
    [InlineData("sibling", false)]
    [InlineData("outside", false)]
    [InlineData("sibling-directory", false)]
    [InlineData("matching-intermediate", true)]
    [InlineData("nonmatching-leaf", false)]
    [InlineData("nonmatching-intermediate", false)]
    public void A_leaf_alias_retains_the_immediate_directory_boundary(string shape, bool complete)
    {
        if (OperatingSystem.IsWindows())
            return;

        using var tree = new TemporaryTree();
        var covering = Directory.CreateDirectory(Path.Combine(tree.Root, "cover")).FullName;
        var source = Directory.CreateDirectory(Path.Combine(covering, "a")).FullName;
        var sibling = Directory.CreateDirectory(Path.Combine(covering, "b")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(tree.Root, "outside")).FullName;
        var targetDirectory = shape switch
        {
            "local" => source,
            "sibling" or "sibling-directory" or "nonmatching-leaf" or "nonmatching-intermediate" or "matching-intermediate" => sibling,
            "outside" => outside,
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
        var target = Path.Combine(targetDirectory, "item.txt");
        File.WriteAllText(target, "test");
        var pattern = shape switch
        {
            "sibling-directory" => "*/*/",
            "nonmatching-leaf" => "*/wanted*.txt",
            "nonmatching-intermediate" => "*/x*/*.txt",
            "matching-intermediate" => "*/a*/*.txt",
            _ => "*/*.txt"
        };
        if (shape is "sibling-directory" or "nonmatching-intermediate" or "matching-intermediate")
            Directory.CreateSymbolicLink(Path.Combine(source, "alias"), targetDirectory);
        else
            File.CreateSymbolicLink(Path.Combine(source, "alias.txt"), target);

        Assert.Equal(complete, IsLinkContained(covering, pattern));
        if (shape is "local" or "sibling" or "outside")
        {
            var policy = new ToolPathPolicy(Bash52, [target]);
            Assert.True(policy.CommandReferencesDeniedPath($"cat {covering}/{pattern}", tree.Root));
        }
    }

    [Fact]
    public void A_leaf_chain_can_return_to_its_immediate_directory_within_the_original_cover()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var tree = new TemporaryTree();
        var covering = Directory.CreateDirectory(Path.Combine(tree.Root, "cover")).FullName;
        var source = Directory.CreateDirectory(Path.Combine(covering, "a")).FullName;
        var sibling = Directory.CreateDirectory(Path.Combine(covering, ".hop-directory")).FullName;
        var target = Path.Combine(source, "item.txt");
        File.WriteAllText(target, "test");
        var hop = Path.Combine(sibling, "hop");
        File.CreateSymbolicLink(hop, target);
        File.CreateSymbolicLink(Path.Combine(source, "alias.txt"), hop);

        Assert.True(IsLinkContained(covering, "a*/*.txt"));
        var policy = new ToolPathPolicy(Bash52, [target]);
        Assert.True(policy.CommandReferencesDeniedPath($"cat {covering}/a*/*.txt", tree.Root));
    }

    [Fact]
    public void A_nonmatching_alias_does_not_add_an_unrelated_target_scope()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var tree = new TemporaryTree();
        var covering = Directory.CreateDirectory(Path.Combine(tree.Root, "cover")).FullName;
        var source = Directory.CreateDirectory(Path.Combine(covering, "a")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(source, "private")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(source, "other"), target);
        var analysis = new ShellCommandAnalyzer(Bash52).Analyze($"touch {covering}/*/wanted*.txt", tree.Root);
        var occurrence = Assert.Single(analysis.Commands);
        var pattern = Assert.IsType<ShellValueDomain.PathPattern>(
            ShellGlobScope.FindGlobPattern(occurrence, occurrence.Clause.Args.Last()));
        Assert.True(Netclaw.Security.Authorization.Filesystem.CanonicalPath.TryCreateHost(covering, null, out var canonical));

        var reach = Assert.IsType<ShellGlobReach.Complete>(ShellGlobScope.ResolveReach(canonical, pattern));

        Assert.Equal(canonical, Assert.Single(reach.Scopes));
        Assert.Empty(reach.Targets);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Alias_child_reach_requires_a_remaining_pattern_segment(bool hasRemainingSegment)
    {
        if (OperatingSystem.IsWindows())
            return;

        using var tree = new TemporaryTree();
        var covering = Directory.CreateDirectory(Path.Combine(tree.Root, "cover")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(covering, "target")).FullName;
        var child = Path.Combine(target, "child.txt");
        File.WriteAllText(child, "test");
        Directory.CreateSymbolicLink(Path.Combine(covering, "alias"), target);
        var suffix = hasRemainingSegment ? "a*/*" : "a*/";
        var analysis = new ShellCommandAnalyzer(Bash52).Analyze($"ls {covering}/{suffix}", tree.Root);
        var occurrence = Assert.Single(analysis.Commands);
        var pattern = Assert.IsType<ShellValueDomain.PathPattern>(
            ShellGlobScope.FindGlobPattern(occurrence, occurrence.Clause.Args.Last()));
        Assert.True(Netclaw.Security.Authorization.Filesystem.CanonicalPath.TryCreateHost(covering, null, out var canonical));
        var reach = Assert.IsType<ShellGlobReach.Complete>(ShellGlobScope.ResolveReach(canonical, pattern));
        var alias = Assert.Single(reach.Targets);

        var match = ShellGlobScope.MatchPathToward(pattern, alias, child);

        Assert.Equal(hasRemainingSegment ? child : null, match);
    }

    [Fact]
    public void A_final_file_alias_has_the_scope_of_its_target_directory()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var tree = new TemporaryTree();
        var covering = Directory.CreateDirectory(Path.Combine(tree.Root, "cover")).FullName;
        var targetDirectory = Directory.CreateDirectory(Path.Combine(covering, "target")).FullName;
        var targetFile = Path.Combine(targetDirectory, "file.txt");
        File.WriteAllText(targetFile, "test");
        File.CreateSymbolicLink(Path.Combine(covering, "alias"), targetFile);
        var analysis = new ShellCommandAnalyzer(Bash52).Analyze($"cat {covering}/a*", tree.Root);
        var occurrence = Assert.Single(analysis.Commands);
        var pattern = Assert.IsType<ShellValueDomain.PathPattern>(
            ShellGlobScope.FindGlobPattern(occurrence, occurrence.Clause.Args.Last()));
        Assert.True(Netclaw.Security.Authorization.Filesystem.CanonicalPath.TryCreateHost(covering, null, out var canonical));

        var reach = Assert.IsType<ShellGlobReach.Complete>(ShellGlobScope.ResolveReach(canonical, pattern));

        Assert.Contains(reach.Scopes, path => path.Value == targetDirectory);
        Assert.DoesNotContain(reach.Scopes, path => path.Value == targetFile);
    }

    [Fact]
    public void Directory_only_glob_excludes_a_final_file_alias_from_protected_reach()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var tree = new TemporaryTree();
        var covering = Directory.CreateDirectory(Path.Combine(tree.Root, "cover")).FullName;
        var targetDirectory = Directory.CreateDirectory(Path.Combine(covering, "target")).FullName;
        var protectedFile = Path.Combine(covering, "private.txt");
        File.WriteAllText(protectedFile, "test");
        File.CreateSymbolicLink(Path.Combine(covering, "alias-file"), protectedFile);
        Directory.CreateSymbolicLink(Path.Combine(covering, "alias-directory"), targetDirectory);
        var policy = new ToolPathPolicy(Bash52, [protectedFile]);

        Assert.True(IsLinkContained(covering, "a*/"));
        Assert.False(policy.CommandReferencesDeniedPath($"ls -d {covering}/a*/", tree.Root));
        Assert.False(policy.CommandReferencesDeniedPath($"ls -d {covering}/a*/*/", tree.Root));
        Assert.False(policy.CommandReferencesDeniedPath($"ls -d {covering}/a*/*", tree.Root));
        Assert.True(policy.CommandReferencesDeniedPath($"cat {covering}/a*", tree.Root));
    }

    [Fact]
    public void Complete_alias_reach_retains_the_real_target_scope()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var tree = new TemporaryTree();
        var covering = Directory.CreateDirectory(Path.Combine(tree.Root, "cover")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(covering, "target")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(covering, "alias"), target);
        var analysis = new ShellCommandAnalyzer(Bash52).Analyze($"cat {covering}/a*/x", tree.Root);
        var occurrence = Assert.Single(analysis.Commands);
        var pattern = Assert.IsType<ShellValueDomain.PathPattern>(
            ShellGlobScope.FindGlobPattern(occurrence, occurrence.Clause.Args.Last()));
        Assert.True(Netclaw.Security.Authorization.Filesystem.CanonicalPath.TryCreateHost(covering, null, out var canonical));

        var reach = Assert.IsType<ShellGlobReach.Complete>(ShellGlobScope.ResolveReach(canonical, pattern));

        Assert.Contains(reach.Scopes, path => path.Value == target);
        var aliasReach = Assert.Single(reach.Targets);
        Assert.Equal(target, aliasReach.Target.Value);
        Assert.Equal(1, aliasReach.NextSegmentIndex);
    }

    private static bool IsLinkContained(string coveringDirectory, string pattern)
    {
        var analysis = new ShellCommandAnalyzer(Bash52).Analyze($"ls {coveringDirectory}/{pattern}", "/work");
        var occurrence = Assert.Single(analysis.Commands);
        var glob = Assert.IsType<ShellValueDomain.PathPattern>(
            ShellGlobScope.FindGlobPattern(occurrence, occurrence.Clause.Args.Last()));
        Assert.True(Netclaw.Security.Authorization.Filesystem.CanonicalPath.TryCreateHost(
            glob.CoveringDirectory, null, out var covering));
        return ShellGlobScope.ResolveReach(covering, glob) is ShellGlobReach.Complete;
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

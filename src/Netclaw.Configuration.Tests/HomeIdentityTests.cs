// -----------------------------------------------------------------------
// <copyright file="HomeIdentityTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Configuration.Tests;

/// <summary>
/// #2527 part 2, review round 2. <see cref="HomeIdentity"/> is the one definition of "the same Netclaw home".
/// The daemon (shutdown notice) and the CLI (stop proof) both call it. A wrong "same" here lets a command for
/// one home stop the daemon of another home.
/// </summary>
/// <remarks>
/// B1: <see cref="HomeIdentity.EnvironmentServesHome"/> reads the target process as that process would read
/// itself. S1: <see cref="HomeIdentity.Canonical"/> resolves links one component at a time, like
/// <c>realpath(3)</c>. The mutation project holds a twin of this file (<c>HomeIdentityMutationTests</c>),
/// because Stryker's adapter does not support xUnit 3. Keep the two in step.
/// </remarks>
public sealed class HomeIdentityTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string P(params string[] parts) => Path.Combine([_dir.Path, .. parts]);

    // The text of /proc/<pid>/environ: entries that end with a NUL character.
    private static string Env(params string[] entries) => string.Concat(entries.Select(entry => entry + "\0"));

    private string Folder(params string[] parts)
    {
        var path = P(parts);
        Directory.CreateDirectory(path);
        return path;
    }

    // ── B1: the environment of the target decides, never the HOME or folder of the caller ──────────

    [Fact]
    public void A_process_with_no_NETCLAW_HOME_serves_dot_netclaw_in_the_HOME_of_its_own_environment()
    {
        var profile = P("profile");

        Assert.True(HomeIdentity.EnvironmentServesHome(Env("PATH=/bin", $"HOME={profile}"), Path.Combine(profile, ".netclaw")));
    }

    [Fact]
    public void The_HOME_of_the_caller_is_not_the_HOME_of_the_target()
    {
        // The target runs with HOME=<profile>. The home of the caller is the default home of the caller
        // (HomeIdentity.DefaultHome). The old code compared the caller default with the home of the command, so a
        // command for the default home of the caller "proved" a daemon of another profile.
        var profile = P("profile");

        Assert.False(HomeIdentity.EnvironmentServesHome(Env($"HOME={profile}"), HomeIdentity.DefaultHome));
    }

    [Fact]
    public void A_process_with_no_NETCLAW_HOME_and_no_HOME_gives_no_proof_even_for_the_default_home_of_the_caller()
    {
        Assert.False(HomeIdentity.EnvironmentServesHome(Env("PATH=/bin"), HomeIdentity.DefaultHome));
        Assert.False(HomeIdentity.EnvironmentServesHome(string.Empty, HomeIdentity.DefaultHome));
    }

    [Theory]
    [InlineData("HOME=")]
    [InlineData("HOME=relative/profile")]
    [InlineData("HOME=.")]
    public void A_HOME_that_is_empty_or_not_fully_qualified_gives_no_proof(string homeEntry)
    {
        Assert.False(HomeIdentity.EnvironmentServesHome(Env(homeEntry), Path.Combine("relative", "profile", ".netclaw")));
        Assert.False(HomeIdentity.EnvironmentServesHome(Env(homeEntry), HomeIdentity.DefaultHome));
    }

    [Fact]
    public void A_declared_absolute_home_is_the_home_and_HOME_does_not_matter()
    {
        var declared = P("declared");
        var profile = P("profile");
        var environment = Env($"NETCLAW_HOME={declared}", $"HOME={profile}");

        Assert.True(HomeIdentity.EnvironmentServesHome(environment, declared));
        Assert.False(HomeIdentity.EnvironmentServesHome(environment, Path.Combine(profile, ".netclaw")));
    }

    [Fact]
    public void A_declared_absolute_home_needs_no_HOME()
    {
        var declared = P("declared");

        Assert.True(HomeIdentity.EnvironmentServesHome(Env($"NETCLAW_HOME={declared}"), declared));
    }

    [Theory]
    [InlineData("NETCLAW_HOME=")]
    [InlineData("NETCLAW_HOME= ")]
    [InlineData("NETCLAW_HOME=  \t ")]
    public void An_empty_or_white_space_NETCLAW_HOME_is_absent_as_NetclawPaths_reads_it(string declaration)
    {
        var profile = P("profile");

        Assert.True(HomeIdentity.EnvironmentServesHome(Env(declaration, $"HOME={profile}"), Path.Combine(profile, ".netclaw")));
        Assert.False(HomeIdentity.EnvironmentServesHome(Env(declaration), Path.Combine(profile, ".netclaw")));
    }

    [Fact]
    public void White_space_around_a_declared_home_is_removed_as_NetclawPaths_removes_it()
    {
        var declared = P("declared");

        Assert.True(HomeIdentity.EnvironmentServesHome(Env($"NETCLAW_HOME=  {declared} "), declared));
    }

    [Fact]
    public void A_trailing_separator_or_a_dot_segment_in_the_declared_home_names_the_same_home()
    {
        var declared = Folder("declared");
        Folder("declared", "sub");

        Assert.True(HomeIdentity.EnvironmentServesHome(Env($"NETCLAW_HOME={declared}{Path.DirectorySeparatorChar}"), declared));
        Assert.True(HomeIdentity.EnvironmentServesHome(Env($"NETCLAW_HOME={Path.Combine(declared, "sub", "..")}"), declared));
    }

    [Fact]
    public void A_trailing_separator_in_HOME_names_the_same_default_home()
    {
        var profile = Folder("profile");

        Assert.True(HomeIdentity.EnvironmentServesHome(Env($"HOME={profile}{Path.DirectorySeparatorChar}"), Path.Combine(profile, ".netclaw")));
    }

    [Theory]
    [InlineData(".netclaw-dev")]
    [InlineData("./.netclaw-dev")]
    [InlineData("../.netclaw-dev")]
    [InlineData("netclaw-home")]
    public void A_relative_NETCLAW_HOME_gives_no_proof_even_when_it_resolves_to_the_home_in_the_folder_of_the_caller(string relative)
    {
        // The daemon froze the value against its own start folder, then left that folder. The caller cannot
        // know the folder, so a path that "resolves" in the folder of the caller proves nothing.
        var resolvedByCaller = Path.GetFullPath(relative);
        var profile = P("profile");

        Assert.False(HomeIdentity.EnvironmentServesHome(Env($"NETCLAW_HOME={relative}", $"HOME={profile}"), resolvedByCaller));
        Assert.False(HomeIdentity.EnvironmentServesHome(Env($"NETCLAW_HOME={relative}", $"HOME={profile}"), Path.Combine(profile, ".netclaw")));
    }

    [Theory]
    [InlineData("~", "")]
    [InlineData("~/.netclaw", ".netclaw")]
    [InlineData("$HOME/.netclaw", ".netclaw")]
    [InlineData("${HOME}/.netclaw", ".netclaw")]
    [InlineData("$HOME", "")]
    public void A_NETCLAW_HOME_with_a_home_token_gives_no_proof_even_when_the_token_would_expand_to_the_home(
        string declared, string suffix)
    {
        // NetclawPaths expands these tokens with PathExpansion. The caller does not, so it must not guess.
        var profile = P("profile");
        var expanded = suffix.Length == 0 ? profile : Path.Combine(profile, suffix);

        Assert.False(HomeIdentity.EnvironmentServesHome(Env($"NETCLAW_HOME={declared}", $"HOME={profile}"), expanded));
        Assert.False(HomeIdentity.EnvironmentServesHome(Env($"NETCLAW_HOME={declared}", $"HOME={profile}"), profile));
    }

    [Fact]
    public void A_dollar_sign_or_a_tilde_anywhere_in_a_declared_home_gives_no_proof()
    {
        // A home path with a literal $ or ~ gets a false refusal. The refusal is safe, and the recovery text applies.
        var withDollar = P("a$b");
        var withTilde = P("a~b");

        Assert.False(HomeIdentity.EnvironmentServesHome(Env($"NETCLAW_HOME={withDollar}"), withDollar));
        Assert.False(HomeIdentity.EnvironmentServesHome(Env($"NETCLAW_HOME={withTilde}"), withTilde));
    }

    [Fact]
    public void A_different_home_a_parent_and_a_child_are_not_the_home()
    {
        var declared = P("declared");
        var environment = Env($"NETCLAW_HOME={declared}");

        Assert.False(HomeIdentity.EnvironmentServesHome(environment, P("other")));
        Assert.False(HomeIdentity.EnvironmentServesHome(environment, _dir.Path));
        Assert.False(HomeIdentity.EnvironmentServesHome(environment, Path.Combine(declared, "workspaces")));
        Assert.False(HomeIdentity.EnvironmentServesHome(environment, declared + "2"));
    }

    [Fact]
    public void Variables_that_only_end_or_start_like_the_names_are_ignored()
    {
        var profile = P("profile");
        var environment = Env(
            $"XNETCLAW_HOME={P("x")}", $"NETCLAW_HOME_EXTRA={P("y")}", $"XHOME={P("z")}", $"MYHOME={P("w")}", $"HOME={profile}");

        Assert.True(HomeIdentity.EnvironmentServesHome(environment, Path.Combine(profile, ".netclaw")));
        Assert.False(HomeIdentity.EnvironmentServesHome(environment, P("x")));
        Assert.False(HomeIdentity.EnvironmentServesHome(environment, Path.Combine(P("z"), ".netclaw")));
    }

    [Fact]
    public void A_value_may_hold_an_equals_sign()
    {
        var declared = P("a=b");

        Assert.True(HomeIdentity.EnvironmentServesHome(Env($"NETCLAW_HOME={declared}"), declared));
    }

    // ── S1: Canonical resolves one link at a time ───────────────────────────────────────────────────

    // The layout of the review: b -> q/r, a -> b/c, q/r/c/link -> ../../y. The real folder of a/link is q/y.
    private void MakeReviewerLayout()
    {
        Folder("q", "r", "c");
        Folder("q", "y");
        Folder("y");
        Directory.CreateSymbolicLink(P("b"), Path.Combine("q", "r"));
        Directory.CreateSymbolicLink(P("a"), Path.Combine("b", "c"));
        Directory.CreateSymbolicLink(P("q", "r", "c", "link"), Path.Combine("..", "..", "y"));
    }

    [SlopwatchSuppress("SW001", "Creating a symbolic link needs a privilege on Windows.")]
    [Fact(SkipUnless = nameof(TestPlatform.IsPosix), SkipType = typeof(TestPlatform), Skip = "Creating a symbolic link needs a privilege on Windows.")]
    public void A_dot_dot_in_a_link_target_leaves_the_resolved_folder_not_the_folder_that_the_text_named()
    {
        MakeReviewerLayout();

        Assert.False(HomeIdentity.Same(P("a", "link"), P("y")), "a/link was joined with the other folder y.");
        Assert.True(HomeIdentity.Same(P("a", "link"), P("q", "y")), "a/link is the folder q/y.");
        Assert.Equal(HomeIdentity.Canonical(P("q", "y")), HomeIdentity.Canonical(P("a", "link")));
        Assert.NotEqual(HomeIdentity.Canonical(P("y")), HomeIdentity.Canonical(P("a", "link")));
    }

    [SlopwatchSuppress("SW001", "Creating a symbolic link needs a privilege on Windows.")]
    [Fact(SkipUnless = nameof(TestPlatform.IsPosix), SkipType = typeof(TestPlatform), Skip = "Creating a symbolic link needs a privilege on Windows.")]
    public void A_dot_dot_in_the_target_of_a_link_below_a_link_leaves_the_real_parent()
    {
        // mid -> real, alias -> mid/x, and real/x/l -> ../z. The folder of alias/l is real/z. A walk that joins
        // the target to the text mid/x would name mid/z and never reach real/z.
        Folder("real", "x");
        Folder("real", "z");
        Directory.CreateSymbolicLink(P("mid"), "real");
        Directory.CreateSymbolicLink(P("alias"), Path.Combine("mid", "x"));
        Directory.CreateSymbolicLink(P("real", "x", "l"), Path.Combine("..", "z"));

        Assert.True(HomeIdentity.Same(P("alias", "l"), P("real", "z")));
        Assert.True(HomeIdentity.Same(P("alias", "l"), P("mid", "z")));
        Assert.Equal(HomeIdentity.Canonical(P("real", "z")), HomeIdentity.Canonical(P("alias", "l")));
    }

    [SlopwatchSuppress("SW001", "Creating a symbolic link needs a privilege on Windows.")]
    [Fact(SkipUnless = nameof(TestPlatform.IsPosix), SkipType = typeof(TestPlatform), Skip = "Creating a symbolic link needs a privilege on Windows.")]
    public void An_absolute_link_target_restarts_the_walk_from_its_root()
    {
        var real = Folder("real", "home");
        Directory.CreateSymbolicLink(P("first"), real);
        Directory.CreateSymbolicLink(P("second"), P("first"));
        Directory.CreateSymbolicLink(P("third"), P("second"));

        Assert.True(HomeIdentity.Same(P("third"), real));
        Assert.Equal(HomeIdentity.Canonical(real), HomeIdentity.Canonical(P("third")));
        Assert.False(HomeIdentity.Same(P("third"), P("real")));
    }

    [SlopwatchSuppress("SW001", "Creating a symbolic link needs a privilege on Windows.")]
    [Fact(SkipUnless = nameof(TestPlatform.IsPosix), SkipType = typeof(TestPlatform), Skip = "Creating a symbolic link needs a privilege on Windows.")]
    public void A_link_in_a_parent_folder_resolves_as_the_var_to_private_var_link_of_macOS()
    {
        Folder("private", "var", "folders", "home");
        Directory.CreateSymbolicLink(P("var"), Path.Combine("private", "var"));
        Folder("var2", "folders", "home");

        Assert.True(HomeIdentity.Same(P("var", "folders", "home"), P("private", "var", "folders", "home")));
        Assert.True(HomeIdentity.Same(P("private", "var", "folders", "home"), P("var", "folders", "home")));
        Assert.False(HomeIdentity.Same(P("var", "folders", "home"), P("var2", "folders", "home")));
    }

    [SlopwatchSuppress("SW001", "Creating a symbolic link needs a privilege on Windows.")]
    [Fact(SkipUnless = nameof(TestPlatform.IsPosix), SkipType = typeof(TestPlatform), Skip = "Creating a symbolic link needs a privilege on Windows.")]
    public void A_dot_dot_in_the_text_of_the_path_collapses_before_any_link_is_read()
    {
        // NetclawPaths collapses the dot segments of its text with GetFullPath before it reads a folder. The
        // comparison does the same, so the daemon and the CLI read one spelling in one way.
        Folder("deep", "dir");
        Folder("x");
        Directory.CreateSymbolicLink(P("link"), P("deep", "dir"));

        Assert.True(HomeIdentity.Same(P("link", "..", "x"), P("x")));
        Assert.False(HomeIdentity.Same(P("link", "..", "x"), P("deep", "x")));
    }

    [Fact]
    public void A_trailing_separator_and_a_missing_part_keep_their_spelling()
    {
        var existing = Folder("home");

        Assert.True(HomeIdentity.Same(existing + Path.DirectorySeparatorChar, existing));
        Assert.True(HomeIdentity.Same(P("no", "such", "home"), P("no", "such", "home") + Path.DirectorySeparatorChar));
        Assert.False(HomeIdentity.Same(P("no", "such", "home"), P("no", "such", "other")));
        Assert.Equal(P("no", "such", "home"), HomeIdentity.Canonical(P("no", "such", "home")));
    }

    [Fact]
    public void A_prefix_and_a_child_are_not_the_same_home()
    {
        var home = Folder("home");
        Folder("home2");
        Folder("home", "workspaces");

        Assert.False(HomeIdentity.Same(home, P("home2")));
        Assert.False(HomeIdentity.Same(home, P("home", "workspaces")));
        Assert.False(HomeIdentity.Same(home, _dir.Path));
    }

    [SlopwatchSuppress("SW001", "The test needs a case-sensitive file system.")]
    [Fact(SkipUnless = nameof(TestPlatform.IsLinux), SkipType = typeof(TestPlatform), Skip = "The test needs a case-sensitive file system.")]
    public void Paths_that_differ_only_in_case_are_two_homes_on_Linux()
    {
        Folder("Home");
        Folder("home");

        Assert.False(HomeIdentity.Same(P("Home"), P("home")));
    }

    // ── S1: a cycle and the bound ────────────────────────────────────────────────────────────────────

    [SlopwatchSuppress("SW001", "Creating a symbolic link needs a privilege on Windows.")]
    [Fact(SkipUnless = nameof(TestPlatform.IsPosix), SkipType = typeof(TestPlatform), Skip = "Creating a symbolic link needs a privilege on Windows.")]
    public void A_link_cycle_gives_no_resolved_path_and_no_match()
    {
        Directory.CreateSymbolicLink(P("a"), "b");
        Directory.CreateSymbolicLink(P("b"), "a");
        Directory.CreateSymbolicLink(P("self"), "self");

        var cycle = Assert.Throws<IOException>(() => HomeIdentity.Canonical(P("a")));
        Assert.Contains("40 links", cycle.Message, StringComparison.Ordinal);
        Assert.Throws<IOException>(() => HomeIdentity.Canonical(P("self")));
        Assert.Throws<IOException>(() => HomeIdentity.Canonical(Path.Combine(P("a"), "below")));
        Assert.False(HomeIdentity.Same(P("a"), P("a")));
        Assert.False(HomeIdentity.Same(P("self"), P("self")));
        Assert.False(HomeIdentity.Same(P("a"), P("b")));
        Assert.False(HomeIdentity.EnvironmentServesHome(Env($"NETCLAW_HOME={P("a")}"), P("a")));
    }

    [SlopwatchSuppress("SW001", "Creating a symbolic link needs a privilege on Windows.")]
    [Fact(SkipUnless = nameof(TestPlatform.IsPosix), SkipType = typeof(TestPlatform), Skip = "Creating a symbolic link needs a privilege on Windows.")]
    public void A_chain_of_forty_links_resolves_and_a_chain_of_forty_one_does_not()
    {
        var real = Folder("real");
        var previous = real;
        for (var i = 1; i <= 41; i++)
        {
            var link = P($"link-{i:00}");
            Directory.CreateSymbolicLink(link, previous);
            previous = link;
        }

        Assert.Equal(HomeIdentity.Canonical(real), HomeIdentity.Canonical(P("link-40")));
        Assert.True(HomeIdentity.Same(P("link-40"), real));
        var tooMany = Assert.Throws<IOException>(() => HomeIdentity.Canonical(P("link-41")));
        Assert.Contains("40 links", tooMany.Message, StringComparison.Ordinal);
        Assert.False(HomeIdentity.Same(P("link-41"), real));
    }

    [SlopwatchSuppress("SW001", "Creating a symbolic link needs a privilege on Windows.")]
    [Fact(SkipUnless = nameof(TestPlatform.IsPosix), SkipType = typeof(TestPlatform), Skip = "Creating a symbolic link needs a privilege on Windows.")]
    public void Dot_segments_in_a_link_target_are_skipped()
    {
        Folder("real", "sub");
        Directory.CreateSymbolicLink(P("link"), Path.Combine(".", "real", ".", "sub", "."));

        Assert.True(HomeIdentity.Same(P("link"), P("real", "sub")));
        Assert.Equal(HomeIdentity.Canonical(P("real", "sub")), HomeIdentity.Canonical(P("link")));
    }

    [SlopwatchSuppress("SW001", "Creating a symbolic link needs a privilege on Windows.")]
    [Fact(SkipUnless = nameof(TestPlatform.IsPosix), SkipType = typeof(TestPlatform), Skip = "Creating a symbolic link needs a privilege on Windows.")]
    public void A_dot_dot_in_a_link_target_stops_at_the_root_of_the_file_system()
    {
        // realpath(3): the parent of the root is the root.
        var root = Path.GetPathRoot(_dir.Path)!;
        var depth = _dir.Path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Length;
        var target = string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat("..", depth + 3).Append("zz-no-such-folder"));
        Directory.CreateSymbolicLink(P("up"), target);

        Assert.Equal(Path.Combine(root, "zz-no-such-folder"), HomeIdentity.Canonical(P("up")));
    }
}

// -----------------------------------------------------------------------
// <copyright file="PathUtilityTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Security.Tests;

public sealed class PathUtilityTests
{
    [Fact]
    public void Normalize_removes_trailing_separator()
    {
        var result = PathUtility.Normalize("/home/user/");
        Assert.False(result.EndsWith('/'));
        Assert.False(result.EndsWith('\\'));
    }

    [Fact]
    public void Normalize_resolves_dot_sequences()
    {
        var result = PathUtility.Normalize("/home/user/../user/docs");
        Assert.DoesNotContain("..", result);
    }

    [Fact]
    public void TryNormalize_returns_true_for_valid_path()
    {
        var success = PathUtility.TryNormalize("/home/user", out var normalized);
        Assert.True(success);
        Assert.False(string.IsNullOrEmpty(normalized));
    }

    [Fact]
    public void TryNormalize_returns_false_for_invalid_path()
    {
        var success = PathUtility.TryNormalize("path\0with\0nulls", out var normalized);
        Assert.False(success);
        Assert.Empty(normalized);
    }

    [Fact]
    public void TryNormalize_respects_working_directory()
    {
        var workingDir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var success = PathUtility.TryNormalize("relative/path", workingDir, out var normalized);
        Assert.True(success);
        Assert.StartsWith(workingDir, normalized);
    }

    [Fact]
    public void IsWithinRoot_returns_true_for_exact_match()
    {
        Assert.True(PathUtility.IsWithinRoot("/home/user", "/home/user"));
    }

    [Fact]
    public void IsWithinRoot_returns_true_for_child_path()
    {
        Assert.True(PathUtility.IsWithinRoot("/home/user/docs/file.txt", "/home/user"));
    }

    [Fact]
    public void IsWithinRoot_returns_false_for_prefix_without_boundary()
    {
        Assert.False(PathUtility.IsWithinRoot("/home/usersecret/file.txt", "/home/user"));
    }

    [Fact]
    public void IsWithinRoot_returns_false_for_unrelated_path()
    {
        Assert.False(PathUtility.IsWithinRoot("/var/log/app.log", "/home/user"));
    }

    [Fact]
    public void IsWithinRoot_handles_trailing_separators()
    {
        Assert.True(PathUtility.IsWithinRoot("/home/user/docs/", "/home/user/"));
        Assert.True(PathUtility.IsWithinRoot("/home/user/docs", "/home/user/"));
        Assert.True(PathUtility.IsWithinRoot("/home/user/docs/", "/home/user"));
    }

    [Fact]
    public void IsWithinAnyRoot_returns_false_for_empty_roots()
    {
        Assert.False(PathUtility.IsWithinAnyRoot("/home/user", Array.Empty<string>()));
    }

    [Fact]
    public void IsWithinAnyRoot_returns_true_when_in_first_root()
    {
        var roots = new[] { "/home/user", "/var/data" };
        Assert.True(PathUtility.IsWithinAnyRoot("/home/user/file.txt", roots));
    }

    [Fact]
    public void IsWithinAnyRoot_returns_true_when_in_last_root()
    {
        var roots = new[] { "/home/user", "/var/data" };
        Assert.True(PathUtility.IsWithinAnyRoot("/var/data/file.txt", roots));
    }

    [Fact]
    public void IsWithinAnyRoot_returns_false_when_in_no_root()
    {
        var roots = new[] { "/home/user", "/var/data" };
        Assert.False(PathUtility.IsWithinAnyRoot("/etc/passwd", roots));
    }

    [Fact]
    public void ExpandHome_expands_tilde()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            return;

        var result = PathUtility.ExpandHome("~/docs");
        Assert.StartsWith(home, result);
        Assert.DoesNotContain("~", result);
    }

    [Fact]
    public void ExpandHome_expands_tilde_alone()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            return;

        var result = PathUtility.ExpandHome("~");
        Assert.Equal(home, result);
    }

    [Fact]
    public void ExpandHome_expands_dollar_home()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            return;

        var result = PathUtility.ExpandHome("$HOME/docs");
        Assert.StartsWith(home, result);
        Assert.DoesNotContain("$HOME", result);
    }

    [Fact]
    public void ExpandHome_expands_braced_home()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            return;

        var result = PathUtility.ExpandHome("${HOME}/docs");
        Assert.StartsWith(home, result);
        Assert.DoesNotContain("${HOME}", result);
    }

    [Fact]
    public void ExpandHome_expands_userprofile()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            return;

        var result = PathUtility.ExpandHome("%USERPROFILE%/docs");
        Assert.StartsWith(home, result);
        Assert.DoesNotContain("%USERPROFILE%", result);
    }

    [Fact]
    public void ExpandHome_returns_unchanged_for_absolute_path()
    {
        var result = PathUtility.ExpandHome("/absolute/path");
        Assert.Equal("/absolute/path", result);
    }

    [Fact]
    public void ExpandAndNormalize_combines_expansion_and_normalization()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            return;

        var result = PathUtility.ExpandAndNormalize("~/docs/../docs");
        Assert.NotNull(result);
        Assert.StartsWith(home, result);
        Assert.DoesNotContain("..", result);
    }

    [Fact]
    public void ExpandAndNormalize_returns_null_for_invalid_path()
    {
        var result = PathUtility.ExpandAndNormalize("path\0with\0nulls");
        Assert.Null(result);
    }
}

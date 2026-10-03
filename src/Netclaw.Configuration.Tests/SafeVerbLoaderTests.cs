// -----------------------------------------------------------------------
// <copyright file="SafeVerbLoaderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class SafeVerbLoaderTests
{
    [Theory]
    [InlineData(false, ApprovalShell.Bash, ApprovalShell.PowerShell)]
    [InlineData(true, ApprovalShell.PowerShell, ApprovalShell.Bash)]
    public void Load_parses_each_bundled_catalog_for_its_own_shell(
        bool isWindows,
        ApprovalShell ownShell,
        ApprovalShell otherShell)
    {
        var list = SafeVerbLoader.Load(isWindows);

        // Each phrase matches as a reviewed diagnostic for the shell of its
        // platform file, and for no other shell.
        Assert.NotEmpty(list.Verbs);
        Assert.All(list.Verbs, phrase =>
        {
            var tokens = phrase.Split(' ');
            Assert.True(list.TryMatchReviewedDiagnostic(ownShell, tokens, out var count));
            Assert.Equal(tokens.Length, count);
            Assert.False(list.TryMatchReviewedDiagnostic(otherShell, tokens, out _));
        });
    }

    [Fact]
    public void Load_public_overload_returns_current_OS_defaults()
    {
        // Smoke test on the parameterless overload: it always returns a
        // non-empty list — the embedded resource is required at build time.
        var list = SafeVerbLoader.Load();

        Assert.NotEmpty(list.Verbs);
    }

    [Fact]
    public void Contains_uses_platform_correct_case_rules()
    {
        var linux = SafeVerbLoader.Load(isWindows: false);
        var windows = SafeVerbLoader.Load(isWindows: true);

        Assert.False(linux.Contains("LS"));
        Assert.True(linux.Contains("ls"));
        Assert.True(windows.Contains("GET-CONTENT"));
        Assert.True(windows.Contains("Get-Content"));
    }

    [Fact]
    public void Compatibility_factory_preserves_exact_phrase_text()
    {
        var list = SafeVerbList.FromVerbs(ApprovalShell.Bash, ["  git  status  "]);

        Assert.True(list.Contains("git  status"));
        Assert.False(list.Contains("git status"));
        Assert.True(list.TryMatchReviewedDiagnostic(
            ApprovalShell.Bash,
            ["git", "status", "--short"],
            out var matchedTokenCount));
        Assert.Equal(2, matchedTokenCount);
    }

    [Fact]
    public void Reviewed_phrase_matches_only_a_canonical_token_prefix_for_its_shell()
    {
        var linux = SafeVerbLoader.Load(isWindows: false);
        var windows = SafeVerbLoader.Load(isWindows: true);

        Assert.True(linux.TryMatchReviewedDiagnostic(
            ApprovalShell.Bash,
            ["git", "ls-tree", "feature"],
            out var linuxTokenCount));
        Assert.Equal(2, linuxTokenCount);
        Assert.False(linux.TryMatchReviewedDiagnostic(
            ApprovalShell.Bash,
            ["git", "ls-treex", "feature"],
            out _));
        Assert.False(linux.TryMatchReviewedDiagnostic(
            ApprovalShell.PowerShell,
            ["git", "ls-tree", "feature"],
            out _));
        Assert.True(windows.TryMatchReviewedDiagnostic(
            ApprovalShell.PowerShell,
            ["get-content", "README.md"],
            out var windowsTokenCount));
        Assert.Equal(1, windowsTokenCount);
        Assert.False(windows.TryMatchReviewedDiagnostic(
            ApprovalShell.Bash,
            ["Get-Content", "README.md"],
            out _));
    }

    [Fact]
    public void Reviewed_phrase_match_returns_the_longest_prefix()
    {
        var list = SafeVerbList.FromVerbs(
            ApprovalShell.Bash,
            ["git", "git status"]);

        Assert.True(list.TryMatchReviewedDiagnostic(
            ApprovalShell.Bash,
            ["git", "status", "--short"],
            out var matchedTokenCount));
        Assert.Equal(2, matchedTokenCount);
    }

    [Fact]
    public void Operand_match_uses_platform_case_rules_and_exact_verb_identity()
    {
        var linux = SafeVerbLoader.Load(isWindows: false);
        var windows = SafeVerbLoader.Load(isWindows: true);

        Assert.True(linux.IsOperandBearingMatch("git ls-tree feature", "git ls-tree"));
        Assert.False(linux.IsOperandBearingMatch("GIT LS-TREE feature", "git ls-tree"));
        Assert.True(windows.IsOperandBearingMatch("GIT LS-TREE feature", "git ls-tree"));
        Assert.False(windows.IsOperandBearingMatch("Get-Content X", "git ls-tree"));
        Assert.False(windows.IsOperandBearingMatch("gh run list X", "git ls-tree"));
    }

    [Fact]
    public void Load_has_no_disk_loading_surface()
    {
        // Architectural assertion: the loader's public API exposes no
        // overload that accepts an external file path. This test fails to
        // compile if a future PR re-introduces an override-file load path,
        // turning the security tightening into a hard contract.
        var methods = typeof(SafeVerbLoader)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        foreach (var method in methods)
        {
            if (method.Name != "Load")
                continue;

            foreach (var param in method.GetParameters())
            {
                Assert.False(
                    param.ParameterType == typeof(string) || param.ParameterType == typeof(NetclawPaths),
                    $"SafeVerbLoader.{method.Name} must not expose a string or NetclawPaths parameter — "
                    + "the safe-verbs list is immutable at runtime by design. Found parameter '{param.Name}' of type {param.ParameterType.Name}.");
            }
        }
    }
}

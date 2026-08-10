// -----------------------------------------------------------------------
// <copyright file="SafeVerbLoaderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class SafeVerbLoaderTests
{
    [Fact]
    public void Load_returns_bundled_linux_defaults()
    {
        var list = SafeVerbLoader.Load(isWindows: false);

        // Spot-check a few entries from the spec's default Linux list.
        Assert.True(list.Contains("ls"));
        Assert.True(list.Contains("grep"));
        Assert.True(list.Contains("git status"));
        Assert.False(list.Contains("sed -n"));
        Assert.False(list.Contains("git push"));
        Assert.False(list.Contains("rm"));

        // Read-only system/info verbs and read-only git/gh queries added by
        // the safe-verb expansion.
        Assert.True(list.Contains("date"));
        Assert.True(list.Contains("uname"));
        Assert.True(list.Contains("whoami"));
        Assert.True(list.Contains("git describe"));
        Assert.True(list.Contains("gh pr view"));
        Assert.True(list.Contains("gh run list"));

        // Excluded on purpose: env can prefix an arbitrary command; git fetch
        // mutates the object store; gh api can issue any HTTP method; printenv
        // and ps dump environment/process state the safe-space gate cannot
        // scope; gh auth status --show-token would print the GitHub token.
        Assert.False(list.Contains("env"));
        Assert.False(list.Contains("git fetch"));
        Assert.False(list.Contains("gh api"));
        Assert.False(list.Contains("printenv"));
        Assert.False(list.Contains("ps"));
        Assert.False(list.Contains("gh auth status"));
    }

    [Fact]
    public void Load_returns_bundled_windows_defaults()
    {
        var list = SafeVerbLoader.Load(isWindows: true);

        // Spot-check a few entries from the spec's default Windows list.
        Assert.True(list.Contains("dir"));
        Assert.True(list.Contains("Get-Content"));
        Assert.True(list.Contains("Test-Path"));
        Assert.True(list.Contains("git status"));
        Assert.False(list.Contains("Remove-Item"));

        // Read-only verbs added by the safe-verb expansion.
        Assert.True(list.Contains("Get-Date"));
        Assert.True(list.Contains("whoami"));
        Assert.True(list.Contains("git describe"));
        Assert.True(list.Contains("gh pr view"));

        // Excluded on purpose: gh api can issue any HTTP method; Get-Process
        // exposes other processes' state; gh auth status --show-token would
        // print the GitHub token.
        Assert.False(list.Contains("gh api"));
        Assert.False(list.Contains("Get-Process"));
        Assert.False(list.Contains("gh auth status"));
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

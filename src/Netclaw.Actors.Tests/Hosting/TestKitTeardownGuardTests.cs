// -----------------------------------------------------------------------
// <copyright file="TestKitTeardownGuardTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;

namespace Netclaw.Actors.Tests.Hosting;

public sealed class TestKitTeardownGuardTests
{
    private static readonly Regex AfterAllOverride = new(
        @"override\s+(async\s+)?Task\s+AfterAllAsync\s*\(\s*\)",
        RegexOptions.Compiled);

    // TestKit fails the test when AfterAllAsync takes more than 5 seconds, and a
    // test cannot change the limit. The actor system is also still running, so an
    // actor can hold or write a file in the folder. A TestKit class deletes what
    // it owns in IAsyncDisposable.DisposeAsync, after base.DisposeAsync().
    private static readonly Regex Cleanup = new(
        @"\b(Directory|File)\.Delete\s*\(|\.Dispose(Async)?\s*\(",
        RegexOptions.Compiled);

    [Fact]
    public void AfterAllAsync_overrides_do_not_delete_or_dispose_what_the_test_owns()
    {
        var root = FindRepoRoot();
        var offenders = new[] { "src", "tests" }
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories))
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(static path => AfterAllBodies(File.ReadAllText(path)).Any(Cleanup.IsMatch))
            .Select(path => Path.GetRelativePath(root, path))
            .Order()
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Delete temp folders in IAsyncDisposable.DisposeAsync after base.DisposeAsync(), not in AfterAllAsync: "
            + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("await base.AfterAllAsync(); await _tempDir.DisposeAsync();", true)]
    [InlineData("await base.AfterAllAsync(); if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }", true)]
    [InlineData("_dir.Dispose(); await base.AfterAllAsync();", true)]
    [InlineData("await _channel.StopAsync(CancellationToken.None); await base.AfterAllAsync();", false)]
    public void The_scan_finds_cleanup_inside_an_AfterAllAsync_body(string body, bool expected)
    {
        var source = "class T : TestKit { protected override async Task AfterAllAsync() { " + body
            + " } void Other() { _dir.Dispose(); } }";

        Assert.Equal(expected, AfterAllBodies(source).Any(Cleanup.IsMatch));
    }

    private static IEnumerable<string> AfterAllBodies(string source)
    {
        foreach (Match match in AfterAllOverride.Matches(source))
        {
            var open = source.IndexOf('{', match.Index + match.Length);
            if (open < 0)
                continue;

            var depth = 0;
            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{')
                    depth++;
                else if (source[i] == '}' && --depth == 0)
                {
                    yield return source[open..(i + 1)];
                    break;
                }
            }
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "IMPLEMENTATION_PLAN.md")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test output directory.");
    }
}

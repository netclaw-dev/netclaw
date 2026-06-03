// -----------------------------------------------------------------------
// <copyright file="SemVerConformanceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration.Feeds;
using Xunit;

namespace Netclaw.Configuration.Tests;

/// <summary>
/// Cross-language conformance: the C# <see cref="SemVer"/> comparator must order the
/// shared fixture (feeds/scripts/semver-order.txt) identically to the bash/python
/// release-manifest generator key (feeds/scripts/semver_key.py, asserted in CI via its
/// <c>--check</c> mode). Both sides validate against the same file, so if either
/// precedence implementation drifts from the canonical order, its check fails.
/// </summary>
public sealed class SemVerConformanceTests
{
    private static List<string> ReadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "semver-order.txt");
        Assert.True(File.Exists(path), $"conformance fixture not found at {path}");
        return File.ReadAllLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();
    }

    [Fact]
    public void Fixture_is_in_strictly_ascending_precedence_order()
    {
        var versions = ReadFixture();
        Assert.True(versions.Count > 1, "fixture should contain multiple versions");

        for (var i = 1; i < versions.Count; i++)
        {
            Assert.True(SemVer.IsNewer(versions[i - 1], versions[i]),
                $"expected '{versions[i]}' to outrank '{versions[i - 1]}'");
            Assert.False(SemVer.IsNewer(versions[i], versions[i - 1]),
                $"'{versions[i - 1]}' must not outrank '{versions[i]}'");
        }
    }

    [Fact]
    public void Sorting_by_SemVer_reproduces_the_fixture_order()
    {
        var fixture = ReadFixture();

        // Start from the reversed fixture (deterministic, not already in canonical order)
        // and sort with the comparator — the result must equal the canonical fixture.
        var sorted = Enumerable.Reverse(fixture)
            .OrderBy(v => v, Comparer<string>.Create((a, b) =>
            {
                Assert.True(SemVer.TryCompare(a, b, out var c), $"unparseable version: '{a}' or '{b}'");
                return c;
            }))
            .ToList();

        Assert.Equal(fixture, sorted);
    }
}

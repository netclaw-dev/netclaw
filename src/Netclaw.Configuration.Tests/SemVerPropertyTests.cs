// -----------------------------------------------------------------------
// <copyright file="SemVerPropertyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using CsCheck;
using Netclaw.Configuration.Feeds;
using Xunit;

namespace Netclaw.Configuration.Tests;

/// <summary>
/// Property-based tests for <see cref="SemVer"/>: instead of fixed examples, generate
/// thousands of random valid versions and assert the comparator obeys the algebraic laws
/// of a SemVer-2.0.0 total order plus the spec's precedence anchors. A failure shrinks to
/// a minimal counterexample.
/// </summary>
public sealed class SemVerPropertyTests
{
    private const long Iter = 100_000;

    // A prerelease identifier: a numeric identifier (multi-digit, so the dotted form
    // beta.10 vs beta.2 is exercised) or an alphanumeric one.
    private static readonly Gen<string> GenPreId =
        Gen.OneOf(
            Gen.Int[0, 30].Select(n => n.ToString()),
            Gen.OneOfConst("alpha", "beta", "rc", "x", "a1", "1a"));

    // A valid SemVer string: small core components (so collisions exercise the
    // equal/transitive paths) plus 0-3 prerelease identifiers.
    private static readonly Gen<string> GenVersion =
        Gen.Select(Gen.Int[0, 3], Gen.Int[0, 3], Gen.Int[0, 3], GenPreId.Array[0, 3],
            (major, minor, patch, pre) =>
            {
                var core = $"{major}.{minor}.{patch}";
                return pre.Length == 0 ? core : $"{core}-{string.Join('.', pre)}";
            });

    [Fact]
    public void Every_generated_version_parses_and_equals_itself()
        => GenVersion.Sample(
            v => SemVer.TryCompare(v, v, out var c) && c == 0,
            iter: Iter);

    [Fact]
    public void Comparison_is_antisymmetric()
        => Gen.Select(GenVersion, GenVersion).Sample(
            pair =>
            {
                var (a, b) = pair;
                SemVer.TryCompare(a, b, out var ab);
                SemVer.TryCompare(b, a, out var ba);
                return Math.Sign(ab) == -Math.Sign(ba);
            },
            iter: Iter);

    [Fact]
    public void Comparison_is_transitive()
        => Gen.Select(GenVersion, GenVersion, GenVersion).Sample(
            triple =>
            {
                var (a, b, c) = triple;
                SemVer.TryCompare(a, b, out var ab);
                SemVer.TryCompare(b, c, out var bc);
                SemVer.TryCompare(a, c, out var ac);
                // a <= b and b <= c  =>  a <= c
                return !(ab <= 0 && bc <= 0) || ac <= 0;
            },
            iter: Iter);

    [Fact]
    public void IsNewer_is_consistent_with_compare()
        => Gen.Select(GenVersion, GenVersion).Sample(
            pair =>
            {
                var (a, b) = pair;
                SemVer.TryCompare(a, b, out var ab);
                return SemVer.IsNewer(a, b) == (ab < 0);
            },
            iter: Iter);

    [Fact]
    public void Build_metadata_does_not_affect_precedence()
        => Gen.Select(GenVersion, Gen.OneOfConst("build", "sha.1", "abc123", "2026.06.03"))
            .Sample(
                pair =>
                {
                    var (v, meta) = pair;
                    SemVer.TryCompare(v, $"{v}+{meta}", out var c);
                    return c == 0;
                },
                iter: Iter);

    [Fact]
    public void Stable_outranks_its_own_prerelease()
        => Gen.Select(Gen.Int[0, 3], Gen.Int[0, 3], Gen.Int[0, 3], GenPreId.Array[1, 3],
                (major, minor, patch, pre) =>
                {
                    var core = $"{major}.{minor}.{patch}";
                    return (stable: core, prerelease: $"{core}-{string.Join('.', pre)}");
                })
            .Sample(
                pair =>
                {
                    SemVer.TryCompare(pair.stable, pair.prerelease, out var c);
                    return c > 0;
                },
                iter: Iter);

    [Fact]
    public void Numeric_identifier_ranks_below_alphanumeric()
        => Gen.Select(Gen.Int[0, 3], Gen.Int[0, 3], Gen.Int[0, 3], Gen.Int[0, 9],
                Gen.OneOfConst("alpha", "beta", "x"),
                (major, minor, patch, num, alpha) =>
                {
                    var core = $"{major}.{minor}.{patch}";
                    return (numeric: $"{core}-{num}", alphanumeric: $"{core}-{alpha}");
                })
            .Sample(
                pair =>
                {
                    SemVer.TryCompare(pair.numeric, pair.alphanumeric, out var c);
                    return c < 0;
                },
                iter: Iter);
}

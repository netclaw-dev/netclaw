// -----------------------------------------------------------------------
// <copyright file="SnowflakeCursorComparerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

/// <summary>
/// Proves the string comparer orders snowflake cursors exactly like
/// <see cref="ulong"/> comparison. The Discord binding actor holds its
/// cursor as a string and relies on this equivalence.
/// </summary>
public sealed class SnowflakeCursorComparerTests
{
    // A fixed representative set. It covers short synthetic test IDs, the
    // adjacent powers of ten where digit length changes, real 18-digit and
    // 19-digit Discord snowflakes, and the ulong boundary. The set is
    // deliberately fixed, not random, so a failure always reproduces.
    private static readonly ulong[] RepresentativeValues =
    [
        0UL,
        1UL,
        2UL,
        9UL,
        10UL,
        11UL,
        99UL,
        100UL,
        101UL,
        999UL,
        1_000UL,
        99_999_999_999_999_999UL,
        100_000_000_000_000_000UL,
        123_456_789_012_345_678UL,
        900_000_000_000_000_000UL,
        999_999_999_999_999_999UL,
        1_000_000_000_000_000_000UL,
        1_000_000_000_000_000_001UL,
        1_100_000_000_000_000_000UL,
        9_999_999_999_999_999_999UL,
        ulong.MaxValue
    ];

    [Fact]
    public void Comparer_matches_numeric_order_for_every_pair()
    {
        foreach (var left in RepresentativeValues)
        {
            foreach (var right in RepresentativeValues)
            {
                var expected = left.CompareTo(right);
                var actual = SnowflakeCursorComparer.Instance.Compare(
                    left.ToString(), right.ToString());

                Assert.Equal(expected, Math.Sign(actual));
            }
        }
    }

    [Fact]
    public void Shorter_snowflake_orders_before_longer_snowflake()
    {
        // Plain ordinal comparison fails this pair: '9' sorts after '1'.
        Assert.True(SnowflakeCursorComparer.Instance.Compare(
            "999999999999999999", "1000000000000000000") < 0);
        Assert.True(string.CompareOrdinal("999999999999999999", "1000000000000000000") > 0);
    }

    [Fact]
    public void Equal_values_compare_equal()
    {
        Assert.Equal(0, SnowflakeCursorComparer.Instance.Compare(
            "1234567890123456789", "1234567890123456789"));
        Assert.Equal(0, SnowflakeCursorComparer.Instance.Compare(null, null));
    }

    [Fact]
    public void Missing_cursor_orders_before_any_snowflake()
    {
        Assert.True(SnowflakeCursorComparer.Instance.Compare(null, "0") < 0);
        Assert.True(SnowflakeCursorComparer.Instance.Compare("0", null) > 0);
    }

    [Fact]
    public void Sorted_string_order_matches_sorted_numeric_order()
    {
        // Start from ordinal string order, which is wrong on purpose: the
        // comparer must repair it into numeric order.
        var byString = RepresentativeValues
            .Select(v => v.ToString())
            .OrderBy(v => v, StringComparer.Ordinal)
            .OrderBy(v => v, SnowflakeCursorComparer.Instance)
            .ToArray();
        var byNumber = RepresentativeValues
            .OrderBy(v => v)
            .Select(v => v.ToString())
            .ToArray();

        Assert.Equal(byNumber, byString);
    }
}

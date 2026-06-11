// -----------------------------------------------------------------------
// <copyright file="SemVer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration.Feeds;

/// <summary>
/// Minimal SemVer 2.0.0 precedence comparison for release version strings
/// (<c>major.minor.patch[-prerelease][+build]</c>). Build metadata is ignored.
///
/// We deliberately roll our own narrow comparator rather than take a
/// <c>NuGet.Versioning</c> dependency: the only inputs are our own well-formed
/// release tags, and the bash release-manifest generator
/// (<c>feeds/scripts/generate-release-manifest.sh</c>) computes
/// <c>latest</c>/<c>latestPrerelease</c> with the exact same precedence rules — the
/// two must agree, so keeping the logic small and co-located avoids drift.
/// </summary>
public static class SemVer
{
    /// <summary>
    /// Returns true if <paramref name="candidate"/> has strictly higher SemVer
    /// precedence than <paramref name="current"/>. Returns false if either string
    /// cannot be parsed — fail safe, so the update check never offers a version it
    /// can't reason about.
    /// </summary>
    public static bool IsNewer(string current, string candidate)
        => TryCompare(current, candidate, out var cmp) && cmp < 0;

    /// <summary>
    /// Compares <paramref name="a"/> and <paramref name="b"/> by SemVer precedence.
    /// On success sets <paramref name="comparison"/> to a value &lt;0, 0, or &gt;0
    /// (the sign of "a relative to b") and returns true. Returns false if either
    /// string is not a parseable version.
    /// </summary>
    public static bool TryCompare(string a, string b, out int comparison)
    {
        comparison = 0;
        if (!TryParse(a, out var pa) || !TryParse(b, out var pb))
            return false;
        comparison = Compare(pa, pb);
        return true;
    }

    private readonly record struct Parsed(int Major, int Minor, int Patch, string[] Pre);

    private static bool TryParse(string version, out Parsed parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(version))
            return false;

        var v = version.Trim();

        // Strip build metadata (everything from the first '+').
        var plus = v.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
            v = v[..plus];

        // Split core from the prerelease label at the first '-'.
        string core;
        string pre;
        var dash = v.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            core = v[..dash];
            pre = v[(dash + 1)..];
            // A '-' must introduce a prerelease label; a trailing '-' is malformed.
            if (pre.Length == 0)
                return false;
        }
        else
        {
            core = v;
            pre = "";
        }

        var coreParts = core.Split('.');
        if (coreParts.Length is < 1 or > 3)
            return false;

        // Accept "0", "0.1", or "0.1.2" — pad omitted components with zero.
        var nums = new int[3];
        for (var i = 0; i < coreParts.Length; i++)
        {
            if (!int.TryParse(coreParts[i], out nums[i]) || nums[i] < 0)
                return false;
        }

        string[] preIds;
        if (pre.Length == 0)
        {
            preIds = [];
        }
        else
        {
            preIds = pre.Split('.');
            // Reject empty identifiers, e.g. "1.0.0-" or "1.0.0-a..b".
            foreach (var id in preIds)
                if (id.Length == 0)
                    return false;
        }

        parsed = new Parsed(nums[0], nums[1], nums[2], preIds);
        return true;
    }

    private static int Compare(Parsed a, Parsed b)
    {
        var c = a.Major.CompareTo(b.Major);
        if (c != 0) return c;
        c = a.Minor.CompareTo(b.Minor);
        if (c != 0) return c;
        c = a.Patch.CompareTo(b.Patch);
        if (c != 0) return c;

        // A version with no prerelease outranks one that has a prerelease with the
        // same core (1.0.0 > 1.0.0-beta.1).
        var aPre = a.Pre.Length > 0;
        var bPre = b.Pre.Length > 0;
        if (!aPre && !bPre) return 0;
        if (!aPre) return 1;
        if (!bPre) return -1;

        // Both prerelease: compare dot-separated identifiers left to right.
        var shared = Math.Min(a.Pre.Length, b.Pre.Length);
        for (var i = 0; i < shared; i++)
        {
            c = ComparePreIdentifier(a.Pre[i], b.Pre[i]);
            if (c != 0) return c;
        }

        // All shared identifiers equal → the longer identifier set has higher
        // precedence (1.0.0-beta.1 < 1.0.0-beta.1.1).
        return a.Pre.Length.CompareTo(b.Pre.Length);
    }

    private static int ComparePreIdentifier(string a, string b)
    {
        // Parse as long (not int): numeric identifiers can be large (timestamps, build
        // counters), and the bash generator compares them with Python's unbounded int —
        // long covers any realistic value and keeps the two implementations in agreement.
        var aNumeric = long.TryParse(a, out var ai);
        var bNumeric = long.TryParse(b, out var bi);

        if (aNumeric && bNumeric) return ai.CompareTo(bi);
        // Numeric identifiers always have lower precedence than alphanumeric ones.
        if (aNumeric) return -1;
        if (bNumeric) return 1;
        return string.CompareOrdinal(a, b);
    }
}

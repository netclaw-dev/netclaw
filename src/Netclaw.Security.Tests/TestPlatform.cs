// -----------------------------------------------------------------------
// <copyright file="TestPlatform.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Security.Tests;

/// <summary>
/// Shared xunit.v3 <c>SkipUnless</c> hook. Tests whose expected output
/// depends on POSIX shell/path semantics — or that exercise code paths only
/// reached on non-Windows (e.g. BashParser routing) — gate on this via
/// <c>[Fact(SkipUnless = nameof(TestPlatform.IsPosix), SkipType = typeof(TestPlatform))]</c>
/// so they record a proper "Skipped" entry on Windows runners.
/// </summary>
public static class TestPlatform
{
    public static bool IsPosix => !OperatingSystem.IsWindows();
}

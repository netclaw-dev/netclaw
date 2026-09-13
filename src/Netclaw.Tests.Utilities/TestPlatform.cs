// -----------------------------------------------------------------------
// <copyright file="TestPlatform.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

namespace Netclaw.Tests.Utilities;

/// <summary>
/// Platform conditions for xUnit conditional skips. Set SkipType to this type
/// so xUnit resolves the property outside the test class.
/// </summary>
public static class TestPlatform
{
    public static bool IsPosix => !OperatingSystem.IsWindows();
    public static bool IsLinux => OperatingSystem.IsLinux();
    public static bool IsWindows => OperatingSystem.IsWindows();
}

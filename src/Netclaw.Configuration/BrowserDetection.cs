// -----------------------------------------------------------------------
// <copyright file="BrowserDetection.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Detects whether a graphical browser can be opened on the current system.
/// Used by OAuth flows (provider and MCP) to decide between auto-opening
/// a browser or showing a URL for manual copy/paste.
/// </summary>
public static class BrowserDetection
{
    /// <summary>
    /// Returns true if the system likely has a graphical browser available.
    /// On Linux, checks for DISPLAY or WAYLAND_DISPLAY environment variables.
    /// Windows and macOS are assumed to always have a browser.
    /// </summary>
    public static bool CanOpenBrowser()
    {
        if (!OperatingSystem.IsLinux())
            return true;

        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
    }
}

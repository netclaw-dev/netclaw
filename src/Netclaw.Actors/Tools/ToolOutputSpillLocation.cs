// -----------------------------------------------------------------------
// <copyright file="ToolOutputSpillLocation.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text;
using Netclaw.Security;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Resolves one opaque tool call id inside one immutable session directory.
/// </summary>
internal static class ToolOutputSpillLocation
{
    internal const int MaximumCallIdLength = 200;
    private const string ToolCallsSubdirectory = "tool-calls";

    public static bool TryResolve(
        string? sessionDirectory,
        string? callId,
        out string directory,
        out string path)
    {
        directory = string.Empty;
        path = string.Empty;

        if (!IsValidSessionDirectory(sessionDirectory) || !IsValidCallId(callId))
            return false;

        try
        {
            directory = Path.GetFullPath(Path.Combine(sessionDirectory!, ToolCallsSubdirectory));
            var fileName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(callId!))) + ".log";
            path = Path.GetFullPath(Path.Combine(directory, fileName));
            return string.Equals(Path.GetDirectoryName(path), directory, PathComparison());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            directory = string.Empty;
            path = string.Empty;
            return false;
        }
    }

    internal static bool IsValidCallId(string? callId)
    {
        if (string.IsNullOrWhiteSpace(callId) || callId.Length > MaximumCallIdLength)
            return false;

        if (callId is "." or "..")
            return false;

        foreach (var value in callId)
        {
            if (char.IsControl(value)
                || char.IsWhiteSpace(value)
                || value is '/' or '\\')
                return false;
        }

        return true;
    }

    private static bool IsValidSessionDirectory(string? sessionDirectory)
    {
        if (string.IsNullOrWhiteSpace(sessionDirectory)
            || sessionDirectory.Any(char.IsControl)
            || !Path.IsPathFullyQualified(sessionDirectory)
            || !Directory.Exists(sessionDirectory))
        {
            return false;
        }

        try
        {
            return string.Equals(
                sessionDirectory,
                Path.GetFullPath(sessionDirectory),
                PathComparison());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static StringComparison PathComparison()
        => OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public static bool IsSafeForIo(string sessionDirectory, string path)
    {
        try
        {
            if ((File.GetAttributes(sessionDirectory) & FileAttributes.ReparsePoint) != 0)
                return false;

            return !PathUtility.ContainsSymlinkSegment(sessionDirectory, path);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or System.Security.SecurityException)
        {
            return false;
        }
    }
}

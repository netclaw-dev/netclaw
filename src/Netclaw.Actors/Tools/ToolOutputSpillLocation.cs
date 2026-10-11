// -----------------------------------------------------------------------
// <copyright file="ToolOutputSpillLocation.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text;
using Netclaw.Security.Authorization.Filesystem;

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

    /// <summary>
    /// Creates the session workspace folder when it does not exist. Returns false
    /// when the path is not a usable session folder or when the path itself is a link.
    /// </summary>
    /// <remarks>
    /// Only the spill writer calls this. <c>tool_output_read</c> never creates a
    /// folder: a missing folder has no retained output. This method checks the
    /// last path segment only, before the creation. The caller checks the folder
    /// and the spill file again with <see cref="IsSafeForIo"/>. No check covers a
    /// link above the session folder; the shell launcher has the same limit.
    /// </remarks>
    public static bool TryEnsureSessionDirectory(string? sessionDirectory)
    {
        if (!IsWellFormedSessionDirectory(sessionDirectory))
            return false;

        // A link reports a target also when the target is missing, so this refuses
        // a dangling link before CreateDirectory can fail on it. Windows keeps file
        // links and directory links apart, so both forms are read.
        if (new DirectoryInfo(sessionDirectory!).LinkTarget is not null
            || new FileInfo(sessionDirectory!).LinkTarget is not null)
        {
            return false;
        }

        Directory.CreateDirectory(sessionDirectory!);
        return IsValidSessionDirectory(sessionDirectory);
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
        => IsWellFormedSessionDirectory(sessionDirectory) && Directory.Exists(sessionDirectory);

    private static bool IsWellFormedSessionDirectory(string? sessionDirectory)
    {
        if (string.IsNullOrWhiteSpace(sessionDirectory)
            || sessionDirectory.Any(char.IsControl)
            || !Path.IsPathFullyQualified(sessionDirectory))
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

    /// <summary>
    /// Returns true when the session directory exists, is not a link, and no link
    /// exists between it and the spill file.
    /// </summary>
    public static bool IsSafeForIo(string sessionDirectory, string path)
        => Path.Exists(sessionDirectory)
           && CanonicalPath.TryCreateHost(sessionDirectory, relativeBase: null, out var session)
           && CanonicalPath.TryCreateHost(path, relativeBase: null, out var spill)
           && FileSystemAuthority.EvaluateMembership(
               spill,
               [new PathBoundary.Folder(session, LinkRule.IncludingRoot)]) is PathDecision.Allowed;
}

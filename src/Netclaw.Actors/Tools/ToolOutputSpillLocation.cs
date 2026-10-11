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
/// Resolves one opaque tool call id inside one immutable session directory. It
/// also owns the one creator of the session workspace folder, shared by the spill
/// writer and the shell launcher.
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
    /// Creates the session workspace folder when it does not exist. Returns null on
    /// success, or a stable error message for a link at the workspace path or at
    /// its parent. It does not catch a creation failure (for example a file at the
    /// path); that exception goes to the caller, which keeps its own failure reason.
    /// </summary>
    /// <remarks>
    /// The spill writer and the shell launcher both call this, so one creator
    /// builds the folder. <c>tool_output_read</c> never creates a folder: a missing
    /// folder has no retained output. The spill writer checks the folder and the
    /// spill file again with <see cref="IsSafeForIo"/>.
    /// </remarks>
    public static string? EnsureSessionWorkspaceDirectory(string? sessionDirectory)
    {
        if (!IsWellFormedSessionDirectory(sessionDirectory))
            return "Error: The session workspace directory path is not valid.";

        // A link reports a target also when the target is missing, so this refuses
        // a dangling link before CreateDirectory can fail on it. Windows keeps file
        // links and directory links apart, so both forms are read.
        if (IsLink(sessionDirectory!))
            return "Error: The session workspace directory is a link.";

        // A link at the parent of the workspace redirects the creation behind
        // the link, outside the sessions root. The parent is the session folder
        // in the version-2 layout and the sessions root in the legacy layout.
        var parent = Path.GetDirectoryName(sessionDirectory!);
        if (parent is not null && IsLink(parent))
            return "Error: The session folder above the workspace directory is a link.";

        Directory.CreateDirectory(sessionDirectory!);
        return IsValidSessionDirectory(sessionDirectory)
            ? null
            : "Error: The session workspace directory was not created.";
    }

    private static bool IsLink(string path)
        => new DirectoryInfo(path).LinkTarget is not null
           || new FileInfo(path).LinkTarget is not null;

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

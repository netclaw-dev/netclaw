// -----------------------------------------------------------------------
// <copyright file="SessionDirectoryHelper.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Protocol;

using Netclaw.Tools;

/// <summary>
/// Shared helper for computing session-scoped directories.
/// Used by <see cref="Sessions.LlmSessionActor"/> and channel pipeline components.
/// </summary>
public static class SessionDirectoryHelper
{
    /// <summary>
    /// Name of the subdirectory under a session directory where inbound
    /// user-uploaded attachments are written by channel adapters.
    /// </summary>
    public const string InboxSubdirectory = "inbox";

    /// <summary>
    /// Name of the subdirectory under a session directory where outbound
    /// DataContent media bytes are persisted.
    /// </summary>
    public const string MediaSubdirectory = "media";

    /// <summary>
    /// Computes the session directory path under the given base directory
    /// (e.g. <c>~/.netclaw/sessions/</c>).
    /// </summary>
    public static string GetSessionDirectory(SessionId sessionId, string basePath)
    {
        var sanitized = SanitizeSessionId(sessionId);
        return Path.Combine(basePath, sanitized);
    }

    /// <summary>Creates and returns the inbox in the resolved session storage layout.</summary>
    /// <param name="storage">The resolved layout.</param>
    /// <returns>The absolute inbox directory.</returns>
    public static string GetOrCreateInboxDirectory(SessionStoragePaths storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        var inboxDir = Path.Combine(storage.SessionDirectory.Value, InboxSubdirectory);
        Directory.CreateDirectory(inboxDir);
        return inboxDir;
    }

    /// <summary>Creates and returns the attachment staging directory in the resolved layout.</summary>
    /// <param name="storage">The resolved layout.</param>
    /// <returns>The absolute staging directory.</returns>
    public static string GetOrCreateAttachmentStagingDirectory(SessionStoragePaths storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        Directory.CreateDirectory(storage.AttachmentStagingDirectory.Value);
        return storage.AttachmentStagingDirectory.Value;
    }

    /// <summary>
    /// Returns true when the given base path resolves under
    /// <see cref="Path.GetTempPath"/>. Used by diagnostics to warn
    /// operators that attachments and session data will not survive a
    /// reboot or <c>tmpfiles</c> cleanup.
    /// </summary>
    public static bool IsUnderTempPath(string basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
            return false;

        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string fullBase;
        try
        {
            fullBase = Path.TrimEndingDirectorySeparator(Path.GetFullPath(basePath));
        }
        catch
        {
            return false;
        }

        return fullBase.Equals(tempRoot, StringComparison.Ordinal)
               || fullBase.StartsWith(tempRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>
    /// Replaces non-alphanumeric characters (except hyphens) with underscores.
    /// Session IDs may contain slashes (e.g. "C123/1234567890.123456").
    /// </summary>
    public static string SanitizeSessionId(SessionId sessionId)
    {
        var value = sessionId.Value;
        Span<char> buf = stackalloc char[value.Length];
        for (var i = 0; i < value.Length; i++)
            buf[i] = char.IsLetterOrDigit(value[i]) || value[i] == '-' ? value[i] : '_';
        return new string(buf);
    }
}

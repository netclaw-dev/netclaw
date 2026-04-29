// -----------------------------------------------------------------------
// <copyright file="SessionDirectoryHelper.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Protocol;

/// <summary>
/// Shared helper for computing session-scoped directories.
/// Used by <see cref="Sessions.LlmSessionActor"/> and channel pipeline components.
/// </summary>
public static class SessionDirectoryHelper
{
    /// <summary>
    /// Name of the hidden root directory under the sessions base path where
    /// attachments are staged before they pass content scanning and are moved
    /// into the agent-visible session inbox.
    /// </summary>
    public const string AttachmentStagingRootSubdirectory = ".attachment-staging";

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

    /// <summary>
    /// Computes and creates the <c>inbox/</c> subdirectory under the
    /// session directory, returning its full path. Channel adapters call
    /// this when writing user-uploaded attachments to disk. The parent
    /// session directory is created if it does not already exist.
    /// </summary>
    public static string GetOrCreateInboxDirectory(SessionId sessionId, string basePath)
    {
        var sessionDir = GetSessionDirectory(sessionId, basePath);
        var inboxDir = Path.Combine(sessionDir, InboxSubdirectory);
        Directory.CreateDirectory(inboxDir);
        return inboxDir;
    }

    /// <summary>
    /// Computes and creates the hidden per-session staging directory used for
    /// streamed attachment downloads before they are accepted into
    /// <c>inbox/</c>. This directory lives outside the session working
    /// directory so rejected files never appear under <c>{session_dir}</c>.
    /// </summary>
    public static string GetOrCreateAttachmentStagingDirectory(SessionId sessionId, string basePath)
    {
        var stagingRoot = Path.Combine(basePath, AttachmentStagingRootSubdirectory);
        var stagingDir = Path.Combine(stagingRoot, SanitizeSessionId(sessionId));
        Directory.CreateDirectory(stagingDir);
        return stagingDir;
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

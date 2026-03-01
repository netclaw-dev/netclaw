namespace Netclaw.Actors.Protocol;

/// <summary>
/// Shared helper for computing session-scoped directories.
/// Used by <see cref="Sessions.LlmSessionActor"/> and channel pipeline components.
/// </summary>
public static class SessionDirectoryHelper
{
    /// <summary>
    /// Computes the session directory path under the OS temp directory.
    /// Prefer the overload that accepts a base path for daemon-mode sessions.
    /// </summary>
    public static string GetSessionDirectory(SessionId sessionId)
    {
        var sanitized = SanitizeSessionId(sessionId.Value);
        return Path.Combine(Path.GetTempPath(), "netclaw-sessions", sanitized);
    }

    /// <summary>
    /// Computes the session directory path under the given base directory
    /// (e.g. <c>~/.netclaw/sessions/</c>).
    /// </summary>
    public static string GetSessionDirectory(SessionId sessionId, string basePath)
    {
        var sanitized = SanitizeSessionId(sessionId.Value);
        return Path.Combine(basePath, sanitized);
    }

    /// <summary>
    /// Replaces non-alphanumeric characters (except hyphens) with underscores.
    /// Session IDs may contain slashes (e.g. "C123/1234567890.123456").
    /// </summary>
    public static string SanitizeSessionId(string sessionId)
    {
        Span<char> buf = stackalloc char[sessionId.Length];
        for (var i = 0; i < sessionId.Length; i++)
            buf[i] = char.IsLetterOrDigit(sessionId[i]) || sessionId[i] == '-' ? sessionId[i] : '_';
        return new string(buf);
    }
}

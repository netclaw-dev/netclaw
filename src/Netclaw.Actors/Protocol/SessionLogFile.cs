// -----------------------------------------------------------------------
// <copyright file="SessionLogFile.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Protocol;

/// <summary>
/// Shared helper for computing and appending to the canonical per-session log file.
/// The file lives outside the agent-visible session working directory so the LLM
/// cannot inspect its own audit trail with file tools.
///
/// Concurrency contract: callers must serialize writes externally. In production
/// the only writer is <c>SessionLogActor</c>, whose mailbox guarantees a single
/// thread per file path. Tests that exercise this directly must observe the same
/// invariant.
///
/// The per-line open/close + retry loop has been replaced by a persistent file
/// handle owned by <see cref="Sessions.SessionLogActor"/> — see #1249.
/// </summary>
public static class SessionLogFile
{
    public const string FileName = "session.log";

    public static string GetLogsDirectory(SessionId sessionId, string sessionLogsBasePath)
    {
        var sanitized = SessionDirectoryHelper.SanitizeSessionId(sessionId);
        return Path.Combine(sessionLogsBasePath, sanitized);
    }

    public static string GetLogPath(SessionId sessionId, string sessionLogsBasePath) =>
        Path.Combine(GetLogsDirectory(sessionId, sessionLogsBasePath), FileName);
}

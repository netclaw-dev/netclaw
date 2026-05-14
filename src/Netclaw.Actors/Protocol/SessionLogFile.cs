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
/// </summary>
public static class SessionLogFile
{
    public const string FileName = "session.log";

    // Bounded retry budget for transient Windows file-sharing conflicts. On NTFS
    // a concurrent reader holding the file with FileShare.Read (e.g. File.ReadAllText*,
    // tail-f tools, Search Indexer, AV scan-on-close) blocks any FileAccess.Write
    // open regardless of the writer's own share mask — share-mode intersection is
    // bidirectional and the reader's mask must permit Write. The kernel/AV hand-off
    // window is typically sub-10ms; 10/20/40/80ms backoff covers the long tail
    // without exceeding an actor's per-message processing budget.
    private const int MaxAttempts = 4;
    private static readonly int[] BackoffMs = [10, 20, 40, 80];

    public static string GetLogsDirectory(SessionId sessionId, string sessionLogsBasePath)
    {
        var sanitized = SessionDirectoryHelper.SanitizeSessionId(sessionId);
        return Path.Combine(sessionLogsBasePath, sanitized);
    }

    public static string GetLogPath(SessionId sessionId, string sessionLogsBasePath) =>
        Path.Combine(GetLogsDirectory(sessionId, sessionLogsBasePath), FileName);

    public static void AppendLine(SessionId sessionId, string sessionLogsBasePath, string line)
    {
        var logPath = GetLogPath(sessionId, sessionLogsBasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                // FileShare.ReadWrite | FileShare.Delete is the canonical log-file mask:
                // it lets concurrent readers (tail, audit consumers, tests) coexist and
                // lets log rotation / Directory.Delete proceed on Windows. The
                // single-writer invariant is enforced by SessionLogActor's mailbox,
                // not by this share mask.
                using var stream = new FileStream(
                    logPath, FileMode.Append, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                using var writer = new StreamWriter(stream) { AutoFlush = true };
                writer.WriteLine(line);
                return;
            }
            catch (Exception ex) when (
                (ex is IOException || ex is UnauthorizedAccessException)
                && attempt < MaxAttempts - 1)
            {
                Thread.Sleep(BackoffMs[attempt]);
            }
        }
    }
}

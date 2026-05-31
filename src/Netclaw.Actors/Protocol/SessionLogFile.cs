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

    // Bounded retry budget for transient Windows file-sharing conflicts. On NTFS a
    // concurrent handle that excludes write (Windows Defender scan-on-close of the
    // just-written file, Search Indexer, a reader opened with FileShare.Read) blocks
    // the next FileMode.Append / FileAccess.Write open — share-mode intersection is
    // mandatory and bidirectional, so the other holder's mask must permit Write.
    // The AV scan-on-close hand-off is usually sub-100ms but spikes under loaded CI,
    // so the schedule below waits ~585ms (plus jitter) before giving up — one retry
    // per backoff entry. Jitter de-correlates retries from a periodic scanner's
    // cadence; the wait still stays within an actor's per-message processing budget.
    private static readonly int[] BackoffMs = [10, 25, 50, 100, 200, 200];

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
                && attempt < BackoffMs.Length)
            {
                // Waiting on an external OS resource (the AV/indexer handle) to be
                // released — the legitimate use of a bounded blocking backoff on the
                // actor's mailbox thread. Jitter spreads retries so a fixed scanner
                // cadence cannot phase-lock with the schedule.
                var baseMs = BackoffMs[attempt];
                Thread.Sleep(baseMs + Random.Shared.Next(baseMs / 2 + 1));
            }
        }
    }
}

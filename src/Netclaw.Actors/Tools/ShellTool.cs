// -----------------------------------------------------------------------
// <copyright file="ShellTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Executes shell commands via /bin/bash (Linux) or cmd.exe (Windows).
/// Captures stdout+stderr, enforces timeout, closes stdin immediately.
/// </summary>
[NetclawTool(ToolName,
    "Execute a shell command and return stdout/stderr output with exit code",
    Grant = "shell")]
public sealed partial class ShellTool : NetclawTool<ShellTool.Params>
{
    public const string ToolName = "shell_execute";

    private readonly ToolConfig _config;
    private readonly ToolPathPolicy? _pathPolicy;
    private readonly ShellCommandPolicy? _commandPolicy;

    public record Params(
        [property: Description("The shell command to execute")] string Command,
        [property: Description("Working directory to run the command in (optional)")] string? WorkingDirectory = null);

    public ShellTool(ToolConfig config, ToolPathPolicy? pathPolicy = null, ShellCommandPolicy? commandPolicy = null)
    {
        _config = config;
        _pathPolicy = pathPolicy;
        _commandPolicy = commandPolicy;
    }

    protected override async Task<string> ExecuteAsync(Params args, CancellationToken ct)
        => await ExecuteAsync(args, ToolExecutionContext.Empty, ct);

    protected override async Task<string> ExecuteAsync(Params args, ToolExecutionContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Command))
            return "Error: 'command' parameter is required.";

        if (_commandPolicy is not null)
        {
            var commandDecision = _commandPolicy.Evaluate(args.Command);
            if (!commandDecision.Allowed)
                return $"Error: Command blocked by hard deny policy: {commandDecision.DenyReason}";
        }

        if (_pathPolicy?.CommandReferencesDeniedPath(args.Command, args.WorkingDirectory) == true)
            return "Error: Command references a protected file path. Access denied by security policy.";

        var isWindows = OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/bash",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (isWindows)
        {
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(args.Command);
        }
        else
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(args.Command);
        }

        // Resolve working directory in priority order: explicit arg →
        // WorkingContext.ProjectDirectory (declared via set_working_directory)
        // → SessionDirectory (per-session scratch). Never falls through to
        // ProcessStartInfo's default of inheriting the daemon process's cwd —
        // that location is wherever the daemon happened to be launched and is
        // unrelated to what the agent is "working on," which makes it
        // impossible for the approval policy to reason about safe-space
        // membership. The matcher reads context.Cwd against the same
        // resolution chain so the gate evaluates folder-scoped ApprovalEntry
        // records against the directory the spawned process will run in.
        var resolvedCwd = context.ResolveShellCwd(args.WorkingDirectory);
        if (!string.IsNullOrWhiteSpace(resolvedCwd))
        {
            if (IsResolvedSessionDirectory(resolvedCwd, context.SessionDirectory))
            {
                try
                {
                    Directory.CreateDirectory(resolvedCwd);
                }
                catch (Exception ex) when (ex is ArgumentException
                                           or IOException
                                           or NotSupportedException
                                           or UnauthorizedAccessException
                                           or System.Security.SecurityException)
                {
                    return $"Error preparing session working directory: {ex.Message}";
                }
            }

            psi.WorkingDirectory = resolvedCwd;
        }

        var effectiveTimeoutSeconds = context.RequestedTimeoutSeconds is > 0
            ? context.RequestedTimeoutSeconds.Value
            : _config.ShellTimeoutSeconds;

        using var timeoutCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (Exception ex)
        {
            return $"Error starting process: {ex.Message}";
        }

        // Start the timeout countdown only after the shell process exists, so
        // process-spawn overhead (heavier on Windows: cmd.exe plus the child it
        // execs) is not charged against the command's execution budget.
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(effectiveTimeoutSeconds));

        using (process)
        {
            process.StandardInput.Close();

            // Start draining both pipes up front: a chatty child can deadlock if
            // one pipe buffer fills while we wait on the other. The reads take
            // CancellationToken.None deliberately — a redirected child holds the
            // pipe write-ends open, so a blocked pipe read cannot be interrupted
            // by a token; killing the process is what closes the pipes.
            //
            // BoundedDrainAsync reads into a head+tail window bounded by
            // MaxOutputChars but continues draining after the cap is reached so
            // the pipe never fills up and deadlocks a still-running child.
            var stdoutTask = BoundedDrainAsync(process.StandardOutput, _config.MaxOutputChars);
            var stderrTask = BoundedDrainAsync(process.StandardError, _config.MaxOutputChars);

            try
            {
                // WaitForExitAsync waits on the process-exit event, not a pipe
                // read, so it honors the token — and a process that exits cleanly
                // completes it normally even if the deadline trips a moment later,
                // so a finished command is never mislabelled as a timeout.
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                var killClosedPipes = true;

                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException ex)
                {
                    // Already exited between cancellation detection and kill.
                    Debug.WriteLine($"shell_execute: process kill skipped — {ex.Message}");
                }
                catch (Win32Exception ex)
                {
                    // If the OS refuses the kill, the child may keep stdout/stderr
                    // open forever. Close our read ends so cancellation still
                    // returns promptly instead of hanging in BoundedDrainAsync.
                    killClosedPipes = false;
                    Debug.WriteLine($"shell_execute: process kill skipped — {ex.Message}");
                    process.StandardOutput.Dispose();
                    process.StandardError.Dispose();
                }

                try
                {
                    await Task.WhenAll(stdoutTask, stderrTask);
                }
                catch (Exception ex) when (!killClosedPipes && ex is IOException or ObjectDisposedException)
                {
                    Debug.WriteLine($"shell_execute: pipe drain aborted — {ex.Message}");
                }

                return timeoutCts.IsCancellationRequested
                    ? $"Error: Command timed out after {effectiveTimeoutSeconds} seconds."
                    : "Error: Command cancelled.";
            }

            var (stdoutText, stdoutTruncated) = await stdoutTask;
            var (stderrText, stderrTruncated) = await stderrTask;

            // Redact secrets on the already-bounded strings, then assemble.
            var stdout = SecretOutputRedactor.Redact(stdoutText);
            var stderr = SecretOutputRedactor.Redact(stderrText);

            var result = new StringBuilder();
            if (stdout.Length > 0)
            {
                result.Append(stdout);
                if (stdoutTruncated)
                    result.Append($"\n[stdout truncated — output exceeded {_config.MaxOutputChars} chars; head and tail shown]");
            }
            if (stderr.Length > 0)
            {
                if (result.Length > 0)
                    result.AppendLine();
                result.Append(stderr);
                if (stderrTruncated)
                    result.Append($"\n[stderr truncated — output exceeded {_config.MaxOutputChars} chars; head and tail shown]");
            }

            return $"Exit code: {process.ExitCode}{Environment.NewLine}{result}";
        }
    }

    /// <summary>
    /// Drains <paramref name="reader"/> into a head+tail window bounded by
    /// <paramref name="maxChars"/>. Bytes beyond the cap are discarded but the
    /// pipe continues to be read so a still-running child never deadlocks on a
    /// full pipe buffer. Returns the captured text and whether it was truncated.
    /// </summary>
    internal static async Task<(string Text, bool Truncated)> BoundedDrainAsync(
        TextReader reader, int maxChars)
    {
        if (maxChars <= 0)
        {
            // Cap disabled: fall back to unbounded read (matches previous behaviour
            // when MaxOutputChars is set to 0 to opt out of truncation).
            var all = await reader.ReadToEndAsync(CancellationToken.None);
            return (all, false);
        }

        // Split the budget: first half for the head, second half for the tail.
        // Odd maxChars gives the extra char to the head.
        var headCap = (maxChars + 1) / 2;
        var tailCap = maxChars / 2;

        var head = new StringBuilder(Math.Min(headCap, 4096));
        // Ring buffer for the tail window: we keep overwriting once the tail is full.
        var tailBuf = tailCap > 0 ? new char[tailCap] : [];
        var tailPos = 0;    // next write position in the ring
        var tailLen = 0;    // chars actually written (< tailCap until ring is full)
        var totalChars = 0; // total chars seen across all reads
        var buf = new char[4096];

        int read;
        while ((read = await reader.ReadAsync(buf, 0, buf.Length)) > 0)
        {
            totalChars += read;
            var span = buf.AsSpan(0, read);

            if (head.Length < headCap)
            {
                var headChunk = Math.Min(headCap - head.Length, span.Length);
                head.Append(span[..headChunk]);
                span = span[headChunk..];
            }

            // Feed overflow chars into the tail ring buffer, discarding nothing
            // except the overwritten oldest entry — the pipe keeps draining.
            if (tailCap > 0)
            {
                foreach (var ch in span)
                {
                    tailBuf[tailPos] = ch;
                    tailPos = (tailPos + 1) % tailCap;
                    if (tailLen < tailCap) tailLen++;
                }
            }
        }

        // Truncation only when total chars exceeded the full budget (head+tail),
        // meaning some middle chars were discarded.
        var truncated = totalChars > maxChars;

        // Reconstruct the tail in order from the ring buffer.
        var tail = new StringBuilder(tailLen);
        if (tailLen > 0)
        {
            var start = tailLen < tailCap ? 0 : tailPos; // oldest char position
            for (var i = 0; i < tailLen; i++)
                tail.Append(tailBuf[(start + i) % tailCap]);
        }

        if (!truncated)
        {
            // Nothing was discarded: head + tail together is the full output.
            head.Append(tail);
            return (head.ToString(), false);
        }

        return (head + "\n...\n" + tail, true);
    }

    // Retained for compatibility with tests that call it directly; the main
    // execution path no longer uses this — BoundedDrainAsync caps at read time.
    internal static string TruncateOutput(string output, int maxChars)
    {
        if (output.Length <= maxChars)
            return output;

        return string.Concat(output.AsSpan(0, maxChars), $"{Environment.NewLine}... [output truncated]");
    }

    private static bool IsResolvedSessionDirectory(string resolvedCwd, string? sessionDirectory)
        => !string.IsNullOrWhiteSpace(sessionDirectory)
           && PathUtility.AreEquivalentPaths(resolvedCwd, sessionDirectory);
}

// -----------------------------------------------------------------------
// <copyright file="ShellTool.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
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

    // The required execution context carries the session default or the
    // agent's validated _timeout_seconds hint as a semantic value.

    // Shell output is mostly verbose noise the model skims, so bound it
    // aggressively: small inline head+tail, full output spilled to a session file
    // to grep. Content tools (file_read, web_fetch, MCP) keep the larger session
    // content budget because the model fetched them to read in full.
    public override int InlineOutputBudgetChars => 2000;

    private readonly ToolConfig _config;
    private readonly ToolPathPolicy _pathPolicy;
    private readonly ShellCommandPolicy _commandPolicy;

    public record Params(
        [property: Description("The shell command to execute")] string Command,
        [property: Description("Working directory to run the command in (optional)")] string? WorkingDirectory = null);

    public ShellTool(ToolConfig config, ToolPathPolicy pathPolicy, ShellCommandPolicy commandPolicy)
    {
        _config = config;
        _pathPolicy = pathPolicy;
        _commandPolicy = commandPolicy;
    }

    protected override async Task<string> ExecuteAsync(Params args, ToolInvocationContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Command))
            return "Error: 'command' parameter is required.";

        var commandDecision = _commandPolicy.Evaluate(args.Command);
        if (!commandDecision.Allowed)
            return $"Error: Command blocked by hard deny policy: {commandDecision.DenyReason}";

        if (_pathPolicy.CommandReferencesDeniedPath(args.Command, args.WorkingDirectory))
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
            else if (!Directory.Exists(resolvedCwd))
            {
                // ProcessStartInfo.WorkingDirectory must point at an existing directory or
                // Process.Start throws an opaque, platform-specific error. Only the session
                // scratch dir is auto-created (above); every other resolved cwd — explicit
                // arg, project dir, inherited cwd — must already exist. Fail loudly with the
                // remedy so the agent creates it instead of retry-looping on a cryptic error.
                // Any approval for this cwd is existence-agnostic, so it still matches once
                // the agent runs the mkdir.
                if (File.Exists(resolvedCwd))
                    return $"Error: Working directory '{resolvedCwd}' is a file, not a directory.";

                var mkdirHint = isWindows ? $"mkdir \"{resolvedCwd}\"" : $"mkdir -p \"{resolvedCwd}\"";
                return $"Error: Working directory '{resolvedCwd}' does not exist. "
                     + $"Create it first, e.g.: {mkdirHint}";
            }

            psi.WorkingDirectory = resolvedCwd;
        }

        var effectiveTimeout = context.ExecutionTimeout.Value;

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
        timeoutCts.CancelAfter(effectiveTimeout);

        using (process)
        {
            process.StandardInput.Close();

            // Start draining both pipes up front: a chatty child can deadlock if
            // one pipe buffer fills while we wait on the other. The reads take
            // CancellationToken.None deliberately — a redirected child holds the
            // pipe write-ends open, so a blocked pipe read cannot be interrupted
            // by a token; killing the process is what closes the pipes.
            //
            // BoundedOutputReader reads into a head+tail window bounded by
            // MaxOutputChars but continues draining after the cap is reached so
            // the pipe never fills up and deadlocks a still-running child.
            var stdoutTask = BoundedOutputReader.DrainToWindowAsync(process.StandardOutput, _config.MaxOutputChars, CancellationToken.None);
            var stderrTask = BoundedOutputReader.DrainToWindowAsync(process.StandardError, _config.MaxOutputChars, CancellationToken.None);

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
                    // returns promptly instead of hanging in the pipe drain.
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
                    ? $"Error: Command timed out after {effectiveTimeout.TotalSeconds:F0} seconds."
                    : "Error: Command cancelled.";
            }

            var (stdoutText, _) = await stdoutTask;
            var (stderrText, _) = await stderrTask;

            // Assemble the raw combined output (stdout then stderr). Each stream was
            // drained to MaxOutputChars, so the concatenation can be up to 2x — re-window
            // the COMBINED back to the capture ceiling so the spill body stays bounded by
            // MaxOutputChars. Redaction and the inline-budget bound + spill+steer happen
            // centrally in DispatchingToolExecutor; the tool only returns its bounded
            // capture.
            var combined = new StringBuilder();
            if (stdoutText.Length > 0)
                combined.Append(stdoutText);
            if (stderrText.Length > 0)
            {
                if (combined.Length > 0)
                    combined.AppendLine();
                combined.Append(stderrText);
            }

            var captured = BoundedOutputReader.Window(combined.ToString(), _config.MaxOutputChars);
            return $"Exit code: {process.ExitCode}{Environment.NewLine}{captured}";
        }
    }

    /// <summary>
    /// Streams stdout/stderr as <see cref="ToolActivityUpdate"/> items while the
    /// process runs. Shell output is live display data only; the parent pipeline
    /// still treats shell as opaque and enforces a wall-clock budget. The terminal
    /// <see cref="ToolCompletedUpdate"/> carries the same bounded head+tail result
    /// as the non-streaming path.
    /// </summary>
    public override async IAsyncEnumerable<ToolCallUpdate> ExecuteStreamAsync(
        IDictionary<string, object?>? arguments,
        ToolInvocationContext context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // All items (activities + completion) are produced by the non-iterator
        // helper and written into a channel. The iterator just relays them.
        // This avoids the C# restriction against yield inside try/catch.
        // The channel read does NOT pass ct: the core method handles cancellation
        // internally and writes the error completion before completing the channel.
        var channel = Channel.CreateUnbounded<ToolCallUpdate>(
            new UnboundedChannelOptions { SingleReader = true });
        _ = ExecuteStreamCoreAsync(arguments, context, channel.Writer, ct);

        await foreach (var update in channel.Reader.ReadAllAsync(CancellationToken.None))
            yield return update;
    }

    private async Task ExecuteStreamCoreAsync(
        IDictionary<string, object?>? arguments,
        ToolInvocationContext context,
        ChannelWriter<ToolCallUpdate> output,
        CancellationToken ct)
    {
        try
        {
            if (!TryParse(arguments, out var error, out var args))
            {
                output.TryWrite(new ToolCompletedUpdate(error));
                return;
            }

            if (string.IsNullOrWhiteSpace(args.Command))
            {
                output.TryWrite(new ToolCompletedUpdate("Error: 'command' parameter is required."));
                return;
            }

            var commandDecision = _commandPolicy.Evaluate(args.Command);
            if (!commandDecision.Allowed)
            {
                output.TryWrite(new ToolCompletedUpdate(
                    $"Error: Command blocked by hard deny policy: {commandDecision.DenyReason}"));
                return;
            }

            if (_pathPolicy.CommandReferencesDeniedPath(args.Command, args.WorkingDirectory))
            {
                output.TryWrite(new ToolCompletedUpdate(
                    "Error: Command references a protected file path. Access denied by security policy."));
                return;
            }

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
                        output.TryWrite(new ToolCompletedUpdate(
                            $"Error preparing session working directory: {ex.Message}"));
                        return;
                    }
                }
                else if (!Directory.Exists(resolvedCwd))
                {
                    if (File.Exists(resolvedCwd))
                    {
                        output.TryWrite(new ToolCompletedUpdate(
                            $"Error: Working directory '{resolvedCwd}' is a file, not a directory."));
                        return;
                    }

                    var mkdirHint = isWindows ? $"mkdir \"{resolvedCwd}\"" : $"mkdir -p \"{resolvedCwd}\"";
                    output.TryWrite(new ToolCompletedUpdate(
                        $"Error: Working directory '{resolvedCwd}' does not exist. "
                        + $"Create it first, e.g.: {mkdirHint}"));
                    return;
                }

                psi.WorkingDirectory = resolvedCwd;
            }

            Process process;
            try
            {
                process = Process.Start(psi)!;
            }
            catch (Exception ex)
            {
                output.TryWrite(new ToolCompletedUpdate($"Error starting process: {ex.Message}"));
                return;
            }

            var effectiveTimeout = context.ExecutionTimeout.Value;

            // Wall-clock ceiling matching the non-streaming path. The parent
            // pipeline also bounds opaque shell calls by wall-clock time, but the
            // tool keeps its own process-level cap so direct callers and cleanup
            // semantics stay consistent.
            using var wallClockCts = new CancellationTokenSource();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, wallClockCts.Token);
            wallClockCts.CancelAfter(effectiveTimeout);

            var activityChannel = Channel.CreateUnbounded<ToolActivityUpdate>(
                new UnboundedChannelOptions { SingleReader = true });
            var stdoutAcc = new BoundedOutputAccumulator(_config.MaxOutputChars);
            var stderrAcc = new BoundedOutputAccumulator(_config.MaxOutputChars);

            using (process)
            {
                process.StandardInput.Close();

                var drainStdout = DrainPipeToChannelAsync(
                    process.StandardOutput, stdoutAcc, activityChannel.Writer, "stdout");
                var drainStderr = DrainPipeToChannelAsync(
                    process.StandardError, stderrAcc, activityChannel.Writer, "stderr");

                _ = Task.WhenAll(drainStdout, drainStderr)
                    .ContinueWith(
                        _ => activityChannel.Writer.TryComplete(),
                        TaskContinuationOptions.ExecuteSynchronously);

                try
                {
                    await foreach (var activity in activityChannel.Reader.ReadAllAsync(linkedCts.Token))
                        output.TryWrite(activity);

                    await process.WaitForExitAsync(linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    // If the process already exited (pipes closed, output fully
                    // accumulated) but a token fired in the narrow gap before
                    // WaitForExitAsync returned, fall through to assemble the
                    // valid accumulated output instead of discarding it.
                    if (!process.HasExited)
                    {
                        await KillAndDrainAsync(process, drainStdout, drainStderr);
                        output.TryWrite(new ToolCompletedUpdate(
                            $"Error: Command timed out after {effectiveTimeout.TotalSeconds:F0} seconds."));
                        return;
                    }
                }

                try { await Task.WhenAll(drainStdout, drainStderr); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    Debug.WriteLine($"shell_execute: pipe drain aborted — {ex.Message}");
                }

                var (stdoutText, _) = stdoutAcc.Finish();
                var (stderrText, _) = stderrAcc.Finish();

                var combined = new StringBuilder();
                if (stdoutText.Length > 0)
                    combined.Append(stdoutText);
                if (stderrText.Length > 0)
                {
                    if (combined.Length > 0)
                        combined.AppendLine();
                    combined.Append(stderrText);
                }

                var captured = BoundedOutputReader.Window(combined.ToString(), _config.MaxOutputChars);
                output.TryWrite(new ToolCompletedUpdate(
                    $"Exit code: {process.ExitCode}{Environment.NewLine}{captured}"));
            }
        }
        catch (Exception ex)
        {
            // Catch-all so an unexpected exception (e.g. from Window() or
            // StringBuilder) surfaces as a tool-result error rather than
            // silently faulting the fire-and-forget task and leaving the
            // stream without a completion item.
            output.TryWrite(new ToolCompletedUpdate($"Error: {ex.Message}"));
        }
        finally
        {
            output.TryComplete();
        }
    }

    private static readonly TimeSpan CoalesceInterval = TimeSpan.FromMilliseconds(500);

    private static async Task DrainPipeToChannelAsync(
        TextReader pipe,
        BoundedOutputAccumulator accumulator,
        ChannelWriter<ToolActivityUpdate> channel,
        string phase)
    {
        var buf = ArrayPool<char>.Shared.Rent(4096);
        var coalesced = new StringBuilder(4096);
        var lastFlush = Stopwatch.GetTimestamp();
        try
        {
            int read;
            while ((read = await pipe.ReadAsync(buf.AsMemory(), CancellationToken.None)) > 0)
            {
                var span = buf.AsSpan(0, read);
                accumulator.Append(span);
                coalesced.Append(span);

                if (Stopwatch.GetElapsedTime(lastFlush) >= CoalesceInterval)
                {
                    channel.TryWrite(new ToolActivityUpdate(phase, coalesced.ToString()));
                    coalesced.Clear();
                    lastFlush = Stopwatch.GetTimestamp();
                }
            }

            if (coalesced.Length > 0)
                channel.TryWrite(new ToolActivityUpdate(phase, coalesced.ToString()));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buf, clearArray: true);
        }
    }

    private static async Task KillAndDrainAsync(Process process, Task drainStdout, Task drainStderr)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException ex)
        {
            Debug.WriteLine($"shell_execute: process kill skipped — {ex.Message}");
        }
        catch (Win32Exception)
        {
            process.StandardOutput.Dispose();
            process.StandardError.Dispose();
        }

        try { await Task.WhenAll(drainStdout, drainStderr); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Debug.WriteLine($"shell_execute: pipe drain aborted — {ex.Message}");
        }
    }

    // Retained for compatibility with tests/benchmark that call it directly; the
    // main execution path no longer uses this — output is bounded at read time by
    // BoundedOutputReader.
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

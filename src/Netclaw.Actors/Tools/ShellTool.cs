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
/// Executes commands through the daemon's resolved native shell environment.
/// Captures stdout+stderr, enforces timeout, closes stdin immediately.
/// </summary>
[NetclawTool(ToolName,
    "Execute operations requiring shell semantics. For one-call work in a named directory, set WorkingDirectory. Program-specific directory options do not replace it. Do not use for known file reads, directory listings, or edits unless shell behavior is requested.",
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
    private readonly ShellExecutionEnvironment _environment;

    public record Params(
        [param: Description(
            "The shell operation only. For one-call work in a named directory, set WorkingDirectory. Example: use Command='inspect' and WorkingDirectory='/repo/child'.")]
        string Command,
        [param: Description(
            "Run the operation in this directory. Always use it for one-call work in a named child directory or worktree. Example: use Command='inspect' and WorkingDirectory='/repo/child'. Omit it to use the session project or scratch directory.")]
        string? WorkingDirectory = null);

    public ShellTool(ToolConfig config, ToolPathPolicy pathPolicy, ShellCommandPolicy commandPolicy)
    {
        _config = config;
        _pathPolicy = pathPolicy;
        _commandPolicy = commandPolicy;
        if (!ReferenceEquals(pathPolicy.Environment, commandPolicy.Environment))
        {
            throw new ArgumentException(
                "Shell command and path policies must use the same shell environment.",
                nameof(commandPolicy));
        }

        _environment = commandPolicy.Environment;
    }

    protected override Task<string> ExecuteAsync(
        Params args,
        ToolInvocationContext context,
        CancellationToken ct)
        => ExecuteCoreAsync(args, context, authorizedAnalysis: null, ct);

    internal async Task<string> ExecuteAuthorizedAsync(
        IDictionary<string, object?>? arguments,
        ToolInvocationContext context,
        ShellCommandAnalysis analysis,
        CancellationToken ct)
    {
        if (!TryParse(arguments, out var error, out var args))
            return error;

        return await ExecuteCoreAsync(args, context, analysis, ct);
    }

    private async Task<string> ExecuteCoreAsync(
        Params args,
        ToolInvocationContext context,
        ShellCommandAnalysis? authorizedAnalysis,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Command))
            return "Error: 'command' parameter is required.";

        // Resolve once before parsing or execution. The same cwd and parse
        // facts feed both security policies and the launched process.
        var resolvedCwd = context.ResolveShellCwd(args.WorkingDirectory);
        var analysis = ResolveAnalysis(args.Command, resolvedCwd, authorizedAnalysis);
        var commandDecision = _commandPolicy.Evaluate(analysis);
        if (!commandDecision.Allowed)
            return $"Error: Command blocked by hard deny policy: {commandDecision.DenyReason}";

        if (_pathPolicy.CommandReferencesDeniedPath(analysis))
            return "Error: Command references a protected file path. Access denied by security policy.";

        var psi = _environment.CreateProcessStartInfo(args.Command);

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
        var workingDirectoryError = PrepareWorkingDirectory(
            psi,
            resolvedCwd,
            context.SessionDirectory);
        if (workingDirectoryError is not null)
            return workingDirectoryError;

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
            return FormatStartError(ex);
        }

        // Start the timeout countdown only after the shell process exists, so
        // process-spawn overhead is not charged against the command's execution budget.
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
    public override IAsyncEnumerable<ToolCallUpdate> ExecuteStreamAsync(
        IDictionary<string, object?>? arguments,
        ToolInvocationContext context,
        CancellationToken ct = default)
        => ExecuteStreamWithAnalysisAsync(
            arguments,
            context,
            authorizedAnalysis: null,
            ct);

    internal IAsyncEnumerable<ToolCallUpdate> ExecuteAuthorizedStreamAsync(
        IDictionary<string, object?>? arguments,
        ToolInvocationContext context,
        ShellCommandAnalysis analysis,
        CancellationToken ct)
        => ExecuteStreamWithAnalysisAsync(arguments, context, analysis, ct);

    private async IAsyncEnumerable<ToolCallUpdate> ExecuteStreamWithAnalysisAsync(
        IDictionary<string, object?>? arguments,
        ToolInvocationContext context,
        ShellCommandAnalysis? authorizedAnalysis,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // All items (activities + completion) are produced by the non-iterator
        // helper and written into a channel. The iterator just relays them.
        // This avoids the C# restriction against yield inside try/catch.
        // The channel read does NOT pass ct: the core method handles cancellation
        // internally and writes the error completion before completing the channel.
        var channel = Channel.CreateUnbounded<ToolCallUpdate>(
            new UnboundedChannelOptions { SingleReader = true });
        _ = ExecuteStreamCoreAsync(
            arguments,
            context,
            authorizedAnalysis,
            channel.Writer,
            ct);

        await foreach (var update in channel.Reader.ReadAllAsync(CancellationToken.None))
            yield return update;
    }

    private async Task ExecuteStreamCoreAsync(
        IDictionary<string, object?>? arguments,
        ToolInvocationContext context,
        ShellCommandAnalysis? authorizedAnalysis,
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

            var resolvedCwd = context.ResolveShellCwd(args.WorkingDirectory);
            var analysis = ResolveAnalysis(args.Command, resolvedCwd, authorizedAnalysis);
            var commandDecision = _commandPolicy.Evaluate(analysis);
            if (!commandDecision.Allowed)
            {
                output.TryWrite(new ToolCompletedUpdate(
                    $"Error: Command blocked by hard deny policy: {commandDecision.DenyReason}"));
                return;
            }

            if (_pathPolicy.CommandReferencesDeniedPath(analysis))
            {
                output.TryWrite(new ToolCompletedUpdate(
                    "Error: Command references a protected file path. Access denied by security policy."));
                return;
            }

            var psi = _environment.CreateProcessStartInfo(args.Command);
            var workingDirectoryError = PrepareWorkingDirectory(
                psi,
                resolvedCwd,
                context.SessionDirectory);
            if (workingDirectoryError is not null)
            {
                output.TryWrite(new ToolCompletedUpdate(workingDirectoryError));
                return;
            }

            Process process;
            try
            {
                process = Process.Start(psi)!;
            }
            catch (Exception ex)
            {
                output.TryWrite(new ToolCompletedUpdate(FormatStartError(ex)));
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

    private ShellCommandAnalysis ResolveAnalysis(
        string command,
        string? resolvedCwd,
        ShellCommandAnalysis? authorizedAnalysis)
    {
        if (authorizedAnalysis is null)
            return _commandPolicy.Analyze(command, resolvedCwd);

        if (!string.Equals(authorizedAnalysis.Source, command, StringComparison.Ordinal)
            || !string.Equals(
                authorizedAnalysis.WorkingDirectory,
                resolvedCwd,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The authorized shell analysis does not match the executed command.");
        }

        return authorizedAnalysis;
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

    private string? PrepareWorkingDirectory(
        ProcessStartInfo startInfo,
        string? resolvedCwd,
        string? sessionDirectory)
    {
        if (string.IsNullOrWhiteSpace(resolvedCwd))
            return null;

        if (IsResolvedSessionDirectory(resolvedCwd, sessionDirectory))
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
            if (File.Exists(resolvedCwd))
                return $"Error: Working directory '{resolvedCwd}' is a file, not a directory.";

            return $"Error: Working directory '{resolvedCwd}' does not exist. "
                   + $"Create it first, e.g.: {CreateDirectoryHint(resolvedCwd)}";
        }

        startInfo.WorkingDirectory = resolvedCwd;
        return null;
    }

    private string CreateDirectoryHint(string path)
        => _environment.PathStyle == ShellPathStyle.Windows
            ? $"New-Item -ItemType Directory -Force -Path '{path.Replace("'", "''", StringComparison.Ordinal)}'"
            : $"mkdir -p -- '{path.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    private string FormatStartError(Exception exception)
        => $"Error starting shell '{_environment.ExecutableName}' "
           + $"at '{_environment.ExecutablePath}': {exception.Message}";

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

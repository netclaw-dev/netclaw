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
            psi.WorkingDirectory = resolvedCwd;

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

            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            // Start draining both pipes up front: a chatty child can deadlock if
            // one pipe buffer fills while we wait on the other. The reads take
            // CancellationToken.None deliberately — a redirected child holds the
            // pipe write-ends open, so a blocked pipe read cannot be interrupted
            // by a token; killing the process is what closes the pipes.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

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
                    // returns promptly instead of hanging in ReadToEndAsync.
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

            outputBuilder.Append(await stdoutTask);
            errorBuilder.Append(await stderrTask);

            var result = new StringBuilder();
            if (outputBuilder.Length > 0)
                result.Append(outputBuilder);
            if (errorBuilder.Length > 0)
            {
                if (result.Length > 0)
                    result.AppendLine();
                result.Append(errorBuilder);
            }

            var sanitized = SecretOutputRedactor.Redact(result.ToString());
            var output = TruncateOutput(sanitized, _config.MaxOutputChars);
            return $"Exit code: {process.ExitCode}{Environment.NewLine}{output}";
        }
    }

    internal static string TruncateOutput(string output, int maxChars)
    {
        if (output.Length <= maxChars)
            return output;

        return string.Concat(output.AsSpan(0, maxChars), $"{Environment.NewLine}... [output truncated]");
    }
}

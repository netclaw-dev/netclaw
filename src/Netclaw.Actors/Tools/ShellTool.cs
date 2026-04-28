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

        if (!string.IsNullOrWhiteSpace(args.WorkingDirectory))
            psi.WorkingDirectory = args.WorkingDirectory;

        var effectiveTimeoutSeconds = context.RequestedTimeoutSeconds is > 0
            ? context.RequestedTimeoutSeconds.Value
            : _config.ShellTimeoutSeconds;

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(effectiveTimeoutSeconds));
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

        using (process)
        {
            process.StandardInput.Close();

            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            try
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
                var stderrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);

                outputBuilder.Append(await stdoutTask);
                errorBuilder.Append(await stderrTask);

                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Process already exited between timeout detection and kill — expected TOCTOU race
                    System.Diagnostics.Debug.WriteLine("Process already exited during timeout cleanup");
                }
                return $"Error: Command timed out after {effectiveTimeoutSeconds} seconds.";
            }

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

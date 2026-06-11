// -----------------------------------------------------------------------
// <copyright file="BackgroundJobExecutionActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Text;
using Akka.Actor;
using Akka.Event;
using Netclaw.Actors.Tools;
using Netclaw.Security;

namespace Netclaw.Actors.Jobs;

/// <summary>
/// Child actor of <see cref="BackgroundJobManagerActor"/> that spawns a process,
/// captures output to disk, and reports completion to its parent.
/// </summary>
public sealed class BackgroundJobExecutionActor : ReceiveActor
{
    private readonly BackgroundJobDefinition _definition;
    private readonly string _outputLogPath;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggingAdapter _log;
    private Process? _process;
    private ICancelable? _timeoutHandle;

    public BackgroundJobExecutionActor(
        BackgroundJobDefinition definition,
        string outputLogPath,
        TimeProvider timeProvider)
    {
        _definition = definition;
        _outputLogPath = outputLogPath;
        _timeProvider = timeProvider;
        _log = Context.GetLogger();

        Receive<CancelBackgroundJob>(_ => HandleCancel());
        Receive<TimeoutTick>(_ => HandleTimeout());
        Receive<ProcessExited>(HandleProcessExited);
    }

    protected override void PreStart()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_outputLogPath)!);
            SpawnProcess();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to start background job {JobId}", _definition.Id);
            ReportCompletion(BackgroundJobStatus.Failed, -1, $"Failed to start: {ex.Message}");
        }
    }

    protected override void PostStop()
    {
        _timeoutHandle?.Cancel();
        KillProcess();
    }

    private void SpawnProcess()
    {
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
            psi.ArgumentList.Add(_definition.Command);
        }
        else
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(_definition.Command);
        }

        if (!string.IsNullOrWhiteSpace(_definition.WorkingDirectory))
        {
            // ProcessStartInfo.WorkingDirectory must point at an existing directory or
            // Process.Start throws an opaque, platform-specific error that surfaces as a
            // cryptic "Failed to start: ...". Report the missing directory with the mkdir
            // remedy so the agent creates it instead of retry-looping on the opaque error.
            if (!Directory.Exists(_definition.WorkingDirectory))
            {
                if (File.Exists(_definition.WorkingDirectory))
                {
                    ReportCompletion(BackgroundJobStatus.Failed, -1,
                        $"Working directory '{_definition.WorkingDirectory}' is a file, not a directory.");
                    return;
                }

                var mkdirHint = isWindows
                    ? $"mkdir \"{_definition.WorkingDirectory}\""
                    : $"mkdir -p \"{_definition.WorkingDirectory}\"";
                ReportCompletion(BackgroundJobStatus.Failed, -1,
                    $"Working directory '{_definition.WorkingDirectory}' does not exist. "
                    + $"Create it first, e.g.: {mkdirHint}");
                return;
            }

            psi.WorkingDirectory = _definition.WorkingDirectory;
        }

        _process = Process.Start(psi);
        if (_process is null)
        {
            ReportCompletion(BackgroundJobStatus.Failed, -1, "Process.Start returned null");
            return;
        }

        _process.StandardInput.Close();

        _log.Info("Background job {JobId} started PID {Pid}: {Command}",
            _definition.Id, _process.Id, _definition.Command);

        if (_definition.TimeoutSeconds > 0)
        {
            _timeoutHandle = Context.System.Scheduler.ScheduleTellOnceCancelable(
                TimeSpan.FromSeconds(_definition.TimeoutSeconds),
                Self, TimeoutTick.Instance, ActorRefs.NoSender);
        }

        var self = Self;
        var process = _process;
        Task.Run(async () =>
        {
            var outputBuilder = new StringBuilder();
            try
            {
                // Bounded capture (not ReadToEndAsync): a chatty long-running job —
                // exactly what background jobs exist for — must not buffer its full
                // output in memory. Drain each stream to the capture ceiling (head+tail
                // for floods larger than it); the source keeps draining past the cap so
                // the child never deadlocks on a full pipe. Closes #1300.
                var stdoutTask = BoundedOutputReader.DrainToWindowAsync(
                    process.StandardOutput, BackgroundJobManagerActor.MaxCapturedOutputChars, CancellationToken.None);
                var stderrTask = BoundedOutputReader.DrainToWindowAsync(
                    process.StandardError, BackgroundJobManagerActor.MaxCapturedOutputChars, CancellationToken.None);

                var (stdout, stdoutTruncated) = await stdoutTask;
                var (stderr, stderrTruncated) = await stderrTask;
                await process.WaitForExitAsync();

                outputBuilder.Append(stdout);
                if (!string.IsNullOrEmpty(stderr))
                {
                    outputBuilder.AppendLine();
                    outputBuilder.Append("STDERR:\n");
                    outputBuilder.Append(stderr);
                }

                // Mark a flood that exceeded the capture ceiling so the log isn't a
                // silent head+tail splice presented as complete.
                if (stdoutTruncated || stderrTruncated)
                    outputBuilder.Append(
                        $"\n[output exceeded the {BackgroundJobManagerActor.MaxCapturedOutputChars}-char capture ceiling — head and tail shown]");

                var fullOutput = SecretOutputRedactor.Redact(outputBuilder.ToString());

                try
                {
                    await File.WriteAllTextAsync(_outputLogPath, fullOutput);
                }
                catch // slopwatch-ignore: SW003 best-effort log write — output still delivered via actor message
                {
                }

                self.Tell(new ProcessExited(process.ExitCode, fullOutput));
            }
            catch (Exception ex)
            {
                self.Tell(new ProcessExited(-1, $"Error capturing output: {ex.Message}"));
            }
        });
    }

    private void HandleProcessExited(ProcessExited msg)
    {
        _timeoutHandle?.Cancel();

        var status = msg.ExitCode == 0
            ? BackgroundJobStatus.Completed
            : BackgroundJobStatus.Failed;

        ReportCompletion(status, msg.ExitCode, msg.Output);
    }

    private void HandleTimeout()
    {
        _log.Warning("Background job {JobId} timed out after {Timeout}s",
            _definition.Id, _definition.TimeoutSeconds);

        KillProcess();
        ReportCompletion(BackgroundJobStatus.TimedOut, -1, "Process killed: timeout exceeded");
    }

    private void HandleCancel()
    {
        _log.Info("Background job {JobId} cancellation requested", _definition.Id);
        _timeoutHandle?.Cancel();
        KillProcess();
        ReportCompletion(BackgroundJobStatus.Cancelled, -1, "Cancelled by user");
    }

    private void KillProcess()
    {
        if (_process is null) return;

        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) // slopwatch-ignore: SW003 expected TOCTOU race — process exited between HasExited check and Kill
        {
        }
        catch (Exception ex)
        {
            _log.Warning("Failed to kill process tree for job {JobId}: {Error}",
                _definition.Id, ex.Message);
        }
    }

    private void ReportCompletion(BackgroundJobStatus status, int exitCode, string? output)
    {
        var outputTail = output is { Length: > BackgroundJobManagerActor.MaxOutputTailChars }
            ? output[^BackgroundJobManagerActor.MaxOutputTailChars..]
            : output;

        Context.Parent.Tell(new BackgroundJobCompleted
        {
            JobId = _definition.Id,
            Status = status,
            ExitCode = exitCode,
            OutputTail = outputTail,
            OutputFilePath = _outputLogPath,
            Duration = _timeProvider.GetUtcNow() - _definition.StartedAt
        });

        Context.Stop(Self);
    }

    private sealed record ProcessExited(int ExitCode, string? Output);

    private sealed record TimeoutTick
    {
        public static readonly TimeoutTick Instance = new();
    }
}

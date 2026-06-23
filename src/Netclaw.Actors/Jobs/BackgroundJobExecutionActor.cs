// -----------------------------------------------------------------------
// <copyright file="BackgroundJobExecutionActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Akka.Actor;
using Akka.Event;
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

        // Release the OS process handle + the wait handle WaitForExitAsync
        // allocates. Without this they linger until finalization; over a
        // long-lived daemon that starts many jobs (the intended workload),
        // that leaks kernel handles. Best-effort: the capture Task may still
        // hold the streams on a kill/timeout path and throw ObjectDisposedException,
        // which its own catch swallows.
        try
        {
            _process?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warning("Failed to dispose process for job {JobId}: {Error}",
                _definition.Id, ex.Message);
        }
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
        var outputLogPath = _outputLogPath;
        Task.Run(async () =>
        {
            // Stream-to-disk capture: a background job is a detached process with
            // no completion expectation (a dev server may never exit), so output
            // must hit the log as it is produced — not at exit. The log itself is
            // rotation-bounded; the pumps keep draining past any write failure so
            // the child never deadlocks on a full pipe.
            var outputLog = new JobOutputLog(outputLogPath);
            try
            {
                var stdoutPump = PumpToLogAsync(process.StandardOutput, outputLog, isStderr: false);
                var stderrPump = PumpToLogAsync(process.StandardError, outputLog, isStderr: true);

                await Task.WhenAll(stdoutPump, stderrPump);
                await process.WaitForExitAsync();
                await outputLog.DisposeAsync();

                var (tail, _) = JobOutputLog.ReadTail(
                    outputLogPath, BackgroundJobManagerActor.MaxOutputTailChars);

                // Re-redact the assembled multi-line tail before it is delivered
                // to the session/LLM. The on-disk log is redacted per line, which
                // misses secrets that span line boundaries (e.g. a PEM block); a
                // pass over the joined tail catches those before they reach the model.
                tail = SecretOutputRedactor.Redact(tail);

                if (outputLog.Rotated)
                    tail += $"\n[earlier output rotated to {outputLog.RotatedPath}]";
                if (outputLog.WriteFailure is not null)
                    tail += $"\n[output capture failed mid-run: {outputLog.WriteFailure} — the log is incomplete]";

                self.Tell(new ProcessExited(process.ExitCode, tail));
            }
            catch (Exception ex)
            {
                await outputLog.DisposeAsync();
                self.Tell(new ProcessExited(-1, $"Error capturing output: {ex.Message}"));
            }
        });
    }

    private static async Task PumpToLogAsync(StreamReader reader, JobOutputLog outputLog, bool isStderr)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            await outputLog.WriteLineAsync(line, isStderr);
        }
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

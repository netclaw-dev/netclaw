// -----------------------------------------------------------------------
// <copyright file="BackgroundJobExecutionActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Text;
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

        if (!string.IsNullOrWhiteSpace(_definition.WorkingDirectory))
            psi.WorkingDirectory = _definition.WorkingDirectory;
        else
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(_definition.Command);
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
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                var stdout = await stdoutTask;
                var stderr = await stderrTask;
                await process.WaitForExitAsync();

                outputBuilder.Append(stdout);
                if (!string.IsNullOrEmpty(stderr))
                {
                    outputBuilder.AppendLine();
                    outputBuilder.Append("STDERR:\n");
                    outputBuilder.Append(stderr);
                }

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
            JobId = new BackgroundJobId(_definition.Id),
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

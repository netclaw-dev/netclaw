// -----------------------------------------------------------------------
// <copyright file="BackgroundJobManagerActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.DependencyInjection;
using Akka.Event;
using Akka.Hosting;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Security;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.Jobs.BackgroundJobProtocol;

namespace Netclaw.Actors.Jobs;

/// <summary>
/// Infrastructure singleton that manages background job lifecycle independently
/// of any session. Follows the same pattern as <c>ReminderManagerActor</c>.
/// </summary>
public sealed class BackgroundJobManagerActor : ReceiveActor
{
    internal const int MaxConcurrentJobs = 5;
    internal const int MaxOutputTailChars = 2000;

    // Capture ceiling for a job's output log: the execution actor drains each
    // stream to this bound (head+tail) so a chatty long-running job can't buffer
    // its full output in memory and OOM the daemon. The log holds a head+tail view
    // for floods larger than the ceiling; the message still carries the last
    // MaxOutputTailChars.
    internal const int MaxCapturedOutputChars = 256_000;
    internal const string JobDeliveryKeyPrefix = "bg-job:";
    internal const string SystemSenderId = "background-job-system";
    internal const string SourceKind = "background-job";
    private static readonly TimeSpan DefaultAckTimeout = TimeSpan.FromSeconds(30);

    private readonly BackgroundJobDefinitionStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly IOperationalNotificationSink _notificationSink;
    private readonly ILoggingAdapter _log;

    private readonly HashSet<string> _activeJobIds = [];
    private readonly Queue<string> _deferredQueue = new();
    private readonly Dictionary<string, BackgroundJobDefinition> _definitions = [];

    public BackgroundJobManagerActor(
        BackgroundJobDefinitionStore store,
        TimeProvider timeProvider,
        IOperationalNotificationSink? notificationSink = null)
    {
        _store = store;
        _timeProvider = timeProvider;
        _notificationSink = notificationSink ?? NullNotificationSink.Instance;
        _log = Context.GetLogger();

        ReceiveAsync<StartBackgroundJob>(HandleStartAsync);
        Receive<BackgroundJobCompleted>(HandleCompleted);
        Receive<CancelBackgroundJob>(HandleCancel);
        Receive<QueryBackgroundJob>(HandleQuery);
        Receive<KillJobsForSession>(HandleKillJobsForSession);
        Receive<GetBackgroundJobManagerHealth>(_ => HandleGetHealth());
    }

    protected override void PreStart()
    {
        _log.Info("BackgroundJobManagerActor started");
        // Run synchronously in PreStart so reconciliation completes before any
        // user message is dispatched. A Self.Tell approach races on slow schedulers
        // (macOS CI): ActorOf returns before PreStart executes, allowing external
        // messages to queue ahead of the Reconcile message.
        HandleReconcile();
    }

    private async Task HandleStartAsync(StartBackgroundJob cmd)
    {
        var jobId = new BackgroundJobId(Guid.NewGuid().ToString("N")[..12]);
        var now = _timeProvider.GetUtcNow();

        var definition = new BackgroundJobDefinition
        {
            Id = jobId,
            Command = cmd.Command,
            WorkingDirectory = cmd.WorkingDirectory,
            SessionId = cmd.SessionId,
            Rationale = cmd.Rationale,
            Status = BackgroundJobStatus.Pending,
            TimeoutSeconds = cmd.TimeoutSeconds,
            StartedAtMs = now.ToUnixTimeMilliseconds(),
            Audience = cmd.Audience,
            Boundary = cmd.Boundary,
            OriginChannelType = cmd.OriginChannelType,
            SenderId = cmd.SenderId
        };

        _store.Save(definition);
        _definitions[jobId.Value] = definition;

        var outputLogPath = _store.GetOutputLogPathOnly(jobId);

        if (_activeJobIds.Count >= MaxConcurrentJobs)
        {
            _log.Info("Background job concurrency limit reached ({0}), queuing job {1}",
                MaxConcurrentJobs, jobId.Value);
            _deferredQueue.Enqueue(jobId.Value);
            Sender.Tell(new BackgroundJobStarted(jobId, outputLogPath));
            return;
        }

        SpawnExecution(definition);
        Sender.Tell(new BackgroundJobStarted(jobId, outputLogPath));
    }

    private void HandleCompleted(BackgroundJobCompleted completed)
    {
        _activeJobIds.Remove(completed.JobId.Value);

        BackgroundJobDefinition? def = null;
        var wasReaped = false;
        if (_definitions.TryGetValue(completed.JobId.Value, out var existing))
        {
            // A reaped job's child reports Cancelled when its process dies, but
            // the reap already decided the terminal status — keep Reaped and
            // suppress delivery (delivering would rehydrate the session that
            // passivated).
            wasReaped = existing.Status is BackgroundJobStatus.Reaped;
            def = existing with
            {
                Status = wasReaped ? BackgroundJobStatus.Reaped : completed.Status,
                ExitCode = completed.ExitCode,
                CompletedAtMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
            };
            _store.Save(def);
        }

        if (!wasReaped)
            DeliverResultToSession(completed, def);

        _definitions.Remove(completed.JobId.Value);
        DispatchDeferred();
    }

    private void HandleKillJobsForSession(KillJobsForSession cmd)
    {
        var owned = _definitions.Values
            .Where(d => d.SessionId == cmd.SessionId
                        && d.Status is BackgroundJobStatus.Running or BackgroundJobStatus.Pending)
            .ToList();

        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        foreach (var def in owned)
        {
            var reaped = def with
            {
                Status = BackgroundJobStatus.Reaped,
                CompletedAtMs = nowMs
            };
            _definitions[def.Id.Value] = reaped;
            _store.Save(reaped);

            var child = Context.Child($"job-{def.Id.Value}");
            if (!child.IsNobody())
            {
                // The child kills its process tree on cancel and reports back;
                // HandleCompleted sees the Reaped status and suppresses delivery.
                child.Tell(new CancelBackgroundJob(def.Id, def.SessionId, def.Audience, def.Boundary));
            }

            _log.Info("Reaped background job {JobId} for passivating session {SessionId}",
                def.Id, cmd.SessionId);
        }

        // Ack after kills are initiated and definitions marked — process death
        // follows from the child's synchronous Kill(entireProcessTree) on the
        // cancel message; a wedged child is backstopped by its PostStop kill
        // and, ultimately, daemon teardown (no job process outlives the daemon).
        Sender.Tell(new SessionJobsReaped(cmd.SessionId, owned.Count));
    }

    private void HandleCancel(CancelBackgroundJob cmd)
    {
        if (!_definitions.TryGetValue(cmd.JobId.Value, out var def))
            def = _store.Get(cmd.JobId);

        if (def is null
            || def.SessionId != cmd.SessionId
            || def.Audience != cmd.Audience
            || def.Boundary != cmd.Boundary)
        {
            Sender.Tell(new BackgroundJobCancelResponse(cmd.JobId, false));
            return;
        }

        var childName = $"job-{cmd.JobId.Value}";
        var child = Context.Child(childName);
        if (!child.IsNobody())
        {
            child.Tell(cmd);
            Sender.Tell(new BackgroundJobCancelResponse(cmd.JobId, true));
        }
        else if (def.Status is BackgroundJobStatus.Pending)
        {
            var updated = def with
            {
                Status = BackgroundJobStatus.Cancelled,
                CompletedAtMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
            };
            _definitions[cmd.JobId.Value] = updated;
            _store.Save(updated);
            Sender.Tell(new BackgroundJobCancelResponse(cmd.JobId, true));
        }
        else
        {
            Sender.Tell(new BackgroundJobCancelResponse(cmd.JobId, true));
        }
    }

    private void HandleQuery(QueryBackgroundJob query)
    {
        if (!_definitions.TryGetValue(query.JobId.Value, out var def))
        {
            var diskDef = _store.Get(query.JobId);
            if (diskDef is not null)
                def = diskDef;
        }

        if (def is null
            || def.SessionId != query.SessionId
            || def.Audience != query.Audience
            || def.Boundary != query.Boundary)
        {
            Sender.Tell(new BackgroundJobStatusResponse
            {
                JobId = query.JobId,
                Status = BackgroundJobStatus.Lost,
                Found = false
            });
            return;
        }

        var outputFilePath = _store.GetOutputLogPathOnly(query.JobId);
        string? outputTail = null;
        var outputFileExists = false;
        try
        {
            // Bounded seek-from-end: the log streams while the job runs and is
            // rotation-capped at megabytes — never load the whole file for a tail.
            (outputTail, _) = JobOutputLog.ReadTail(outputFilePath, MaxOutputTailChars);
            // Re-redact the multi-line tail: the on-disk log is redacted per line,
            // which misses secrets spanning line boundaries before they reach the LLM.
            outputTail = SecretOutputRedactor.Redact(outputTail);
            outputFileExists = true;
        }
        catch (FileNotFoundException) { } // slopwatch-ignore: SW003 output file may not exist yet for running jobs
        catch (DirectoryNotFoundException) { } // slopwatch-ignore: SW003 job output directory may not exist yet for running jobs
        catch (Exception ex)
        {
            _log.Warning("Failed to read output log for job {JobId}: {Error}",
                query.JobId.Value, ex.Message);
        }

        var elapsed = def.CompletedAtMs is not null
            ? DateTimeOffset.FromUnixTimeMilliseconds(def.CompletedAtMs.Value) -
              DateTimeOffset.FromUnixTimeMilliseconds(def.StartedAtMs)
            : _timeProvider.GetUtcNow() - DateTimeOffset.FromUnixTimeMilliseconds(def.StartedAtMs);

        Sender.Tell(new BackgroundJobStatusResponse
        {
            JobId = query.JobId,
            Status = def.Status,
            Found = true,
            ExitCode = def.ExitCode,
            OutputTail = outputTail,
            OutputFilePath = outputFileExists ? outputFilePath : null,
            Elapsed = elapsed,
            Rationale = def.Rationale
        });
    }

    private void HandleGetHealth()
    {
        Sender.Tell(new BackgroundJobManagerHealthResponse(_activeJobIds.Count, _deferredQueue.Count));
    }

    private void HandleReconcile()
    {
        var persisted = _store.List();
        EmitRejectedLegacyDefinitionAlerts();

        var reconciled = 0;

        foreach (var def in persisted)
        {
            var current = def;
            if (def.Status is BackgroundJobStatus.Running or BackgroundJobStatus.Pending)
            {
                var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
                current = def with
                {
                    Status = BackgroundJobStatus.Lost,
                    CompletedAtMs = nowMs
                };
                _store.Save(current);
                reconciled++;

                _log.Warning("Reconciled orphaned background job {JobId} as lost (process lost during restart)",
                    def.Id);

                // Termination is a notification, whatever its cause — tell the
                // owning session its process is gone so the agent can relaunch.
                // Volume is bounded: passivated sessions have no live jobs, so
                // only sessions that were warm at crash time appear here. The
                // streamed log holds everything the process said before the
                // daemon died.
                NotifyLostJob(current, nowMs);
            }

            _definitions[current.Id.Value] = current;
        }

        if (reconciled > 0)
            _log.Info("Background job startup reconciliation: marked {0} orphaned job(s) as lost", reconciled);
    }

    private void NotifyLostJob(BackgroundJobDefinition lost, long nowMs)
    {
        var outputFilePath = _store.GetOutputLogPathOnly(lost.Id);
        string? outputTail = null;
        try
        {
            (outputTail, _) = JobOutputLog.ReadTail(outputFilePath, MaxOutputTailChars);
            // Re-redact the multi-line tail before it is delivered to the session.
            outputTail = SecretOutputRedactor.Redact(outputTail);
        }
        catch (FileNotFoundException) { } // slopwatch-ignore: SW003 job may have produced no output before the restart
        catch (DirectoryNotFoundException) { } // slopwatch-ignore: SW003 job may have produced no output before the restart
        catch (Exception ex)
        {
            _log.Warning("Failed to read output log for lost job {JobId}: {Error}",
                lost.Id.Value, ex.Message);
        }

        DeliverResultToSession(new BackgroundJobCompleted
        {
            JobId = lost.Id,
            Status = BackgroundJobStatus.Lost,
            ExitCode = -1,
            OutputTail = outputTail,
            OutputFilePath = File.Exists(outputFilePath) ? outputFilePath : null,
            Duration = TimeSpan.FromMilliseconds(Math.Max(0, nowMs - lost.StartedAtMs))
        }, lost);
    }

    private void EmitRejectedLegacyDefinitionAlerts()
    {
        var rejected = _store.ConsumeRejectedLegacyDefinitions();
        if (rejected.Count == 0)
            return;

        var rejectedIds = string.Join(", ", rejected.Select(x => x.JobId));
        _notificationSink.Emit(OperationalAlert.Create(
            _timeProvider,
            "background-job.schema.legacy_rejected",
            AlertType.BackgroundJobSchemaDropped,
            $"Rejected {rejected.Count} legacy background job definition(s) missing trust fields during startup. Repair or recreate job IDs: {rejectedIds}.",
            AlertSeverity.Warning,
            source: "startup",
            context: new Dictionary<string, string>
            {
                ["rejectedCount"] = rejected.Count.ToString(),
                ["rejectedIds"] = rejectedIds
            }));

        _log.Warning(
            "Rejected {0} legacy background job definition(s) missing trust fields during startup: {1}",
            rejected.Count,
            rejectedIds);
    }

    private void SpawnExecution(BackgroundJobDefinition definition)
    {
        var running = definition with { Status = BackgroundJobStatus.Running };
        _definitions[running.Id.Value] = running;
        _store.Save(running);
        _activeJobIds.Add(running.Id.Value);

        var outputLogPath = _store.GetOutputLogPath(running.Id);
        var props = DependencyResolver.For(Context.System)
            .Props<BackgroundJobExecutionActor>(running, outputLogPath, _timeProvider);
        Context.ActorOf(props, $"job-{running.Id}");
    }

    private void DispatchDeferred()
    {
        while (_deferredQueue.Count > 0 && _activeJobIds.Count < MaxConcurrentJobs)
        {
            var jobId = _deferredQueue.Dequeue();
            if (!_definitions.TryGetValue(jobId, out var def)
                || def.Status is BackgroundJobStatus.Cancelled or BackgroundJobStatus.Reaped)
                continue;

            SpawnExecution(def);
        }
    }

    private void DeliverResultToSession(BackgroundJobCompleted completed, BackgroundJobDefinition? def)
    {
        if (def is null) return;

        var sessionId = def.SessionId;
        var originChannelType = def.OriginChannelType;

        var jobDeliveryKey = $"{JobDeliveryKeyPrefix}{def.Id}";
        var content = BuildResultContent(completed, def);

        var source = new MessageSource
        {
            ChannelType = originChannelType,
            SenderId = new Protocol.SenderId(SystemSenderId),
            MessageId = jobDeliveryKey,
            TurnId = new Protocol.TurnId(jobDeliveryKey),
            Audience = def.Audience,
            Boundary = def.Boundary,
            Principal = PrincipalClassification.VerifiedAutomation,
            Provenance = new SourceProvenance(
                TransportAuthenticity.LocalProcess,
                PayloadTaint.Trusted)
            {
                SourceKind = new SourceKind(BackgroundJobManagerActor.SourceKind)
            },
            ReceivedAt = _timeProvider.GetUtcNow(),
            BackgroundJobId = jobDeliveryKey
        };

        var deliverMsg = new DeliverTrustedSessionTurn(sessionId, content, source);

        var registry = ActorRegistry.For(Context.System);
        var gateway = originChannelType switch
        {
            ChannelType.Slack => registry.TryGet<SlackGatewayActorKey>(out var slack) ? slack : null,
            ChannelType.Tui => registry.TryGet<SignalRGatewayActorKey>(out var signalr) ? signalr : null,
            ChannelType.SignalR => registry.TryGet<SignalRGatewayActorKey>(out var signalr2) ? signalr2 : null,
            _ => null
        };

        if (gateway is null)
        {
            _log.Warning("Cannot deliver background job result for {JobId}: no gateway for channel type {ChannelType}",
                def.Id, originChannelType);
            return;
        }

        gateway.Ask<object>(deliverMsg, DefaultAckTimeout).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _log.Warning("Background job {JobId} delivery failed: {Error}",
                    def.Id, t.Exception?.GetBaseException().Message);
            }
            else if (t.Result is CommandAck)
            {
                _log.Info("Background job {JobId} result delivered to session {SessionId}",
                    def.Id, def.SessionId);
            }
        }, TaskContinuationOptions.ExecuteSynchronously);
    }

    private static string BuildResultContent(BackgroundJobCompleted completed, BackgroundJobDefinition def)
    {
        var statusLabel = completed.Status switch
        {
            BackgroundJobStatus.Completed => completed.ExitCode == 0 ? "completed successfully" : $"completed with exit code {completed.ExitCode}",
            BackgroundJobStatus.Failed => $"failed with exit code {completed.ExitCode}",
            BackgroundJobStatus.TimedOut => "timed out",
            BackgroundJobStatus.Cancelled => "was cancelled",
            BackgroundJobStatus.Lost => "was lost — its process did not survive a daemon restart; relaunch it if still needed",
            _ => $"finished with status {completed.Status}"
        };

        var output = !string.IsNullOrEmpty(completed.OutputTail)
            ? $"\n\nOutput (last {Math.Min(completed.OutputTail.Length, MaxOutputTailChars)} chars):\n```\n{completed.OutputTail}\n```"
            : "\n\n(no output captured)";

        var filePath = !string.IsNullOrEmpty(completed.OutputFilePath)
            ? $"\n\nFull output: {completed.OutputFilePath}"
            : "";

        return $"[Background job {def.Id} {statusLabel}]\n" +
               $"Command: {def.Command}\n" +
               (!string.IsNullOrWhiteSpace(def.WorkingDirectory)
                   ? $"Working directory: {def.WorkingDirectory}\n"
                   : string.Empty) +
               $"Rationale: {def.Rationale}\n" +
               $"Duration: {completed.Duration.TotalSeconds:F1}s" +
               output + filePath;
    }

}

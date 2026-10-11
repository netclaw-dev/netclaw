// -----------------------------------------------------------------------
// <copyright file="ChildProcessJournal.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Akka.Actor;
using Akka.Configuration;
using Microsoft.Data.Sqlite;
using Netclaw.Actors.Serialization;
using Netclaw.Actors.SubAgents;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Evals.ChildRuns;

internal static class ChildProcessJournal
{
    internal static async Task<int> RunAsync(string database, string session, string output)
    {
        if (!File.Exists(database) || string.IsNullOrWhiteSpace(session) || File.Exists(output))
            throw new InvalidDataException("Supply one retained database, one session, and one new output path.");
        var system = ActorSystem.Create("process-journal-projector", ConfigurationFactory.ParseString(
            "akka.actor.provider = local\nakka.loglevel = ERROR\nakka.stdout-loglevel = ERROR"));
        try
        {
            var serializer = new NetclawProtobufSerializer((ExtendedActorSystem)system);
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.GetFullPath(database), Mode = SqliteOpenMode.ReadOnly }.ToString());
            await connection.OpenAsync();
            await using (var schema = connection.CreateCommand())
            {
                schema.CommandText = "PRAGMA table_info(journal)";
                var columns = new HashSet<string>(StringComparer.Ordinal);
                await using var table = await schema.ExecuteReaderAsync();
                while (await table.ReadAsync()) columns.Add(table.GetString(1));
                if (!new[] { "persistence_id", "sequence_number", "identifier", "manifest", "message", "deleted" }
                    .All(columns.Contains))
                    throw new InvalidDataException("The owned daemon journal schema differs from its configured default mapping.");
            }
            await using var command = connection.CreateCommand();
            // The daemon uses the default Akka.Persistence.Sql journal columns.
            command.CommandText = "SELECT sequence_number, identifier, manifest, message, deleted "
                + "FROM journal WHERE persistence_id = $owner ORDER BY sequence_number";
            command.Parameters.AddWithValue("$owner", "session-" + session);
            var rows = new List<object>();
            var runs = new Dictionary<string, BackgroundChildRun>(StringComparer.Ordinal);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var sequence = reader.GetInt64(0);
                var identifier = reader.GetInt32(1);
                var manifest = reader.GetString(2);
                if (reader.GetBoolean(4))
                    throw new InvalidDataException("A process journal row is deleted.");
                if (identifier != serializer.Identifier)
                    throw new InvalidDataException("The process journal uses another event serializer.");
                var value = serializer.FromBinary((byte[])reader.GetValue(3), manifest);
                var projected = Project(value, runs);
                if (projected is not null)
                    rows.Add(new { persistence_id = "session-" + session, sequence_nr = sequence,
                        serializer_id = identifier, manifest, event_type = projected.Value.Type, data = projected.Value.Data });
            }
            if (runs.Count != 1)
                throw new InvalidDataException("The fixed process case requires exactly one durable child acceptance.");
            await using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
            await JsonSerializer.SerializeAsync(stream, rows, new JsonSerializerOptions { WriteIndented = true });
            return 0;
        }
        finally
        {
            await system.Terminate();
        }
    }

    private static (string Type, object Data)? Project(object value, Dictionary<string, BackgroundChildRun> runs)
    {
        switch (value)
        {
            case ChildRunAccepted accepted:
                runs.Add(accepted.Run.RunId.Value, accepted.Run);
                return (nameof(ChildRunAccepted), new
                {
                    session_id = accepted.SessionId.Value, run_id = accepted.Run.RunId.Value,
                    scope_id = accepted.Run.ScopeId.Value, source_operation = accepted.Run.SourceOperation,
                    original_call_id = (accepted.Run.StartKey as ChildRunStartKey.Tool)?.CallId.Value,
                    authority = Authority(accepted.Run.OriginalContext)
                });
            case ToolApprovalRequested requested:
                return (nameof(ToolApprovalRequested), new
                {
                    session_id = requested.SessionId.Value, run_id = requested.SourceChildRunId?.Value,
                    original_call_id = requested.OriginalChildCallId?.Value, call_id = requested.CallId,
                    attempt_id = requested.AuthorizationAttemptId, requester = requested.RequesterSenderId?.Value,
                    cwd = requested.Cwd, options = requested.OptionKeys, candidates = requested.Candidates,
                    authority = Authority(requested.TurnContext), requested_at_ms = requested.RequestedAtMs
                });
            case ToolApprovalResolved resolved:
                return (nameof(ToolApprovalResolved), new
                {
                    session_id = resolved.SessionId.Value, run_id = resolved.SourceChildRunId?.Value,
                    call_id = resolved.CallId, attempt_id = resolved.AuthorizationAttemptId,
                    decision = resolved.Decision, resolved_at_ms = resolved.ResolvedAtMs
                });
            case ChildRunEvent child:
                if (!runs.TryGetValue(child.RunId.Value, out var run))
                    throw new InvalidDataException("A child event lacks its actual prior acceptance.");
                if (child is ChildRunEvent.Checkpointed checkpointed)
                    runs[child.RunId.Value] = run = run with { ChildCheckpoint = checkpointed.Checkpoint };
                var terminal = child switch
                {
                    ChildRunEvent.TerminalRecorded recorded => recorded.Terminal,
                    ChildRunEvent.ResultPrepared prepared => prepared.Terminal,
                    _ => null
                };
                var input = (child as ChildRunEvent.DeliveryAdmitted)?.Input;
                return (nameof(ChildRunEvent), new
                {
                    session_id = child.SessionId.Value, run_id = child.RunId.Value, kind = child.GetType().Name,
                    terminal = terminal is null ? null : Terminal(run, terminal),
                    checkpoint = (child as ChildRunEvent.Checkpointed)?.Checkpoint,
                    input_id = input?.InputId.Value, authority = Authority(input?.TurnContext),
                    recorded_at_ms = child.RecordedAtMs
                });
            case InputAdmitted admitted:
                return (nameof(InputAdmitted), new
                {
                    session_id = admitted.SessionId.Value, run_id = admitted.SourceChildRunId?.Value,
                    input_id = admitted.InputId.Value, authority = Authority(admitted.TurnContext),
                    source_message_id = admitted.SourceMessageId, user_message = admitted.UserMessage.Content,
                    user_role = admitted.UserMessage.Role.ToString(),
                    admitted_at_ms = admitted.AdmittedAtMs
                });
            case ToolTaskAdopted adopted:
                return (nameof(ToolTaskAdopted), new
                {
                    session_id = adopted.SessionId.Value, run_id = adopted.ContinuedChildRunId?.Value,
                    input_ids = adopted.InputIds.Select(id => id.Value), authority = Authority(adopted.TurnContext),
                    starts_child_continuation_window = adopted.StartsChildContinuationWindow,
                    adopted_at_ms = adopted.AdoptedAtMs
                });
            case InputClosed closed:
                return (nameof(InputClosed), new
                {
                    session_id = closed.SessionId.Value, input_ids = closed.InputIds.Select(id => id.Value),
                    task_id = closed.TaskId, closed_at_ms = closed.ClosedAtMs
                });
            default:
                return null;
        }
    }

    private static object? Authority(TurnContextRecord? value) => value is null ? null : new
    {
        SessionId = value.SessionId.Value, value.TurnId, Audience = value.Audience.ToString(),
        Boundary = value.Boundary?.ToString(), value.ChannelType, RequesterSenderId = value.RequesterSenderId?.Value,
        RequesterPrincipal = value.RequesterPrincipal?.ToString(),
        TransportAuthenticity = value.TransportAuthenticity.ToString(), PayloadTaint = value.PayloadTaint.ToString(),
        value.SourceScope, value.SourceKind, value.DefaultDeliveryTarget, value.RequestedDeliveryTarget,
        value.HasAdoptedContext, value.HasThirdPartyAdoptedContext, value.AdoptedSpeakerIds,
        value.SupportsInteractiveApproval
    };

    private static object Terminal(BackgroundChildRun run, ChildRunTerminal terminal)
    {
        var result = terminal.Result;
        return new
        {
            run_id = result.RunId?.Value, scope_id = result.ScopeId?.Value,
            state = (run with { Terminal = terminal }).State.ToString(), source_operation = run.SourceOperation,
            outcome = result.Outcome.ToString(), reason = result.OutcomeReason?.Value,
            output = result.Output, working_context = result.WorkingContext, findings = result.Findings,
            log_path = result.LogPath, artifact_directory = result.ArtifactDirectory,
            warning = terminal.EvidenceWarning, checkpoint = run.ChildCheckpoint
        };
    }
}

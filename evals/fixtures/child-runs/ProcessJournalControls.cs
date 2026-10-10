// -----------------------------------------------------------------------
// <copyright file="ProcessJournalControls.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Akka.Actor;
using Akka.Configuration;
using Microsoft.Data.Sqlite;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Serialization;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.SubAgents.SubAgentProtocol;

namespace Netclaw.Evals.ChildRuns;

internal static class ProcessJournalControls
{
    internal static async Task<int> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "netclaw-process-projection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        ActorSystem? system = null;
        try
        {
            system = ActorSystem.Create("process-projection-controls", ConfigurationFactory.ParseString(
                "akka.actor.provider = local\nakka.loglevel = ERROR\nakka.stdout-loglevel = ERROR"));
            var serializer = new NetclawProtobufSerializer((ExtendedActorSystem)system);
            var owner = new SessionId("process-projection-owner");
            var authority = new TurnContextRecord
            {
                SessionId = owner, TurnId = "actual-ingress-turn", Audience = TrustAudience.Personal,
                Boundary = TrustBoundary.TrustedInstance, ChannelType = "tui", RequesterSenderId = new SenderId("local"),
                RequesterPrincipal = PrincipalClassification.Operator, TransportAuthenticity = TransportAuthenticity.LocalProcess,
                PayloadTaint = PayloadTaint.Trusted, SourceKind = "signalr", SupportsInteractiveApproval = true
            };
            const string prompt = "Retain this exact task.\nRetain its second line.";
            var input = new InputAdmitted { SessionId = owner, InputId = new InputId("original-input"),
                SourceMessageId = "signalr:projection", TurnContext = authority,
                UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = prompt } };
            var run = new BackgroundChildRun
            {
                RunId = new SubAgentRunId("projection-run"), ScopeId = new SubAgentScopeId("process-projection-owner/subagent/worker/projection-run"),
                AgentName = new AgentName("worker"), SourceOperation = "spawn_agent", ArgumentsDigest = new string('a', 64),
                StartKey = new ChildRunStartKey.Tool(new ToolCallId("start")) { SessionId = owner, TurnId = new TurnId(authority.TurnId) },
                OriginalContext = authority, OriginInputIds = [input.InputId], InitialWorkingSnapshot = new WorkingContextSnapshot
                    { WorkingContext = WorkingContext.Empty, Git = new GitWorkingContextInspection.Skipped() },
                ParentCheckpoint = new ToolLoopCheckpoint { TaskId = "same-task" }
            };
            var terminal = new ChildRunTerminal(new SubAgentResult
            {
                Completion = new ChildRunCompletion.Completed(new WorkingContextDelta()), Output = "complete\nactual bytes",
                AgentName = run.AgentName, RunId = run.RunId, ScopeId = run.ScopeId,
                LogPath = "/owned/logs/session.log", ArtifactDirectory = "/owned/artifacts"
            }, false, null);
            var events = new object[]
            {
                input, new ChildRunAccepted { SessionId = owner, Run = run },
                new ToolApprovalRequested { SessionId = owner, SourceChildRunId = run.RunId,
                    OriginalChildCallId = new ToolCallId("protected"), CallId = "approval", AuthorizationAttemptId = "actual-attempt",
                    ToolName = "shell_execute", RequesterSenderId = authority.RequesterSenderId, TurnContext = authority,
                    Cwd = "/owned", OptionKeys = ["approve_once"],
                    Candidates = [new ApprovalCandidate("owned-command", "/owned") { VerbTokens = ["owned-command"], Shell = ApprovalShell.Bash }] },
                new ChildRunEvent.TerminalRecorded(terminal, 4) { SessionId = owner, RunId = run.RunId, RecordedAtMs = 4 }
            };
            var database = Path.Combine(root, "journal.sqlite");
            await using (var connection = new SqliteConnection("Data Source=" + database))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE journal (persistence_id TEXT, sequence_number INTEGER, identifier INTEGER, manifest TEXT, message BLOB, deleted INTEGER)";
                await command.ExecuteNonQueryAsync();
                for (var index = 0; index < events.Length; index++)
                {
                    command.Parameters.Clear();
                    command.CommandText = "INSERT INTO journal VALUES ($owner, $sequence, $identifier, $manifest, $message, 0)";
                    command.Parameters.AddWithValue("$owner", "session-" + owner.Value);
                    command.Parameters.AddWithValue("$sequence", index + 1);
                    command.Parameters.AddWithValue("$identifier", serializer.Identifier);
                    command.Parameters.AddWithValue("$manifest", serializer.Manifest(events[index]));
                    command.Parameters.AddWithValue("$message", serializer.ToBinary(events[index]));
                    await command.ExecuteNonQueryAsync();
                }
            }
            var output = Path.Combine(root, "projected.json");
            Check(await ChildProcessJournal.RunAsync(database, owner.Value, output) == 0, "The real journal projection failed.");
            using var projected = JsonDocument.Parse(await File.ReadAllTextAsync(output));
            var rows = projected.RootElement;
            Check(rows.GetArrayLength() == 4 && rows[0].GetProperty("data").GetProperty("user_message").GetString() == prompt
                && rows[0].GetProperty("data").GetProperty("user_role").GetString() == "User", "The ingress text or role changed.");
            foreach (var row in rows.EnumerateArray().Take(3))
            {
                var context = row.GetProperty("data").GetProperty("authority");
                Check(context.ValueKind == JsonValueKind.Object
                    && context.GetProperty("SessionId").GetString() == owner.Value
                    && context.GetProperty("RequesterSenderId").GetString() == "local"
                    && context.GetProperty("Audience").GetString() == "Personal", "The authority lost its scalar shape.");
            }
            Check(rows[2].GetProperty("data").GetProperty("candidates")[0].GetProperty("VerbTokens")[0].GetString() == "owned-command"
                && rows[3].GetProperty("data").GetProperty("terminal").GetProperty("output").GetString() == terminal.Result.Output
                && rows[3].GetProperty("data").GetProperty("terminal").GetProperty("state").GetString() == "Completed",
                "The canonical candidate or terminal changed.");
            await Mutate("UPDATE journal SET identifier = 0 WHERE sequence_number = 1");
            await Reject("foreign-serializer.json");
            await Mutate("ALTER TABLE journal RENAME COLUMN message TO foreign_message");
            await Reject("foreign-schema.json");
            Console.WriteLine(JsonSerializer.Serialize(new { passed = true, controls = 3, projected_events = events.Length }));
            return 0;

            async Task Mutate(string sql)
            {
                await using var connection = new SqliteConnection("Data Source=" + database);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync();
            }

            async Task Reject(string filename)
            {
                try { await ChildProcessJournal.RunAsync(database, owner.Value, Path.Combine(root, filename)); }
                catch (InvalidDataException) { return; }
                throw new InvalidDataException("The changed journal schema or serializer was accepted.");
            }
        }
        finally
        {
            if (system is not null) await system.Terminate();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

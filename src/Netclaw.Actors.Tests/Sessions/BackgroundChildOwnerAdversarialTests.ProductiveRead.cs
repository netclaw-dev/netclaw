// -----------------------------------------------------------------------
// <copyright file="BackgroundChildOwnerAdversarialTests.ProductiveRead.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using Akka.Actor;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed partial class BackgroundChildOwnerAdversarialTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_report_created_by_the_accepted_child_remains_readable_after_its_canonical_delivery(bool priorMissingReads)
    {
        const string report = """
            {"completed":true,"artifact":"implementation.json","changed_files":["source/catalog.py"],"checks":{"duplicate_rejection":true,"prior_state_preserved":true,"valid_order_preserved":true},"limits":"This deterministic child fixture proves an actual artifact write and an authorized parent read. It does not prove Git integration, release, deployment."}
            """ + "\n";
        const string source = "The catalog must preserve its prior records after an invalid refresh.\n";
        const string finalReply = "I read the complete child report after its canonical delivery.";
        Assert.Equal(349, Encoding.UTF8.GetByteCount(report));
        var root = Path.Combine(_directory!.Paths.WorkspacesDirectory, "productive-child-read");
        Directory.CreateDirectory(root);
        var reportPath = Path.Combine(root, "implementation.json");
        var sourcePath = Path.Combine(root, "requirements.md");
        await File.WriteAllTextAsync(sourcePath, source, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(reportPath));

        var config = new ToolConfig();
        var registry = Host.Services.GetRequiredService<ToolRegistry>();
        registry.ReplaceCore(new FileReadTool(config, _directory.Paths, new ToolPathPolicy([])));
        registry.RegisterCore(new FileWriteTool(config, _directory.Paths, new ToolPathPolicy([])));
        var profiles = new SubAgentDefinitionRegistry();
        profiles.Register(new SubAgentProfile
        {
            Name = "worker", Description = "Write the assigned complete report.",
            SystemPrompt = "Write the complete report with file_write.",
            ModelRole = ModelRole.Compaction, ToolNames = [FileWriteTool.ToolName],
            EmitStructuredFindings = false, Visibility = SubAgentVisibility.UserFacing
        });
        _start!.StartTool = new SpawnAgentTool(profiles, _start.Spawner, _directory.Paths);
        FunctionCallContent Read(string id, string path) => new(id, FileReadTool.ToolName,
            new Dictionary<string, object?> { ["Path"] = path, ["_rationale"] = "Read the complete assigned file." });
        _main.ToolCallsOnFirstCall = null;
        _main.PlannedResponses.Enqueue([new FunctionCallContent("start-1", "spawn_agent", new Dictionary<string, object?>
        {
            ["Agent"] = "worker", ["Task"] = "Write the complete report at " + reportPath,
            ["_rationale"] = "Delegate the complete report to the child."
        })]);
        if (priorMissingReads)
        {
            _main.PlannedResponses.Enqueue([Read("report-before-1", reportPath)]);
            _main.PlannedResponses.Enqueue([Read("report-before-2", reportPath)]);
        }
        else
            _main.PlannedResponses.Enqueue([Read("independent-source", sourcePath)]);
        _main.PlannedResponses.Enqueue([new TextContent("The child owns its report task. The parent completed its available work.")]);
        _main.PlannedResponses.Enqueue([Read("report-after-3", reportPath)]);
        _main.PlannedResponses.Enqueue([Read("report-after-4", reportPath)]);
        _main.PlannedResponses.Enqueue([new TextContent(finalReply)]);
        _child.ToolCallsOnFirstCall = [new FunctionCallContent("child-report-write", FileWriteTool.ToolName,
            new Dictionary<string, object?>
            {
                ["Path"] = reportPath, ["Content"] = report,
                ["_rationale"] = "Write the complete assigned report."
            })];
        _child.PlannedResponses.Enqueue([new TextContent("The complete report exists at " + reportPath)]);

        var (owner, manager, subscriber) = await CreateOwnerAsync();
        try
        {
            await SendOriginalAsync(manager);
            await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            await CompletedAsync(subscriber);
            var before = await ReadJournalPositionsAsync();
            var accepted = Assert.Single(before.Select(row => row.Event).OfType<ChildRunAccepted>());
            Assert.Equal("original", accepted.Run.OriginalContext.TurnId);
            Assert.Equal(new SenderId("operator-a"), accepted.Run.OriginalContext.RequesterSenderId);
            Assert.False(_childRelease.Task.IsCompleted);
            Assert.False(File.Exists(reportPath));
            Assert.Empty(before.Select(row => row.Event).OfType<ChildRunEvent.TerminalRecorded>());
            var initialReads = before.Where(row => row.Event is ToolCallRecorded call
                && call.ToolResult.ToolCallId is { } id
                && (id.Value.StartsWith("report-before-", StringComparison.Ordinal) || id.Value == "independent-source")).ToArray();
            Assert.Equal(priorMissingReads ? 2 : 1, initialReads.Length);
            foreach (var row in initialReads)
            {
                var read = Assert.IsType<ToolCallRecorded>(row.Event);
                Assert.False(read.LoopObservation!.Synthetic);
                Assert.False(read.LoopObservation.MissingReceipt);
                Assert.Equal(priorMissingReads ? (int)ToolInvocationOutcomeCategory.NotFound
                    : (int)ToolInvocationOutcomeCategory.Success, read.LoopObservation.Category);
                Assert.Equal(priorMissingReads ? "Error: File not found: " + reportPath : source, read.ToolResult.Content);
                Assert.True(before.Single(value => ReferenceEquals(value.Event, accepted)).SequenceNr < row.SequenceNr);
            }
            var current = Assert.IsType<ChildStartReply.Accepted>(await owner.Ask<ChildStartReply>(Retry(accepted), Ceiling,
                TestContext.Current.CancellationToken));
            Assert.Equal(BackgroundChildState.Running, current.State);
            Assert.True(SessionState.SameCanonicalContext(accepted.Run.OriginalContext, current.Run.OriginalContext));

            _childRelease.TrySetResult();
            await CompletedAsync(subscriber);
            var rows = await ReadJournalPositionsAsync();
            var events = rows.Select(row => row.Event).ToArray();
            Assert.Single(events.OfType<ChildRunAccepted>());
            Assert.Single(events.OfType<ChildRunEvent.Started>());
            var terminal = Assert.Single(events.OfType<ChildRunEvent.TerminalRecorded>());
            var prepared = Assert.Single(events.OfType<ChildRunEvent.ResultPrepared>());
            var delivery = Assert.Single(events.OfType<ChildRunEvent.DeliveryAdmitted>());
            var adoption = Assert.Single(events.OfType<ToolTaskAdopted>(), item => item.ContinuedChildRunId is not null);
            Assert.Equal(accepted.Run.RunId, terminal.RunId);
            Assert.Equal(accepted.Run.RunId, prepared.RunId);
            Assert.Equal(accepted.Run.RunId, delivery.RunId);
            Assert.Equal(accepted.Run.RunId, adoption.ContinuedChildRunId);
            Assert.Equal(new[] { delivery.Input.InputId }, adoption.InputIds);
            Assert.True(SessionState.SameCanonicalContext(accepted.Run.OriginalContext, adoption.TurnContext));
            Assert.Equal(SubAgentRunOutcome.Completed, terminal.Terminal.Result.Outcome);
            Assert.Contains(reportPath, terminal.Terminal.Result.WorkingContext!.ConfirmedChangedFiles);
            Assert.Equal(report, await File.ReadAllTextAsync(reportPath, TestContext.Current.CancellationToken));
            Assert.Equal(349, new FileInfo(reportPath).Length);
            var childWrite = Assert.Single(_child.ReceivedMessages[^1]
                .SelectMany(message => message.Contents.OfType<FunctionResultContent>()),
                item => item.CallId == "child-report-write");
            Assert.Equal("Successfully wrote 349 bytes to " + reportPath, Assert.IsType<string>(childWrite.Result));
            long Position(ISessionEvent value) => rows.Single(row => ReferenceEquals(row.Event, value)).SequenceNr;
            Assert.True(Position(terminal) < Position(prepared));
            Assert.True(Position(prepared) < Position(delivery));
            Assert.True(Position(delivery) < Position(adoption));
            Assert.Single(events.OfType<ToolTaskAdopted>(), item => item.ContinuedChildRunId is null);
            foreach (var id in new[] { "report-after-3", "report-after-4" })
            {
                var read = Assert.Single(events.OfType<ToolCallRecorded>(),
                    item => item.ToolResult.ToolCallId == new ToolCallId(id));
                Assert.True(Position(adoption) < Position(read));
                Assert.False(read.LoopObservation!.Synthetic);
                Assert.Equal((int)ToolInvocationOutcomeCategory.Success, read.LoopObservation.Category);
                Assert.Equal(report, read.ToolResult.Content);
            }
            Assert.Equal(finalReply, events.OfType<TurnRecorded>().Last().AssistantReply.Content);
        }
        finally { _childRelease.TrySetResult(); }
    }
}

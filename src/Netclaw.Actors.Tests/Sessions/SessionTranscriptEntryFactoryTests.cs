// -----------------------------------------------------------------------
// <copyright file="SessionTranscriptEntryFactoryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Media;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class SessionTranscriptEntryFactoryTests
{
    private static readonly SessionId SessionId = new("test/transcript");

    [Fact]
    public void Settled_outputs_map_to_complete_transcript_entries()
    {
        var call = new ToolCallOutput
        {
            SessionId = SessionId,
            TimestampMs = 10,
            CallId = new ToolCallId("call-1"),
            ToolName = new ToolName("shell_execute"),
            Rationale = "Verify the source tree",
            ArgumentsJson = "{\"command\":\"dotnet test\"}"
        };
        var tool = SessionTranscriptEntryFactory.Tool(call, new ToolResultOutput
        {
            SessionId = SessionId,
            TimestampMs = 11,
            CallId = call.CallId,
            ToolName = call.ToolName,
            Result = "Passed"
        }, "turn-1");
        var subAgent = SessionTranscriptEntryFactory.SubAgent(new SubAgentOutput
        {
            SessionId = SessionId,
            TimestampMs = 12,
            AgentName = new AgentName("reviewer"),
            Phase = SubAgentPhase.Completed,
            RunId = new SubAgentRunId("run-1"),
            ParentCallId = call.CallId,
            Success = false,
            Outcome = SubAgentRunOutcome.Partial,
            OutcomeReason = SubAgentOutcomeReason.ToolIterationBudgetExhausted,
            Duration = TimeSpan.FromMilliseconds(250),
            FindingsCount = 2,
            MemoryDecision = "accepted"
        }, "turn-1");
        var file = SessionTranscriptEntryFactory.File(new FileOutput
        {
            SessionId = SessionId,
            TimestampMs = 13,
            FilePath = "/tmp/report.txt",
            FileName = "report.txt",
            MimeType = new MimeType("text/plain")
        }, "turn-1");
        var error = SessionTranscriptEntryFactory.Error(new ErrorOutput
        {
            SessionId = SessionId,
            TimestampMs = 14,
            Message = "Provider failed",
            Category = ErrorCategory.ProviderFailure,
            CorrelationId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Cause = new InvalidOperationException("detail")
        }, "turn-1");
        var usage = SessionTranscriptEntryFactory.Usage(new UsageOutput
        {
            SessionId = SessionId,
            TimestampMs = 15,
            InputTokens = 100,
            OutputTokens = 20,
            TotalTokens = 120,
            CachedInputTokens = 40,
            ReasoningTokens = 8,
            ContextWindowTokens = 1000,
            UsagePercent = 0.1,
            PromptMs = 12,
            PredictedPerSecond = 50
        }, "turn-1");
        var compaction = SessionTranscriptEntryFactory.Compaction(new CompactionOutput
        {
            SessionId = SessionId,
            TimestampMs = 16,
            MessagesBefore = 20,
            MessagesAfter = 6,
            ToolResultsCleared = true,
            Summarized = true,
            ContextWindowTokens = 1000,
            PreCompactionInputTokens = 900,
            KeepCountUsed = 4
        }, "turn-1");

        Assert.Equal("dotnet test", System.Text.Json.JsonDocument.Parse(tool.ArgumentsJson!).RootElement
            .GetProperty("command").GetString());
        Assert.Equal("Passed", tool.Result);
        Assert.Equal("Verify the source tree", tool.Rationale);
        Assert.Equal("run-1", subAgent.RunId);
        Assert.Equal("partial", subAgent.Outcome);
        Assert.Equal("report.txt", file.FileName);
        Assert.Equal(nameof(ErrorCategory.ProviderFailure), error.ErrorCategory);
        Assert.Contains("detail", error.ErrorDetail, StringComparison.Ordinal);
        Assert.Equal(8, usage.ReasoningTokens);
        Assert.Equal(50, usage.PredictedPerSecond);
        Assert.True(compaction.ToolResultsCleared);
        Assert.True(compaction.Summarized);
    }

    [Fact]
    public void Transient_activity_outputs_are_not_session_journal_events()
    {
        var toolActivity = new ToolActivityOutput
        {
            SessionId = SessionId,
            CallId = new ToolCallId("call-1"),
            ToolName = new ToolName("search"),
            TurnId = new TurnId("turn-1"),
            Phase = "running",
            Summary = "private progress"
        };
        var subAgentActivity = new SubAgentOutput
        {
            SessionId = SessionId,
            AgentName = new AgentName("reviewer"),
            Phase = SubAgentPhase.Activity,
            RunId = new SubAgentRunId("run-1"),
            ActivityPhase = "reviewing",
            ActivitySummary = "private progress"
        };

        Assert.IsNotAssignableFrom<ISessionEvent>(toolActivity);
        Assert.IsNotAssignableFrom<ISessionEvent>(subAgentActivity);
    }
}

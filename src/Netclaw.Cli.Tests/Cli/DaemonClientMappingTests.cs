// -----------------------------------------------------------------------
// <copyright file="DaemonClientMappingTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.SubAgents;
using Netclaw.Cli.Daemon;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Cli.Tests.Cli;

public sealed class DaemonClientMappingTests
{
    [Theory]
    [InlineData("session-signalr/abc123", "signalr/abc123")]
    [InlineData("session-C07ABC/1234567890.123456", "C07ABC/1234567890.123456")]
    [InlineData("signalr/no-prefix", "signalr/no-prefix")]
    public void SessionCatalogEntryDto_SessionId_strips_persistence_prefix(
        string persistenceId, string expectedSessionId)
    {
        var dto = new SessionCatalogEntryDto
        {
            PersistenceId = persistenceId,
            Channel = "tui",
            Status = "active",
            TurnCount = 0,
            CreatedAt = 0,
            LastActivity = 0
        };

        Assert.Equal(expectedSessionId, dto.SessionId);
    }

    [Fact]
    public void FromDto_maps_text_delta_output()
    {
        var dto = new SessionOutputDto
        {
            Type = "text_delta",
            SessionId = "signalr/test",
            TimestampMs = 123,
            Text = "hel"
        };

        var output = DaemonClient.FromDto(dto);

        var delta = Assert.IsType<TextDeltaOutput>(output);
        Assert.Equal("signalr/test", delta.SessionId.Value);
        Assert.Equal("hel", delta.Delta);
    }

    [Fact]
    public void FromDto_maps_tool_result_output()
    {
        var dto = new SessionOutputDto
        {
            Type = "tool_result",
            SessionId = "signalr/test",
            TimestampMs = 123,
            CallId = "abc",
            ToolName = "bash",
            Result = "ok"
        };

        var output = DaemonClient.FromDto(dto);

        var result = Assert.IsType<ToolResultOutput>(output);
        Assert.Equal("signalr/test", result.SessionId.Value);
        Assert.Equal("abc", result.CallId.Value);
        Assert.Equal("bash", result.ToolName.Value);
        Assert.Equal("ok", result.Result);
    }

    [Fact]
    public void FromDto_unknown_type_becomes_error_output()
    {
        var dto = new SessionOutputDto
        {
            Type = "mystery",
            SessionId = "signalr/test",
            TimestampMs = 123
        };

        var output = DaemonClient.FromDto(dto);

        var error = Assert.IsType<ErrorOutput>(output);
        Assert.Contains("Unknown output type", error.Message);
        Assert.Equal("signalr/test", error.SessionId.Value);
    }

    [Fact]
    public void FromDto_maps_session_joined_with_recent_messages()
    {
        var dto = new SessionOutputDto
        {
            Type = "session_joined",
            SessionId = "signalr/test",
            TimestampMs = 100,
            Title = "Test Chat",
            TurnCount = 3,
            RecentMessages =
            [
                new ChatMessageDto("user", "Hello"),
                new ChatMessageDto("assistant", "Hi there!")
            ]
        };

        var output = DaemonClient.FromDto(dto);

        var joined = Assert.IsType<SessionJoined>(output);
        Assert.Equal("signalr/test", joined.SessionId.Value);
        Assert.Equal("Test Chat", joined.Title);
        Assert.Equal(3, joined.TurnCount);
        Assert.NotNull(joined.RecentMessages);
        Assert.Equal(2, joined.RecentMessages.Count);
        Assert.Equal("user", joined.RecentMessages[0].Role);
        Assert.Equal("Hello", joined.RecentMessages[0].Content);
        Assert.Equal("assistant", joined.RecentMessages[1].Role);
        Assert.Equal("Hi there!", joined.RecentMessages[1].Content);
    }

    [Fact]
    public void FromDto_maps_session_joined_without_recent_messages()
    {
        var dto = new SessionOutputDto
        {
            Type = "session_joined",
            SessionId = "signalr/test",
            TimestampMs = 100,
            Title = null,
            TurnCount = 0,
            RecentMessages = null
        };

        var output = DaemonClient.FromDto(dto);

        var joined = Assert.IsType<SessionJoined>(output);
        Assert.Equal("signalr/test", joined.SessionId.Value);
        Assert.Null(joined.Title);
        Assert.Equal(0, joined.TurnCount);
        Assert.Null(joined.RecentMessages);
    }

    [Fact]
    public void SubAgentOutput_roundtrips_through_dto_started()
    {
        var original = new SubAgentOutput
        {
            SessionId = new SessionId("signalr/test"),
            TimestampMs = 500,
            AgentName = new AgentName("memory-curator"),
            Phase = SubAgentPhase.Started,
            ToolCount = 5
        };

        var dto = SessionOutputDtoMapper.ToDto(original);
        Assert.Equal("subagent", dto.Type);
        Assert.Equal("memory-curator", dto.AgentName);
        Assert.Equal("started", dto.Phase);
        Assert.Equal(5, dto.ToolCountSub);
        Assert.Null(dto.SubAgentOutcome);
        Assert.Null(dto.SubAgentOutcomeReason);
        Assert.Null(dto.MemoryDecision);

        var roundTripped = DaemonClient.FromDto(dto);
        var result = Assert.IsType<SubAgentOutput>(roundTripped);
        Assert.Equal("memory-curator", result.AgentName.Value);
        Assert.Equal(SubAgentPhase.Started, result.Phase);
        Assert.Equal(5, result.ToolCount);
    }

    [Fact]
    public void SubAgentOutput_roundtrips_through_dto_completed()
    {
        var original = new SubAgentOutput
        {
            SessionId = new SessionId("signalr/test"),
            TimestampMs = 600,
            AgentName = new AgentName("memory-retriever"),
            Phase = SubAgentPhase.Completed,
            Success = true,
            Outcome = SubAgentRunOutcome.Partial,
            OutcomeReason = SubAgentOutcomeReason.ToolIterationBudgetExhausted,
            Duration = TimeSpan.FromSeconds(12.3)
        };

        var dto = SessionOutputDtoMapper.ToDto(original);
        Assert.Equal("subagent", dto.Type);
        Assert.Equal("completed", dto.Phase);
        Assert.True(dto.SubAgentSuccess);
        Assert.Equal("partial", dto.SubAgentOutcome);
        Assert.Equal("tool_iteration_budget_exhausted", dto.SubAgentOutcomeReason);

        var enrichedDto = dto with
        {
            MemoryDecision = "accepted",
            MemoryDecisionReason = null,
            FindingsCount = 2
        };

        var roundTripped = DaemonClient.FromDto(enrichedDto);
        var result = Assert.IsType<SubAgentOutput>(roundTripped);
        Assert.Equal("memory-retriever", result.AgentName.Value);
        Assert.Equal(SubAgentPhase.Completed, result.Phase);
        Assert.True(result.Success);
        Assert.Equal(SubAgentRunOutcome.Partial, result.Outcome);
        Assert.Equal(SubAgentOutcomeReason.ToolIterationBudgetExhausted, result.OutcomeReason);
        Assert.Equal(12300, result.Duration.TotalMilliseconds, 1);
        Assert.Equal("accepted", result.MemoryDecision);
        Assert.Equal(2, result.FindingsCount);
    }

    [Fact]
    public void TextOutput_roundtrips_IsCallBoundary_false_through_dto()
    {
        // F2: the notice emitters (EmitExpiredPromptNotice and siblings) set
        // IsCallBoundary = false on the wire so a resuming daemon client
        // doesn't treat the notice as the live call's completion.
        var original = new TextOutput("That approval prompt has expired.")
        {
            SessionId = new SessionId("signalr/test"),
            TimestampMs = 900,
            IsCallBoundary = false
        };

        var dto = SessionOutputDtoMapper.ToDto(original);
        Assert.Equal(SessionOutputTypes.Text, dto.Type);
        Assert.False(dto.IsCallBoundary);

        var roundTripped = DaemonClient.FromDto(dto);
        var result = Assert.IsType<TextOutput>(roundTripped);
        Assert.False(result.IsCallBoundary);
        Assert.Equal("That approval prompt has expired.", result.Text);
    }

    [Fact]
    public void TextOutput_FromDto_defaults_IsCallBoundary_true_for_older_payloads_without_the_field()
    {
        // A daemon running an older version never sends IsCallBoundary on the
        // wire — the field deserializes as null. It must default to true so
        // every pre-existing TextOutput keeps the call-boundary contract.
        var dto = new SessionOutputDto
        {
            Type = SessionOutputTypes.Text,
            SessionId = "signalr/test",
            TimestampMs = 901,
            Text = "Full answer.",
            IsCallBoundary = null
        };

        var output = DaemonClient.FromDto(dto);
        var result = Assert.IsType<TextOutput>(output);
        Assert.True(result.IsCallBoundary);
    }

    [Theory]
    [InlineData(ErrorCategory.ToolFailure)]
    [InlineData(ErrorCategory.ProviderFailure)]
    [InlineData(ErrorCategory.Timeout)]
    [InlineData(ErrorCategory.Unknown)]
    public void ErrorOutput_roundtrips_correlation_id_and_category_through_dto(ErrorCategory category)
    {
        var correlationId = Guid.NewGuid();
        var original = new ErrorOutput
        {
            SessionId = new SessionId("signalr/test"),
            TimestampMs = 100,
            Message = "Something went wrong.",
            CorrelationId = correlationId,
            Category = category
        };

        var dto = SessionOutputDtoMapper.ToDto(original);

        Assert.Equal("error", dto.Type);
        Assert.Equal(correlationId.ToString("N"), dto.ErrorCorrelationId);
        Assert.Equal(category.ToString(), dto.ErrorCategory);

        var roundTripped = DaemonClient.FromDto(dto);
        var result = Assert.IsType<ErrorOutput>(roundTripped);
        Assert.Equal(correlationId, result.CorrelationId);
        Assert.Equal(category, result.Category);
        Assert.Equal("Something went wrong.", result.Message);
    }

    [Fact]
    public void BufferFlush_roundtrips_through_dto()
    {
        var original = new BufferFlush
        {
            SessionId = new SessionId("signalr/test"),
            TimestampMs = 777
        };

        var dto = SessionOutputDtoMapper.ToDto(original);
        Assert.Equal("buffer_flush", dto.Type);
        Assert.Equal("signalr/test", dto.SessionId);
        Assert.Equal(777, dto.TimestampMs);

        var roundTripped = DaemonClient.FromDto(dto);
        var result = Assert.IsType<BufferFlush>(roundTripped);
        Assert.Equal("signalr/test", result.SessionId.Value);
        Assert.Equal(777, result.TimestampMs);
    }

    [Fact]
    public void TextStreamDiscarded_roundtrips_through_dto()
    {
        var original = new TextStreamDiscarded
        {
            SessionId = new SessionId("signalr/test"),
            TimestampMs = 779
        };

        var dto = SessionOutputDtoMapper.ToDto(original);
        Assert.Equal(SessionOutputTypes.TextStreamDiscarded, dto.Type);
        Assert.Equal("signalr/test", dto.SessionId);
        Assert.Equal(779, dto.TimestampMs);

        var roundTripped = DaemonClient.FromDto(dto);
        var result = Assert.IsType<TextStreamDiscarded>(roundTripped);
        Assert.Equal("signalr/test", result.SessionId.Value);
        Assert.Equal(779, result.TimestampMs);
    }

    [Fact]
    public void UsageOutput_roundtrips_discarded_resume_fields_through_dto()
    {
        var original = new UsageOutput
        {
            SessionId = new SessionId("signalr/test"),
            TimestampMs = 780,
            InputTokens = 100,
            DiscardedResumeEstimatedInputTokens = 42,
            DiscardedResumeAttempts = 2
        };

        var dto = SessionOutputDtoMapper.ToDto(original);
        Assert.Equal(42, dto.DiscardedResumeEstimatedInputTokens);
        Assert.Equal(2, dto.DiscardedResumeAttempts);

        var roundTripped = DaemonClient.FromDto(dto);
        var result = Assert.IsType<UsageOutput>(roundTripped);
        Assert.Equal(42, result.DiscardedResumeEstimatedInputTokens);
        Assert.Equal(2, result.DiscardedResumeAttempts);
    }

    [Fact]
    public void ProcessingStateOutput_roundtrips_through_dto()
    {
        var original = new ProcessingStateOutput(true)
        {
            SessionId = new SessionId("signalr/test"),
            TimestampMs = 778,
            IsRequired = true
        };

        var dto = SessionOutputDtoMapper.ToDto(original);
        Assert.Equal(SessionOutputTypes.ProcessingState, dto.Type);
        Assert.True(dto.IsProcessing);
        Assert.True(dto.ProcessingStateRequired);

        var roundTripped = DaemonClient.FromDto(dto);
        var result = Assert.IsType<ProcessingStateOutput>(roundTripped);
        Assert.Equal("signalr/test", result.SessionId.Value);
        Assert.Equal(778, result.TimestampMs);
        Assert.True(result.IsProcessing);
        Assert.True(result.IsRequired);
    }

    [Fact]
    public void ToolInteractionRequest_roundtrips_through_dto()
    {
        var original = new ToolInteractionRequest
        {
            SessionId = new SessionId("signalr/test"),
            TimestampMs = 888,
            Kind = "approval",
            CallId = new Netclaw.Tools.ToolCallId("call-1"),
            ToolName = new Netclaw.Tools.ToolName("shell_execute"),
            DisplayText = "git push origin main",
            RequesterSenderId = new SenderId("device-1"),
            HasAdoptedContext = true,
            HasThirdPartyAdoptedContext = true,
            AdoptedSpeakerIds = ["device-1", "device-2"],
            Patterns = ["git push"],
            CandidateVerbs = ["git push"],
            Options =
            [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveSessionKey, ApprovalOptionKeys.ApproveSessionLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveAlwaysKey, ApprovalOptionKeys.ApproveAlwaysLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveEverywhereKey, ApprovalOptionKeys.ApproveEverywhereLabel),
                new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
            ]
        };

        var dto = SessionOutputDtoMapper.ToDto(original);
        Assert.Equal(SessionOutputTypes.ToolInteraction, dto.Type);
        Assert.Equal("approval", dto.InteractionKind);
        Assert.Equal("git push origin main", dto.InteractionDisplayText);
        Assert.Equal("device-1", dto.RequesterSenderId);
        Assert.True(dto.InteractionHasAdoptedContext);
        Assert.True(dto.InteractionHasThirdPartyAdoptedContext);
        Assert.Equal(["device-1", "device-2"], dto.InteractionAdoptedSpeakerIds);
        Assert.Equal(["git push"], dto.InteractionCandidateVerbs);
        Assert.Equal(5, dto.InteractionOptions!.Count);

        var roundTripped = DaemonClient.FromDto(dto);
        var result = Assert.IsType<ToolInteractionRequest>(roundTripped);
        Assert.Equal("call-1", result.CallId.Value);
        Assert.Equal("shell_execute", result.ToolName.Value);
        Assert.Equal("git push origin main", result.DisplayText);
        Assert.Equal("device-1", result.RequesterSenderId?.Value);
        Assert.True(result.HasAdoptedContext);
        Assert.True(result.HasThirdPartyAdoptedContext);
        Assert.Equal(["device-1", "device-2"], result.AdoptedSpeakerIds);
        Assert.Equal(["git push"], result.Patterns);
        Assert.Equal(["git push"], result.CandidateVerbs);
        Assert.Equal(5, result.Options.Count);
    }

    [Fact]
    public void ErrorOutput_defaults_to_unknown_category_when_dto_field_missing()
    {
        var dto = new SessionOutputDto
        {
            Type = "error",
            SessionId = "signalr/test",
            TimestampMs = 100,
            ErrorMessage = "Daemon error"
            // ErrorCategory and ErrorCorrelationId intentionally absent
        };

        var output = DaemonClient.FromDto(dto);

        var error = Assert.IsType<ErrorOutput>(output);
        Assert.Equal(ErrorCategory.Unknown, error.Category);
        Assert.NotEqual(Guid.Empty, error.CorrelationId);
    }

    [Fact]
    public void SessionOutputDto_turn_number_serializes_as_bare_json_integer()
    {
        // Pass 7d wraps SessionOutputDto.TurnNumber in the TurnNumber value
        // object. The SignalR JSON wire form must stay a bare integer (never a
        // nested { "Value": N } object) so an old/new daemon and CLI interop.
        var dto = new SessionOutputDto
        {
            Type = SessionOutputTypes.TurnCompleted,
            SessionId = "signalr/test",
            TimestampMs = 1,
            TurnNumber = new TurnNumber(9)
        };

        var json = System.Text.Json.JsonSerializer.Serialize(dto);
        Assert.Contains("\"TurnNumber\":9", json);
        Assert.DoesNotContain("\"Value\"", json);

        var restored = System.Text.Json.JsonSerializer.Deserialize<SessionOutputDto>(json);
        Assert.Equal(new TurnNumber(9), restored!.TurnNumber);
    }

    [Fact]
    public void SessionOutputDto_null_turn_number_serializes_as_json_null()
    {
        var dto = new SessionOutputDto
        {
            Type = SessionOutputTypes.Text,
            SessionId = "signalr/test",
            TimestampMs = 1,
            TurnNumber = null
        };

        var json = System.Text.Json.JsonSerializer.Serialize(dto);
        Assert.Contains("\"TurnNumber\":null", json);

        var restored = System.Text.Json.JsonSerializer.Deserialize<SessionOutputDto>(json);
        Assert.Null(restored!.TurnNumber);
    }

    [Fact]
    public void ToolInteractionOption_key_serializes_as_bare_json_string()
    {
        // ToolInteractionOption.Key crosses the SignalR JSON boundary nested in
        // SessionOutputDto.InteractionOptions. The ApprovalOptionKey converter
        // must keep it a bare string so channel adapters parse it unchanged.
        var option = new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, "Once");

        var json = System.Text.Json.JsonSerializer.Serialize(option);
        Assert.Contains("\"Key\":\"approve_once\"", json);
        Assert.DoesNotContain("\"Value\"", json);

        var restored = System.Text.Json.JsonSerializer.Deserialize<ToolInteractionOption>(json);
        Assert.Equal(ApprovalOptionKeys.ApproveOnceKey, restored!.Key);
    }
}

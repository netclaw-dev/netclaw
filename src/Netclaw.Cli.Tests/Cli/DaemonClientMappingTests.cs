// -----------------------------------------------------------------------
// <copyright file="DaemonClientMappingTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.SubAgents;
using Netclaw.Cli.Daemon;
using Xunit;

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
        Assert.Equal("abc", result.CallId);
        Assert.Equal("bash", result.ToolName);
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
                new ChatMessageDto { Role = "user", Content = "Hello" },
                new ChatMessageDto { Role = "assistant", Content = "Hi there!" }
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
            AgentName = "memory-curator",
            Phase = SubAgentPhase.Started,
            ToolCount = 5
        };

        var dto = SessionOutputDtoMapper.ToDto(original);
        Assert.Equal("subagent", dto.Type);
        Assert.Equal("memory-curator", dto.AgentName);
        Assert.Equal("started", dto.Phase);
        Assert.Equal(5, dto.ToolCountSub);
        Assert.Null(dto.MemoryDecision);

        var roundTripped = DaemonClient.FromDto(dto);
        var result = Assert.IsType<SubAgentOutput>(roundTripped);
        Assert.Equal("memory-curator", result.AgentName);
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
            AgentName = "memory-retriever",
            Phase = SubAgentPhase.Completed,
            Success = true,
            Duration = TimeSpan.FromSeconds(12.3)
        };

        var dto = SessionOutputDtoMapper.ToDto(original);
        Assert.Equal("subagent", dto.Type);
        Assert.Equal("completed", dto.Phase);
        Assert.True(dto.SubAgentSuccess);

        var enrichedDto = dto with
        {
            MemoryDecision = "accepted",
            MemoryDecisionReason = null,
            FindingsCount = 2
        };

        var roundTripped = DaemonClient.FromDto(enrichedDto);
        var result = Assert.IsType<SubAgentOutput>(roundTripped);
        Assert.Equal("memory-retriever", result.AgentName);
        Assert.Equal(SubAgentPhase.Completed, result.Phase);
        Assert.True(result.Success);
        Assert.Equal(12300, result.Duration.TotalMilliseconds, 1);
        Assert.Equal("accepted", result.MemoryDecision);
        Assert.Equal(2, result.FindingsCount);
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
    public void ToolInteractionRequest_roundtrips_through_dto()
    {
        var original = new ToolInteractionRequest
        {
            SessionId = new SessionId("signalr/test"),
            TimestampMs = 888,
            Kind = "approval",
            CallId = "call-1",
            ToolName = "shell_execute",
            DisplayText = "git push origin main",
            RequesterSenderId = "device-1",
            HasAdoptedContext = true,
            HasThirdPartyAdoptedContext = true,
            AdoptedSpeakerIds = ["device-1", "device-2"],
            Patterns = ["git push"],
            ApprovalEntries = ["git push"],
            DirectoryRoots = [],
            Options =
            [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveSession, ApprovalOptionKeys.ApproveSessionLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveAlways, ApprovalOptionKeys.ApproveAlwaysLabel),
                new ToolInteractionOption(ApprovalOptionKeys.Deny, ApprovalOptionKeys.DenyLabel)
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
        Assert.Equal(["git push"], dto.InteractionApprovalEntries);
        Assert.Equal([], dto.InteractionDirectoryRoots ?? []);

        var roundTripped = DaemonClient.FromDto(dto);
        var result = Assert.IsType<ToolInteractionRequest>(roundTripped);
        Assert.Equal("call-1", result.CallId);
        Assert.Equal("shell_execute", result.ToolName);
        Assert.Equal("git push origin main", result.DisplayText);
        Assert.Equal("device-1", result.RequesterSenderId);
        Assert.True(result.HasAdoptedContext);
        Assert.True(result.HasThirdPartyAdoptedContext);
        Assert.Equal(["device-1", "device-2"], result.AdoptedSpeakerIds);
        Assert.Equal(["git push"], result.Patterns);
        Assert.Equal(["git push"], result.ApprovalEntries);
        Assert.Equal([], result.DirectoryRoots);
        Assert.Equal(4, result.Options.Count);
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
}

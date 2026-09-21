// -----------------------------------------------------------------------
// <copyright file="TeamsSdkReplyClientTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Teams;
using Netclaw.Daemon.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class TeamsSdkReplyClientTests
{
    [Fact]
    public async Task Reply_client_maps_create_and_update_success_without_exposing_sdk_data()
    {
        var operations = new FakeOperations("created", "updated");
        var client = CreateClient(operations);

        var created = await client.DeliverAsync(CreateMessage(), TestContext.Current.CancellationToken);
        var updated = await client.DeliverAsync(CreateMessage(updateActivityId: "processing"), TestContext.Current.CancellationToken);

        Assert.Equal(TeamsDeliveryStatus.Delivered, created.Status);
        Assert.Equal("created", created.ActivityId);
        Assert.Equal(TeamsDeliveryStatus.Updated, updated.Status);
        Assert.Equal("updated", updated.ActivityId);
        Assert.Equal(2, operations.Requests.Count);
    }

    [Fact]
    public async Task Reply_client_sends_native_typing_without_requiring_a_message_activity_id()
    {
        var operations = new FakeOperations();
        var client = CreateClient(operations);
        var destination = CreateMessage().Destination;

        var result = await client.SendTypingAsync(destination, TestContext.Current.CancellationToken);

        Assert.Equal(TeamsDeliveryStatus.Delivered, result.Status);
        Assert.Null(result.ActivityId);
        Assert.Equal(destination, Assert.Single(operations.TypingDestinations));
    }

    [Fact]
    public async Task Reply_client_maps_typing_transport_failures_to_safe_results()
    {
        var operations = new FakeOperations { TypingOutcome = "unavailable" };
        var client = CreateClient(operations);

        var result = await client.SendTypingAsync(CreateMessage().Destination, TestContext.Current.CancellationToken);

        Assert.Equal(TeamsDeliveryStatus.Unavailable, result.Status);
        Assert.Equal("sdk_unavailable", result.ReasonCode);
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("unauthorized")]
    public async Task Reply_client_maps_transport_failures_to_safe_unavailable_results(string failure)
    {
        var client = CreateClient(new FakeOperations(failure));

        var result = await client.DeliverAsync(CreateMessage(), TestContext.Current.CancellationToken);

        Assert.Equal(TeamsDeliveryStatus.Unavailable, result.Status);
        Assert.DoesNotContain("secret", result.ReasonCode, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenant", result.ReasonCode, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reply_client_maps_message_size_failures_without_the_response_body()
    {
        var client = CreateClient(new FakeOperations("too-large"));

        var result = await client.DeliverAsync(CreateMessage(), TestContext.Current.CancellationToken);

        Assert.Equal(TeamsDeliveryStatus.RejectedTooLarge, result.Status);
        Assert.Equal("output_too_large", result.ReasonCode);
    }

    [Fact]
    public async Task Reply_client_maps_a_missing_destination_to_a_permanent_safe_result()
    {
        var result = await CreateClient(new FakeOperations("missing-destination"))
            .DeliverAsync(CreateMessage(), TestContext.Current.CancellationToken);

        Assert.Equal(TeamsDeliveryStatus.InvalidDestination, result.Status);
        Assert.Equal("sdk_destination_invalid", result.ReasonCode);
    }

    [Fact]
    public async Task Reply_client_maps_cancellation_and_sanitizes_unknown_exceptions()
    {
        var cancelled = await CreateClient(new FakeOperations("cancelled"))
            .DeliverAsync(CreateMessage(), TestContext.Current.CancellationToken);
        var failure = await CreateClient(new FakeOperations("secret"))
            .DeliverAsync(CreateMessage(), TestContext.Current.CancellationToken);

        Assert.Equal(TeamsDeliveryStatus.Cancelled, cancelled.Status);
        Assert.Equal("cancelled", cancelled.ReasonCode);
        Assert.Equal(TeamsDeliveryStatus.Failed, failure.Status);
        Assert.Equal("sdk_delivery_failed", failure.ReasonCode);
        Assert.DoesNotContain("secret", failure.ReasonCode, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Approval_card_payload_preserves_the_canonical_button_styles()
    {
        var request = new ToolInteractionRequest
        {
            SessionId = new SessionId("teams~tenant~personal~conversation/conversation"),
            Kind = "approval",
            CallId = new ToolCallId("call-approval"),
            ToolName = new ToolName("shell_execute"),
            DisplayText = "rmdir /home/sto/.netclaw/workspaces/testapproval",
            Options =
            [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveSessionKey, ApprovalOptionKeys.ApproveSessionLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveAlwaysKey, ApprovalOptionKeys.ApproveAlwaysLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveEverywhereKey, ApprovalOptionKeys.ApproveEverywhereLabel),
                new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
            ]
        };

        var approvalCard = TeamsApprovalCardRenderer.CreatePending(request, "correlation_123", "nonce_123");
        var payload = TeamsAdaptiveCardPayloadBuilder.Create(approvalCard);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var actions = document.RootElement.GetProperty("actions").EnumerateArray().ToArray();

        Assert.Equal(request.Options.Select(option => option.Label), actions.Select(action => action.GetProperty("title").GetString()));
        Assert.Equal(
            ["positive", "default", "default", "default", "destructive"],
            actions.Select(action => action.GetProperty("style").GetString()));
        Assert.All(actions, action =>
        {
            Assert.Equal("Action.Execute", action.GetProperty("type").GetString());
            Assert.Equal("netclaw-approval", action.GetProperty("verb").GetString());
            var data = action.GetProperty("data");
            Assert.True(data.TryGetProperty("correlation", out _));
            Assert.True(data.TryGetProperty("nonce", out _));
            Assert.True(data.TryGetProperty("action", out _));
            Assert.Equal(3, data.EnumerateObject().Count());
        });

        Assert.Equal("1.5", document.RootElement.GetProperty("version").GetString());
        Assert.Equal("Approval required for a Netclaw tool operation.", document.RootElement.GetProperty("speak").GetString());
        var body = document.RootElement.GetProperty("body");
        Assert.Equal("ColumnSet", body[0].GetProperty("type").GetString());
        Assert.Equal("ShieldLock", body[0].GetProperty("columns")[0].GetProperty("items")[0].GetProperty("name").GetString());
        Assert.Equal("Approval Required", body[0].GetProperty("columns")[1].GetProperty("items")[0].GetProperty("text").GetString());
        Assert.Equal("NETCLAW SECURITY CONTROL", body[0].GetProperty("columns")[1].GetProperty("items")[1].GetProperty("text").GetString());
        Assert.Equal("Container", body[1].GetProperty("type").GetString());
        Assert.Equal("Accent", body[1].GetProperty("style").GetString());
        Assert.Equal("Table", body[2].GetProperty("type").GetString());
        Assert.False(body[2].GetProperty("firstRowAsHeader").GetBoolean());
        Assert.False(body[2].TryGetProperty("firstRowAsHeaders", out _));
        var rows = body[2].GetProperty("rows");
        Assert.Equal("Tool", rows[0].GetProperty("cells")[0].GetProperty("items")[0].GetProperty("text").GetString());
        Assert.Equal("shell_execute", rows[0].GetProperty("cells")[1].GetProperty("items")[0].GetProperty("text").GetString());
        Assert.Equal("Command", rows[1].GetProperty("cells")[0].GetProperty("items")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void Pending_mcp_approval_card_uses_the_actual_teams_sdk_activity_serializer()
    {
        var request = new ToolInteractionRequest
        {
            SessionId = new SessionId("teams~tenant~personal~conversation/conversation"),
            Kind = "approval",
            CallId = new ToolCallId("call-helpdesk-capabilities"),
            ToolName = new ToolName("helpdesk-dev/helpdesk_capabilities"),
            DisplayText = "Inspect the available helpdesk development capabilities.",
            Options =
            [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveSessionKey, ApprovalOptionKeys.ApproveSessionLabel),
                new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
            ]
        };
        var card = TeamsApprovalCardRenderer.CreatePending(request, "correlation_123", "nonce_123");

        var activity = TeamsSdkActivityFactory.CreateMessage(CreateMessage(approvalCard: card));
        using var document = JsonDocument.Parse(activity.ToJson());

        Assert.Equal("message", document.RootElement.GetProperty("type").GetString());
        Assert.False(document.RootElement.TryGetProperty("text", out _));
        var attachment = Assert.Single(document.RootElement.GetProperty("attachments").EnumerateArray());
        Assert.Equal("application/vnd.microsoft.card.adaptive", attachment.GetProperty("contentType").GetString());
        var payload = attachment.GetProperty("content");
        Assert.Equal("1.5", payload.GetProperty("version").GetString());
        Assert.Equal("Action.Execute", payload.GetProperty("actions")[0].GetProperty("type").GetString());
        Assert.Equal("netclaw-approval", payload.GetProperty("actions")[0].GetProperty("verb").GetString());
        Assert.Equal(
            request.Options.Select(option => option.Key.Value),
            payload.GetProperty("actions").EnumerateArray()
                .Select(action => action.GetProperty("data").GetProperty("action").GetString()));

        var body = payload.GetProperty("body");
        var icon = body[0].GetProperty("columns")[0].GetProperty("items")[0];
        Assert.Equal("ShieldLock", icon.GetProperty("name").GetString());
        Assert.Equal("Accent", icon.GetProperty("color").GetString());
        Assert.Equal("Large", icon.GetProperty("size").GetString());
        Assert.Equal("Regular", icon.GetProperty("style").GetString());
        Assert.Equal("Accent", body[1].GetProperty("style").GetString());
        Assert.Equal("Accent", body[1].GetProperty("items")[0].GetProperty("color").GetString());
        Assert.Equal("Default", body[2].GetProperty("gridStyle").GetString());
        Assert.False(body[2].GetProperty("firstRowAsHeader").GetBoolean());
        Assert.DoesNotContain("correlation_123", payload.GetProperty("speak").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("nonce_123", payload.GetProperty("speak").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_text_and_typing_activities_keep_their_native_teams_serialization_shape()
    {
        var textActivity = TeamsSdkActivityFactory.CreateMessage(CreateMessage());
        var typingActivity = TeamsSdkActivityFactory.CreateTyping(CreateMessage().Destination);

        using var textDocument = JsonDocument.Parse(textActivity.ToJson());
        using var typingDocument = JsonDocument.Parse(typingActivity.ToJson());

        Assert.Equal("reply", textDocument.RootElement.GetProperty("text").GetString());
        Assert.False(textDocument.RootElement.TryGetProperty("attachments", out _));
        Assert.Equal("typing", typingDocument.RootElement.GetProperty("type").GetString());
        Assert.False(typingDocument.RootElement.TryGetProperty("replyToId", out _));
    }

    [Theory]
    [InlineData("approval_payload_build_failed")]
    [InlineData("approval_activity_build_failed")]
    [InlineData("approval_activity_serialize_failed")]
    [InlineData("sdk_create_failed")]
    [InlineData("sdk_reply_failed")]
    [InlineData("sdk_update_failed")]
    public async Task Reply_client_maps_staged_delivery_failures_to_safe_diagnostics(string failureCode)
    {
        var logger = new RecordingLogger<TeamsSdkReplyClient>();
        var client = new TeamsSdkReplyClient(new FakeOperations(failureCode), logger);

        var result = await client.DeliverAsync(CreateMessage(), TestContext.Current.CancellationToken);

        Assert.Equal(TeamsDeliveryStatus.Failed, result.Status);
        Assert.Equal(failureCode, result.ReasonCode);
        var diagnostic = Assert.Single(logger.Entries);
        Assert.Contains(failureCode, diagnostic, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenant", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("service", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Terminal_denial_card_payload_has_no_actions_and_uses_attention_tone()
    {
        var card = TeamsApprovalCardRenderer.CreateDenied(
            "shell_execute",
            "rmdir netclaw-approval-card-never-created",
            DateTimeOffset.Parse("2026-08-25T15:25:00Z"));
        var payload = TeamsAdaptiveCardPayloadBuilder.Create(card);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));

        var body = document.RootElement.GetProperty("body");
        Assert.Equal("ShieldDismiss", body[0].GetProperty("columns")[0].GetProperty("items")[0].GetProperty("name").GetString());
        Assert.Equal("Approval Denied", body[0].GetProperty("columns")[1].GetProperty("items")[0].GetProperty("text").GetString());
        Assert.Equal("Attention", body[1].GetProperty("style").GetString());
        Assert.Equal("Table", body[2].GetProperty("type").GetString());
        Assert.Equal("STATUS: EXECUTION BLOCKED", body[3].GetProperty("items")[0].GetProperty("text").GetString());
        Assert.Empty(document.RootElement.GetProperty("actions").EnumerateArray());
    }

    [Fact]
    public void Elevated_granted_and_expired_payloads_keep_the_modern_terminal_visual_language()
    {
        var request = new ToolInteractionRequest
        {
            SessionId = new SessionId("teams~tenant~personal~conversation/conversation"),
            Kind = "approval",
            CallId = new ToolCallId("call-visual-variants"),
            ToolName = new ToolName("shell_execute"),
            DisplayText = "remove temporary artefacts",
            Options = [new ToolInteractionOption(ApprovalOptionKeys.DenyKey, "Deny")]
        };
        var cards = new[]
        {
            TeamsApprovalCardRenderer.CreateElevatedPending(
                request,
                "correlation_123",
                "nonce_123",
                "HIGH",
                "May permanently delete files."),
            TeamsApprovalCardRenderer.CreateGranted(
                "shell_execute",
                "git status",
                ApprovalOptionKeys.ApproveSession,
                DateTimeOffset.Parse("2026-08-25T15:25:00Z")),
            TeamsApprovalCardRenderer.CreateExpired(
                "shell_execute",
                "git status",
                DateTimeOffset.Parse("2026-08-25T15:25:00Z"))
        };

        var expected = new[]
        {
            ("Warning", "Elevated Approval Required", "Warning", false),
            ("ShieldCheckmark", "Approval Granted", "Good", true),
            ("ClockDismiss", "Approval Card Expired", "Warning", true)
        };

        for (var index = 0; index < cards.Length; index++)
        {
            var payload = TeamsAdaptiveCardPayloadBuilder.Create(cards[index]);
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
            var body = document.RootElement.GetProperty("body");

            Assert.Equal(expected[index].Item1, body[0].GetProperty("columns")[0].GetProperty("items")[0].GetProperty("name").GetString());
            Assert.Equal(expected[index].Item2, body[0].GetProperty("columns")[1].GetProperty("items")[0].GetProperty("text").GetString());
            Assert.Equal(expected[index].Item3, body[1].GetProperty("style").GetString());
            Assert.Equal("Table", body[2].GetProperty("type").GetString());
            Assert.Equal(expected[index].Item4, !document.RootElement.GetProperty("actions").EnumerateArray().Any());
        }
    }

    [Fact]
    public void Approval_card_payload_preserves_bounded_unicode_display_text_without_changing_callback_values()
    {
        var request = new ToolInteractionRequest
        {
            SessionId = new SessionId("teams~tenant~personal~conversation/conversation"),
            Kind = "approval",
            CallId = new ToolCallId("call-unicode"),
            ToolName = new ToolName("filesystem/read_file"),
            DisplayText = "Read \"Résumé 📄\" from /work/λ",
            Options =
            [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, "Approve once ✓"),
                new ToolInteractionOption(ApprovalOptionKeys.DenyKey, "Deny")
            ]
        };

        var card = TeamsApprovalCardRenderer.CreatePending(request, "correlation_123", "nonce_123");
        var payload = TeamsAdaptiveCardPayloadBuilder.Create(card);
        var serialized = JsonSerializer.SerializeToUtf8Bytes(payload);
        using var document = JsonDocument.Parse(serialized);

        var rows = document.RootElement.GetProperty("body")[2].GetProperty("rows");
        Assert.Equal("Read \"Résumé 📄\" from /work/λ", rows[1].GetProperty("cells")[1].GetProperty("items")[0].GetProperty("text").GetString());
        var execute = document.RootElement.GetProperty("actions")[0];
        Assert.Equal("approve_once", execute.GetProperty("data").GetProperty("action").GetString());
        Assert.Equal("nonce_123", execute.GetProperty("data").GetProperty("nonce").GetString());
        Assert.True(serialized.Length <= TeamsApprovalCard.MaxSerializedBytes);
    }

    private static TeamsSdkReplyClient CreateClient(ITeamsSdkReplyOperations operations) =>
        new(operations, NullLogger<TeamsSdkReplyClient>.Instance);

    private static TeamsOutboundMessage CreateMessage(
        string? updateActivityId = null,
        TeamsApprovalCard? approvalCard = null) => new(
        new TeamsOutboundDestination(
            "tenant",
            "conversation",
            TeamsConversationScope.Personal,
            "https://service.invalid/",
            userId: "user"),
        "reply",
        "idempotency",
        "correlation",
        updateActivityId: updateActivityId,
        approvalCard: approvalCard);

    private sealed class FakeOperations(params string[] outcomes) : ITeamsSdkReplyOperations
    {
        private readonly Queue<string> _outcomes = new(outcomes);

        public List<TeamsOutboundMessage> Requests { get; } = [];

        public List<TeamsOutboundDestination> TypingDestinations { get; } = [];

        public string TypingOutcome { get; init; } = "delivered";

        public Task<string?> DeliverAsync(TeamsOutboundMessage message, CancellationToken cancellationToken)
        {
            Requests.Add(message);
            var outcome = _outcomes.Dequeue();
            return outcome switch
            {
                "unavailable" => Task.FromException<string?>(new HttpRequestException("transport unavailable")),
                "unauthorized" => Task.FromException<string?>(new UnauthorizedAccessException("credential failure")),
                "too-large" => Task.FromException<string?>(new HttpRequestException("MessageSizeTooBig", null, HttpStatusCode.RequestEntityTooLarge)),
                "missing-destination" => Task.FromException<string?>(new HttpRequestException("gone", null, HttpStatusCode.Gone)),
                "cancelled" => Task.FromException<string?>(new OperationCanceledException()),
                "secret" => Task.FromException<string?>(new InvalidOperationException("secret tenant service URL body")),
                "approval_payload_build_failed" or "approval_activity_build_failed" or "approval_activity_serialize_failed"
                    or "sdk_create_failed" or "sdk_reply_failed" or "sdk_update_failed"
                    => Task.FromException<string?>(new TeamsSdkDeliveryException(
                        outcome,
                        new InvalidOperationException("secret tenant service URL body"))),
                _ => Task.FromResult<string?>(outcome)
            };
        }

        public Task SendTypingAsync(TeamsOutboundDestination destination, CancellationToken cancellationToken)
        {
            TypingDestinations.Add(destination);
            return TypingOutcome switch
            {
                "unavailable" => Task.FromException(new HttpRequestException("transport unavailable")),
                "unauthorized" => Task.FromException(new UnauthorizedAccessException("credential failure")),
                "cancelled" => Task.FromException(new OperationCanceledException()),
                "secret" => Task.FromException(new InvalidOperationException("secret tenant service URL body")),
                _ => Task.CompletedTask
            };
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(formatter(state, exception));
        }
    }
}

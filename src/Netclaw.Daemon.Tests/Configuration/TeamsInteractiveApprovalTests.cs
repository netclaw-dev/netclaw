// -----------------------------------------------------------------------
// <copyright file="TeamsInteractiveApprovalTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Actors.Protocol;
using Netclaw.Channels;
using Netclaw.Channels.Teams;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class TeamsInteractiveApprovalTests
{
    [Fact]
    public void Pending_card_preserves_the_session_option_order_labels_and_safe_context()
    {
        var request = new ToolInteractionRequest
        {
            SessionId = new SessionId("teams~dGVuYW50~personal~Y29udmVyc2F0aW9u/conversation"),
            Kind = "approval",
            CallId = new ToolCallId("call-1"),
            ToolName = new ToolName("execute_shell"),
            DisplayText = "git push origin main",
            CandidateVerbs = ["git push", "git status"],
            Cwd = "/work/netclaw",
            HasAdoptedContext = true,
            AdoptedSpeakerIds = ["user-a", "user-b"],
            Options =
            [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveSessionKey, ApprovalOptionKeys.ApproveSessionLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveAlwaysKey, ApprovalOptionKeys.ApproveAlwaysLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveEverywhereKey, ApprovalOptionKeys.ApproveEverywhereLabel),
                new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
            ]
        };

        var card = TeamsApprovalCardRenderer.CreatePending(request, "correlation_123", "nonce_123");

        Assert.Equal("Approval Required", card.Title);
        Assert.Equal(TeamsApprovalCardTone.Accent, card.Tone);
        Assert.Equal("ShieldLock", card.IconName);
        Assert.Equal("Netclaw wants to run a command or tool operation.", card.Banner);
        Assert.Equal(request.Options.Select(option => option.Key.Value), card.Actions.Select(action => action.Action));
        Assert.Equal(request.Options.Select(option => option.Label), card.Actions.Select(action => action.Title));
        Assert.Equal(
            [
                TeamsApprovalActionStyle.Positive,
                TeamsApprovalActionStyle.Default,
                TeamsApprovalActionStyle.Default,
                TeamsApprovalActionStyle.Default,
                TeamsApprovalActionStyle.Destructive
            ],
            card.Actions.Select(action => action.Style));
        Assert.Contains("Tool: execute_shell", card.Body, StringComparison.Ordinal);
        Assert.Contains("Request: git push origin main", card.Body, StringComparison.Ordinal);
        Assert.Contains("Candidates: git push, git status", card.Body, StringComparison.Ordinal);
        Assert.Contains("Working Directory: /work/netclaw", card.Body, StringComparison.Ordinal);
        Assert.Contains("Adopted context: present.", card.Body, StringComparison.Ordinal);
        Assert.Contains("Speakers: user-a, user-b", card.Body, StringComparison.Ordinal);
        Assert.Equal(
            [
                new TeamsApprovalCardField("Tool", "execute_shell"),
                new TeamsApprovalCardField("Request", "git push origin main"),
                new TeamsApprovalCardField("Candidates", "git push, git status"),
                new TeamsApprovalCardField("Working Directory", "/work/netclaw")
            ],
            card.Fields);
        Assert.Equal("Adopted context: present.\nSpeakers: user-a, user-b", card.Summary);
        Assert.DoesNotContain(card.Fields, static field => field.Label == "Risk Level");
        Assert.All(card.Actions, action =>
        {
            Assert.Equal("correlation_123", action.CorrelationId);
            Assert.Equal("nonce_123", action.Nonce);
            var serializedAction = JsonSerializer.Serialize(action);
            Assert.DoesNotContain(request.CallId.Value, serializedAction, StringComparison.Ordinal);
            Assert.DoesNotContain(request.DisplayText, serializedAction, StringComparison.Ordinal);
            Assert.DoesNotContain(request.ToolName.Value, serializedAction, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Pending_card_uses_the_supplied_mcp_scope_label()
    {
        var request = new ToolInteractionRequest
        {
            SessionId = new SessionId("teams~dGVuYW50~personal~Y29udmVyc2F0aW9u/conversation"),
            Kind = "approval",
            CallId = new ToolCallId("call-mcp"),
            ToolName = new ToolName("filesystem/read_file"),
            DisplayText = "Read README.md",
            Options =
            [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveEverywhereKey, ApprovalOptionKeys.ApproveMcpToolLabel),
                new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
            ]
        };

        var card = TeamsApprovalCardRenderer.CreatePending(request, "correlation_123", "nonce_123");

        Assert.Equal("Approval Required", card.Title);
        Assert.Equal(
            [ApprovalOptionKeys.ApproveOnceLabel, ApprovalOptionKeys.ApproveMcpToolLabel, ApprovalOptionKeys.DenyLabel],
            card.Actions.Select(action => action.Title));
        Assert.Equal(
            [ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.ApproveEverywhere, ApprovalOptionKeys.Deny],
            TeamsApprovalCardRenderer.GetOfferedOptionKeys(request));
        Assert.Equal(
            [
                TeamsApprovalActionStyle.Positive,
                TeamsApprovalActionStyle.Default,
                TeamsApprovalActionStyle.Destructive
            ],
            card.Actions.Select(action => action.Style));
    }

    [Fact]
    public void Pending_card_rejects_duplicate_or_invalid_option_keys()
    {
        var duplicate = CreateRequest(
            new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
            new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, "Once again"));
        var invalid = CreateRequest(new ToolInteractionOption(new ApprovalOptionKey("approve now"), "Approve"));

        Assert.Throws<ArgumentException>(() => TeamsApprovalCardRenderer.CreatePending(duplicate, "correlation_123", "nonce_123"));
        Assert.Throws<ArgumentException>(() => TeamsApprovalCardRenderer.CreatePending(invalid, "correlation_123", "nonce_123"));
    }

    [Fact]
    public void Pending_card_accepts_only_the_bounded_maximum_option_count()
    {
        var maximum = Enumerable.Range(1, TeamsApprovalCardRenderer.MaxOptionCount)
            .Select(index => new ToolInteractionOption(new ApprovalOptionKey($"option_{index}"), $"Option {index}"))
            .ToArray();
        var tooMany = maximum.Append(new ToolInteractionOption(new ApprovalOptionKey("option_extra"), "Option extra")).ToArray();

        var card = TeamsApprovalCardRenderer.CreatePending(CreateRequest(maximum), "correlation_123", "nonce_123");

        Assert.Equal(maximum.Select(option => option.Key.Value), card.Actions.Select(action => action.Action));
        Assert.Throws<ArgumentException>(() => TeamsApprovalCardRenderer.CreatePending(CreateRequest(tooMany), "correlation_123", "nonce_123"));
    }

    [Fact]
    public void Terminal_denial_card_keeps_the_tool_context_and_removes_actions()
    {
        var card = TeamsApprovalCardRenderer.CreateDenied(
            "shell_execute",
            "rmdir netclaw-approval-card-never-created",
            DateTimeOffset.Parse("2026-08-25T15:25:00Z"));

        Assert.Equal("Approval Denied", card.Title);
        Assert.Equal(TeamsApprovalCardTone.Attention, card.Tone);
        Assert.Equal("ShieldDismiss", card.IconName);
        Assert.Equal("STATUS: EXECUTION BLOCKED", card.Footer);
        Assert.Empty(card.Actions);
        Assert.Equal(
            [
                new TeamsApprovalCardField("Tool", "shell_execute"),
                new TeamsApprovalCardField("Command", "rmdir netclaw-approval-card-never-created"),
                new TeamsApprovalCardField("Denied By", "Authorized operator"),
                new TeamsApprovalCardField("Denied At", "2026-08-25 15:25 UTC"),
                new TeamsApprovalCardField("Reason", "User rejected the request")
            ],
            card.Fields);
        Assert.Equal("Approval denied. The request was rejected and was not executed.", card.Speak);
    }

    [Fact]
    public void Terminal_cards_honor_a_valid_operator_display_name()
    {
        var granted = TeamsApprovalCardRenderer.CreateGranted(
            "shell_execute",
            "git status",
            ApprovalOptionKeys.ApproveOnce,
            DateTimeOffset.Parse("2026-08-25T15:25:00Z"),
            operatorDisplayName: "Ada Lovelace");
        var denied = TeamsApprovalCardRenderer.CreateDenied(
            "shell_execute",
            "git status",
            DateTimeOffset.Parse("2026-08-25T15:25:00Z"),
            operatorDisplayName: "Grace Hopper");

        Assert.Contains(new TeamsApprovalCardField("Approved By", "Ada Lovelace"), granted.Fields);
        Assert.Contains(new TeamsApprovalCardField("Denied By", "Grace Hopper"), denied.Fields);
    }

    [Fact]
    public void Terminal_cards_fall_back_for_unsafe_presenter_labels_and_bound_safe_labels()
    {
        var unsafeCard = TeamsApprovalCardRenderer.CreateGranted(
            "shell_execute",
            "git status",
            ApprovalOptionKeys.ApproveOnce,
            DateTimeOffset.Parse("2026-08-25T15:25:00Z"),
            operatorDisplayName: "Ada\u202eLovelace");
        var rawGuidCard = TeamsApprovalCardRenderer.CreateDenied(
            "shell_execute",
            "git status",
            DateTimeOffset.Parse("2026-08-25T15:25:00Z"),
            operatorDisplayName: "f9acbff0-9265-4e8f-8fba-9bc16fe46ed5");
        var longName = new string('A', TeamsApprovalAction.MaxOperatorDisplayNameLength + 40);
        var boundedCard = TeamsApprovalCardRenderer.CreateGranted(
            "shell_execute",
            "git status",
            ApprovalOptionKeys.ApproveOnce,
            DateTimeOffset.Parse("2026-08-25T15:25:00Z"),
            operatorDisplayName: longName);

        Assert.Contains(new TeamsApprovalCardField("Approved By", "Authorized operator"), unsafeCard.Fields);
        Assert.Contains(new TeamsApprovalCardField("Denied By", "Authorized operator"), rawGuidCard.Fields);
        Assert.Contains(
            new TeamsApprovalCardField(
                "Approved By",
                ApprovalDisplayTextFormatter.Truncate(longName, TeamsApprovalAction.MaxOperatorDisplayNameLength)),
            boundedCard.Fields);
    }

    [Theory]
    [InlineData(ApprovalOptionKeys.ApproveOnce, "One-time approval")]
    [InlineData(ApprovalOptionKeys.ApproveSession, "Session approval")]
    [InlineData(ApprovalOptionKeys.ApproveAlways, "Always here")]
    [InlineData(ApprovalOptionKeys.ApproveEverywhere, "Always anywhere")]
    public void Granted_card_maps_the_accepted_scope_without_extending_it(string selectedKey, string scope)
    {
        var card = TeamsApprovalCardRenderer.CreateGranted(
            "shell_execute",
            "git status",
            selectedKey,
            DateTimeOffset.Parse("2026-08-25T15:25:00Z"));

        Assert.Equal("Approval Granted", card.Title);
        Assert.Equal(TeamsApprovalCardTone.Good, card.Tone);
        Assert.Equal("ShieldCheckmark", card.IconName);
        Assert.Equal("STATUS: EXECUTION AUTHORIZED", card.Footer);
        Assert.Empty(card.Actions);
        Assert.Contains(new TeamsApprovalCardField("Approval Scope", scope), card.Fields);
        Assert.Contains(new TeamsApprovalCardField("Execution State", "Execution Approved"), card.Fields);
        var terminalText = string.Join('\n', [card.Body, card.Banner, card.Footer, card.Speak, card.Summary]);
        Assert.DoesNotContain("success", terminalText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("complete", terminalText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Expired_card_records_no_decision_and_carries_no_actions()
    {
        var card = TeamsApprovalCardRenderer.CreateExpired(
            "shell_execute",
            "git status",
            DateTimeOffset.Parse("2026-08-25T15:25:00Z"));

        Assert.Equal("Approval Card Expired", card.Title);
        Assert.Equal(TeamsApprovalCardTone.Warning, card.Tone);
        Assert.Equal("ClockDismiss", card.IconName);
        Assert.Equal("STATUS: NO DECISION RECORDED", card.Footer);
        Assert.Empty(card.Actions);
        Assert.Contains(new TeamsApprovalCardField("Approval Window", "15 minutes"), card.Fields);
        Assert.Contains(new TeamsApprovalCardField("Expired At", "2026-08-25 15:25 UTC"), card.Fields);
    }

    [Fact]
    public void Elevated_card_is_a_presentation_variant_that_requires_caller_supplied_risk_details()
    {
        var card = TeamsApprovalCardRenderer.CreateElevatedPending(
            CreateRequest(new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)),
            "correlation_123",
            "nonce_123",
            "HIGH",
            "May permanently delete files.");

        Assert.Equal("Elevated Approval Required", card.Title);
        Assert.Equal(TeamsApprovalCardTone.Warning, card.Tone);
        Assert.Equal("Warning", card.IconName);
        Assert.Contains(new TeamsApprovalCardField("Risk Level", "HIGH"), card.Fields);
        Assert.Contains(new TeamsApprovalCardField("Impact", "May permanently delete files."), card.Fields);
        Assert.Throws<ArgumentException>(() => TeamsApprovalCardRenderer.CreateElevatedPending(
            CreateRequest(new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)),
            "correlation_123",
            "nonce_123",
            string.Empty,
            "May permanently delete files."));
    }

    [Fact]
    public void Nonce_hash_requires_an_exact_bounded_value()
    {
        const string nonce = "opaque_nonce_123";
        var hash = TeamsApprovalCardRenderer.HashNonce(nonce);

        Assert.True(TeamsApprovalCardRenderer.NonceMatches(hash, nonce));
        Assert.False(TeamsApprovalCardRenderer.NonceMatches(hash, "opaque_nonce_12"));
        Assert.False(TeamsApprovalCardRenderer.NonceMatches(hash, nonce + "!"));
        Assert.False(TeamsApprovalCardRenderer.NonceMatches(hash, new string('x', TeamsApprovalAction.MaxNonceLength + 1)));
        Assert.False(TeamsApprovalCardRenderer.NonceMatches("not-a-hash", nonce));
    }

    [Theory]
    [InlineData("approve", true)]
    [InlineData("deny", true)]
    [InlineData("approve_once", true)]
    [InlineData("approve_session", true)]
    [InlineData("approve_always", true)]
    [InlineData("approve_everywhere", true)]
    [InlineData("Approve", false)]
    [InlineData("approve now", false)]
    [InlineData("", false)]
    public void Approval_action_accepts_only_a_bounded_canonical_key_shape(string action, bool expected)
    {
        Assert.Equal(expected, TeamsApprovalAction.IsSupportedAction(action));
    }

    private static ToolInteractionRequest CreateRequest(params ToolInteractionOption[] options) => new()
    {
        SessionId = new SessionId("teams~dGVuYW50~personal~Y29udmVyc2F0aW9u/conversation"),
        Kind = "approval",
        CallId = new ToolCallId("call-invalid"),
        ToolName = new ToolName("execute_shell"),
        DisplayText = "git status",
        Options = options
    };
}

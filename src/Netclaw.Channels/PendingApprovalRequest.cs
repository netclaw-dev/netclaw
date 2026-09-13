// -----------------------------------------------------------------------
// <copyright file="PendingApprovalRequest.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Channels;

/// <summary>
/// In-memory record of an approval prompt a channel binding actor is
/// waiting on. This is recovery scratch state held in each actor's
/// pending-request list, not journaled — the durable record is
/// <c>PendingApprovalPromptTracked</c>/<c>PendingApprovalPromptCleared</c>
/// in <c>Netclaw.Actors.Channels</c>. <typeparamref name="TPromptId"/> is
/// the transport-specific prompt locator: a Discord message id, a
/// Mattermost post id, or a Slack event timestamp.
/// </summary>
public class PendingApprovalRequest<TPromptId>
    where TPromptId : struct
{
    public PendingApprovalRequest(ToolInteractionRequest request)
    {
        Request = request;
        CallId = request.CallId;
        RequesterSenderId = request.RequesterSenderId is { } requesterSenderId
            ? requesterSenderId.Value
            : null;
        RequesterPrincipal = request.RequesterPrincipal;
        Options = request.Options;
        OptionKeys = request.Options.Select(option => option.Key.Value).ToArray();
        ToolName = request.ToolName.Value;
        DisplayText = request.DisplayText;
    }

    public PendingApprovalRequest(
        ToolCallId callId,
        string? requesterSenderId,
        PrincipalClassification? requesterPrincipal,
        IReadOnlyList<string> optionKeys,
        TPromptId? promptId,
        string? toolName = null,
        string? displayText = null)
    {
        Request = null;
        CallId = callId;
        RequesterSenderId = requesterSenderId;
        RequesterPrincipal = requesterPrincipal;
        OptionKeys = [.. optionKeys];
        var isMcpTool = !string.IsNullOrEmpty(toolName) && new ToolName(toolName).IsMcp;
        Options = OptionKeys
            .Select(key => new ToolInteractionOption(
                new ApprovalOptionKey(key),
                ApprovalOptionKeys.LabelFor(key, isMcpTool)))
            .ToArray();
        PromptId = promptId;
        ToolName = toolName;
        DisplayText = displayText;
    }

    public ToolInteractionRequest? Request { get; }
    public ToolCallId CallId { get; }

    public string? RequesterSenderId { get; }

    public PrincipalClassification? RequesterPrincipal { get; }
    public IReadOnlyList<ToolInteractionOption> Options { get; }
    public IReadOnlyList<string> OptionKeys { get; }

    /// <summary>
    /// Tool name carried through cold-spawn recovery so the redraw can render
    /// the original tool name without round-tripping to the session. Null on
    /// pre-field journal entries.
    /// </summary>
    public string? ToolName { get; }

    /// <summary>
    /// Display text carried through cold-spawn recovery (already truncated to
    /// the persisted ceiling). Null on pre-field journal entries.
    /// </summary>
    public string? DisplayText { get; }

    public TPromptId? PromptId { get; set; }
}

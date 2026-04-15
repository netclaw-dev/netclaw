using Netclaw.Actors.Channels;
using ProtoBuf;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Command delivering user input to a session actor.
/// </summary>
[ProtoContract]
public sealed class SendUserMessage : IWithSessionId
{
    [ProtoMember(1)]
    public SessionId SessionId { get; set; }

    [ProtoMember(2)]
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Media references (images, audio, etc.) attached to this message.
    /// </summary>
    [ProtoMember(3)]
    public List<SerializableMediaReference> MediaReferences { get; set; } = new();

    /// <summary>
    /// Ephemeral channel metadata for ACL/audit. Not persisted.
    /// </summary>
    [ProtoIgnore]
    public MessageSource? Source { get; set; }
}

/// <summary>
/// Channel-agnostic trusted-turn delivery for Mode B reminder re-entry.
/// Issued by <c>ReminderExecutionActor</c> to the originating channel's
/// gateway actor. Gateways route this message down their existing
/// inbound-routing hierarchy via <c>Forward</c> (preserving the
/// dispatcher's <c>Ask&lt;CommandAck&gt;</c> temp actor as <c>Sender</c>)
/// until the leaf binding/session actor offers a <see cref="Channels.ChannelInput"/>
/// to the pipeline with <see cref="Channels.MessageSource.AckTarget"/>
/// populated from that <c>Sender</c>. No channel-level inbound ACL check
/// is performed — the reminder's audience is validated at minting time
/// by <c>reminder-audience-authorization</c>.
/// </summary>
public sealed record DeliverTrustedSessionTurn(
    SessionId SessionId,
    string Content,
    Channels.MessageSource Source) : IWithSessionId;

/// <summary>
/// User's response to a <see cref="ToolInteractionRequest"/>.
/// Routed from the channel adapter to the session actor to complete the
/// blocked tool's <see cref="System.Threading.Tasks.TaskCompletionSource{T}"/>.
/// </summary>
public sealed class ToolInteractionResponse : IWithSessionId
{
    public required SessionId SessionId { get; init; }

    /// <summary>The tool call ID from the original <see cref="ToolInteractionRequest"/>.</summary>
    public required string CallId { get; init; }

    /// <summary>The selected option key. See <see cref="ApprovalOptionKeys"/>.</summary>
    public required string SelectedKey { get; init; }

    /// <summary>
    /// Identity of the user who selected the option. Used to bind approvals to
    /// the same principal that initiated the tool request.
    /// </summary>
    public required string SenderId { get; init; }
}

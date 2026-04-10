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

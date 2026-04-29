// -----------------------------------------------------------------------
// <copyright file="SerializableChatMessage.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using ProtoBuf;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Persistence-safe representation of a chat message.
/// Never persist Microsoft.Extensions.AI types directly — use this instead.
/// </summary>
[ProtoContract]
public sealed class SerializableChatMessage
{
    [ProtoMember(1)]
    public ChatRole Role { get; set; }

    [ProtoMember(2)]
    public string Content { get; set; } = string.Empty;

    /// <summary>Optional name (used for tool results: the tool function name).</summary>
    [ProtoMember(3)]
    public string? Name { get; set; }

    /// <summary>
    /// Tool calls requested by the assistant. Present when role is Assistant
    /// and the LLM wants to invoke tools.
    /// </summary>
    [ProtoMember(4)]
    public List<SerializableToolCall> ToolCalls { get; set; } = new();

    /// <summary>
    /// The tool call ID this message is a result for. Present when role is Tool.
    /// </summary>
    [ProtoMember(5)]
    public string? ToolCallId { get; set; }

    /// <summary>
    /// Media references (images, audio, etc.) attached to this message.
    /// Stored as relative paths within the session media directory.
    /// </summary>
    [ProtoMember(6)]
    public List<SerializableMediaReference> MediaReferences { get; set; } = new();
}

/// <summary>
/// Persistence-safe reference to a media file stored in the session directory.
/// </summary>
[ProtoContract]
public sealed class SerializableMediaReference
{
    /// <summary>Relative path within the session media directory.</summary>
    [ProtoMember(1)]
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>MIME type of the media (e.g. "image/png").</summary>
    [ProtoMember(2)]
    public string MimeType { get; set; } = string.Empty;

    /// <summary>Content modality as integer for wire safety (maps to <see cref="MediaModality"/>).</summary>
    [ProtoMember(3)]
    public int Modality { get; set; }
}

/// <summary>
/// Content modality for media references. Matches <see cref="Netclaw.Configuration.ModelModality"/>
/// values but kept as a separate enum to avoid coupling persistence types to configuration.
/// </summary>
public enum MediaModality
{
    Image = 1,
    Audio = 2,
    Video = 3
}

/// <summary>
/// Persistence-safe representation of a single tool call from the assistant.
/// </summary>
[ProtoContract]
public sealed class SerializableToolCall
{
    [ProtoMember(1)]
    public string CallId { get; set; } = string.Empty;

    [ProtoMember(2)]
    public string Name { get; set; } = string.Empty;

    [ProtoMember(3)]
    public string ArgumentsJson { get; set; } = string.Empty;

    /// <summary>
    /// Opaque JSON envelope for per-call metadata (rationale, timeout hint, background flag).
    /// Null for legacy tool calls persisted before metadata support was added.
    /// </summary>
    [ProtoMember(4)]
    public string? MetaJson { get; set; }
}

/// <summary>
/// Role of a chat message participant. Stable integer values for wire safety.
/// </summary>
public enum ChatRole
{
    User = 0,
    Assistant = 1,
    System = 2,
    Tool = 3
}

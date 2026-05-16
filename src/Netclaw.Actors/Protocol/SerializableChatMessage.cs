// -----------------------------------------------------------------------
// <copyright file="SerializableChatMessage.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Serialization;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Persistence-safe representation of a chat message.
/// Never persist Microsoft.Extensions.AI types directly — use this instead.
/// </summary>
public sealed record SerializableChatMessage : INetclawSerializableMessage
{
    public ChatRole Role { get; init; }

    public string Content { get; init; } = string.Empty;

    /// <summary>Optional name (used for tool results: the tool function name).</summary>
    public string? Name { get; init; }

    /// <summary>
    /// Tool calls requested by the assistant. Present when role is Assistant
    /// and the LLM wants to invoke tools.
    /// </summary>
    public IReadOnlyList<SerializableToolCall> ToolCalls { get; init; } =
        Array.Empty<SerializableToolCall>();

    /// <summary>
    /// The tool call ID this message is a result for. Present when role is Tool.
    /// </summary>
    public ToolCallId? ToolCallId { get; init; }

    /// <summary>
    /// Media references (images, audio, etc.) attached to this message.
    /// Stored as relative paths within the session media directory.
    /// </summary>
    public IReadOnlyList<SerializableMediaReference> MediaReferences { get; init; } =
        Array.Empty<SerializableMediaReference>();
}

/// <summary>
/// Persistence-safe reference to a media file stored in the session directory.
/// </summary>
public sealed record SerializableMediaReference : INetclawSerializableMessage
{
    /// <summary>Relative path within the session media directory.</summary>
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>MIME type of the media (e.g. "image/png").</summary>
    public MimeType MimeType { get; init; }

    /// <summary>Content modality as integer for wire safety (maps to <see cref="MediaModality"/>).</summary>
    public int Modality { get; init; }

    /// <summary>
    /// Raw file size in bytes. Used by compaction token estimation to account
    /// for base64-encoded media payloads at LLM-call time without reading
    /// files from disk. Zero for records persisted before this field was
    /// added — proto3 default — which causes legacy records to under-count
    /// the same way they did before this field existed (no regression).
    /// </summary>
    public long FileSizeBytes { get; init; }
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
public sealed record SerializableToolCall : INetclawSerializableMessage
{
    public ToolCallId CallId { get; init; }

    public ToolName Name { get; init; }

    public string ArgumentsJson { get; init; } = string.Empty;

    /// <summary>
    /// Opaque JSON envelope for per-call metadata (rationale, timeout hint, background flag).
    /// Null for legacy tool calls persisted before metadata support was added.
    /// </summary>
    public string? MetaJson { get; init; }
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

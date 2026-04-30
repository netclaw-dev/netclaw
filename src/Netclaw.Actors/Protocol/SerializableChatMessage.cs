// -----------------------------------------------------------------------
// <copyright file="SerializableChatMessage.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Protocol;

/// <summary>
/// Persistence-safe representation of a chat message.
/// Never persist Microsoft.Extensions.AI types directly — use this instead.
/// </summary>
public sealed class SerializableChatMessage
{
    public ChatRole Role { get; set; }

    public string Content { get; set; } = string.Empty;

    /// <summary>Optional name (used for tool results: the tool function name).</summary>
    public string? Name { get; set; }

    /// <summary>
    /// Tool calls requested by the assistant. Present when role is Assistant
    /// and the LLM wants to invoke tools.
    /// </summary>
    public List<SerializableToolCall> ToolCalls { get; set; } = [];

    /// <summary>
    /// The tool call ID this message is a result for. Present when role is Tool.
    /// </summary>
    public string? ToolCallId { get; set; }

    /// <summary>
    /// Media references (images, audio, etc.) attached to this message.
    /// Stored as relative paths within the session media directory.
    /// </summary>
    public List<SerializableMediaReference> MediaReferences { get; set; } = [];
}

/// <summary>
/// Persistence-safe reference to a media file stored in the session directory.
/// </summary>
public sealed class SerializableMediaReference
{
    /// <summary>Relative path within the session media directory.</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>MIME type of the media (e.g. "image/png").</summary>
    public string MimeType { get; set; } = string.Empty;

    /// <summary>Content modality as integer for wire safety (maps to <see cref="MediaModality"/>).</summary>
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
public sealed class SerializableToolCall
{
    public string CallId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string ArgumentsJson { get; set; } = string.Empty;

    /// <summary>
    /// Opaque JSON envelope for per-call metadata (rationale, timeout hint, background flag).
    /// Null for legacy tool calls persisted before metadata support was added.
    /// </summary>
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

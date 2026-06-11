// -----------------------------------------------------------------------
// <copyright file="ToolCallUpdate.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Tools;

/// <summary>
/// One item in a streaming tool-call result. A tool invocation yields zero or
/// more non-terminal <see cref="ToolActivityUpdate"/> items followed by exactly
/// one terminal <see cref="ToolCompletedUpdate"/>.
/// </summary>
public interface ToolCallUpdate;

/// <summary>
/// A non-terminal progress/liveness signal emitted while a tool is still
/// running. Activity items drive the per-call inactivity watchdog and an
/// optional live output relay; they are never accumulated into LLM context.
/// </summary>
/// <param name="Phase">A short label describing what the tool is doing.</param>
/// <param name="OutputChunk">
/// Optional incremental output (e.g. streamed shell stdout) for live display.
/// </param>
public sealed record ToolActivityUpdate(string Phase, string? OutputChunk = null) : ToolCallUpdate
{
    /// <summary>
    /// True when the tool is intentionally blocked on external input, such as a
    /// human approval prompt. The watchdog resumes on the next non-suspending
    /// activity item or terminal completion.
    /// </summary>
    public bool SuspendsInactivityWatchdog { get; init; }
}

/// <summary>
/// The terminal item of a tool-call stream. Its <see cref="Result"/> is the only
/// part of the stream that becomes the tool-result message in the conversation.
/// </summary>
/// <param name="Result">The tool's final result text.</param>
public sealed record ToolCompletedUpdate(string Result) : ToolCallUpdate;

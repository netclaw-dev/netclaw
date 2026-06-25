// -----------------------------------------------------------------------
// <copyright file="ToolCallUpdate.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Tools;

/// <summary>
/// Describes who owns stall detection for a tool call.
/// </summary>
public enum ToolLivenessMode
{
    /// <summary>
    /// The tool does not expose reliable internal stall detection. The parent
    /// tool pipeline bounds the whole call with a wall-clock budget.
    /// </summary>
    Opaque,

    /// <summary>
    /// The tool has its own internal watchdogs and returns a terminal success or
    /// failure result when it detects a stall. The parent pipeline only guards
    /// the wait for the first stream item.
    /// </summary>
    SelfMonitoring
}

/// <summary>
/// One item in a streaming tool-call result. A tool invocation yields zero or
/// more non-terminal <see cref="ToolActivityUpdate"/> items followed by exactly
/// one terminal <see cref="ToolCompletedUpdate"/>.
/// </summary>
public interface ToolCallUpdate;

/// <summary>
/// A non-terminal activity signal emitted while a tool is still running.
/// Activity items may feed live output relays, but their liveness meaning is
/// determined by the tool's <see cref="ToolLivenessMode"/>. They are never
/// accumulated into LLM context.
/// </summary>
/// <param name="Phase">A short label describing what the tool is doing.</param>
/// <param name="OutputChunk">
/// Optional incremental output (e.g. streamed shell stdout) for live display.
/// </param>
public sealed record ToolActivityUpdate(string Phase, string? OutputChunk = null) : ToolCallUpdate;

/// <summary>
/// The terminal item of a tool-call stream. Its <see cref="Result"/> is the only
/// part of the stream that becomes the tool-result message in the conversation.
/// </summary>
/// <param name="Result">The tool's final result text.</param>
public sealed record ToolCompletedUpdate(string Result) : ToolCallUpdate;

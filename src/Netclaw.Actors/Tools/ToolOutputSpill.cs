// -----------------------------------------------------------------------
// <copyright file="ToolOutputSpill.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Text;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Bounds a tool result to the inline budget <c>N</c>
/// (<see cref="ToolExecutionContext.MaxInlineToolResultChars"/>) and, when it
/// exceeds <c>N</c>, spills the full result to
/// <c>{SessionDirectory}/tool-calls/{toolCallId}.log</c> and steers the model to
/// read a slice (<c>file_read</c> offset/limit) or grep it instead of re-running.
/// </summary>
/// <remarks>
/// Called from <c>DispatchingToolExecutor</c> for <i>every</i> tool, right after
/// the central redaction — so bounding + spill happen once, uniformly, for the
/// main session and sub-agents alike (both funnel through the dispatcher). Tools
/// only bound their own <i>capture</i> for memory safety; they do not window or
/// spill. The two-param overload accepts separate model-facing and spill content
/// so that tools with <c>SuppressOutputRedaction</c> can return raw results to the
/// model while still writing redacted content to the spill file on disk.
/// </remarks>
internal static class ToolOutputSpill
{
    private const string ToolCallsSubdirectory = "tool-calls";

    // Content budget used when neither the tool nor the context supplies one
    // (sub-agent / Empty / direct construction). Matches
    // SessionTuning.MaxInlineToolResultChars's default so un-plumbed paths bound the
    // same as the main session.
    internal const int DefaultContentBudget = 12_000;

    /// <summary>
    /// Returns <paramref name="redactedResult"/> unchanged if it fits
    /// <paramref name="budget"/>; otherwise returns a <paramref name="budget"/>-char
    /// head+tail window plus a steer, having spilled the full result to a session
    /// file (when a session directory and call id are available). The dispatcher
    /// resolves <paramref name="budget"/> from the tool's per-tool override or the
    /// session content budget.
    /// </summary>
    public static Task<string> BoundAndSpillAsync(
        string redactedResult, string? toolCallId, int budget, ToolExecutionContext? context, CancellationToken ct)
        => BoundAndSpillAsync(modelFacingResult: redactedResult, spillContent: redactedResult,
            toolCallId, budget, context, ct);

    /// <summary>
    /// Overload that separates the model-facing result from the spill content.
    /// When a tool suppresses output redaction, <paramref name="modelFacingResult"/>
    /// is the raw (unredacted) result while <paramref name="spillContent"/> is the
    /// redacted version written to disk.
    /// </summary>
    public static async Task<string> BoundAndSpillAsync(
        string modelFacingResult, string spillContent, string? toolCallId, int budget,
        ToolExecutionContext? context, CancellationToken ct)
    {
        if (budget <= 0)
            budget = DefaultContentBudget;

        if (modelFacingResult.Length <= budget)
            return modelFacingResult;

        var inline = BoundedOutputReader.Window(modelFacingResult, budget);
        var spillPath = await TryWriteSpillAsync(spillContent, toolCallId, context, ct);
        return Compose(inline, spillPath, modelFacingResult.Length, budget);
    }

    private static async Task<string?> TryWriteSpillAsync(
        string redacted, string? toolCallId, ToolExecutionContext? context, CancellationToken ct)
    {
        // A spill needs both a place (session dir) and a name (call id). Without
        // either, degrade to inline-only.
        if (context is null
            || string.IsNullOrWhiteSpace(context.SessionDirectory)
            || string.IsNullOrWhiteSpace(toolCallId))
            return null;

        // Best-effort: the inline head+tail is always returned, and a failed (or
        // cancelled) on-disk copy must not fail the tool call — so the write is
        // decoupled from the request's CancellationToken (the body is bounded by
        // the capture ceiling, so the write is small and fast). The `ct` is kept
        // in the signature for symmetry / future use.
        _ = ct;
        try
        {
            var dir = Path.Combine(context.SessionDirectory, ToolCallsSubdirectory);
            Directory.CreateDirectory(dir);
            // Sanitize the (provider-supplied) call id so a spill can never escape
            // the tool-calls directory.
            var path = Path.Combine(dir, SafeFileName(toolCallId!) + ".log");
            await File.WriteAllTextAsync(path, redacted, CancellationToken.None);
            return path;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException
                                   or System.Security.SecurityException)
        {
            Debug.WriteLine($"tool-output spill write failed: {ex.Message}");
            return null;
        }
    }

    private static string Compose(string inline, string? spillPath, int fullLength, int budget)
    {
        var sb = new StringBuilder(inline);
        sb.Append($"\n\n[output truncated to {budget} chars of {fullLength}");
        if (spillPath is not null)
            sb.Append($"; output saved to {spillPath} — read a slice with file_read (offset/limit) or grep it instead of re-running");
        sb.Append(']');
        return sb.ToString();
    }

    private static string SafeFileName(string id)
    {
        var invalid = Path.GetInvalidFileNameChars();
        Span<char> buffer = id.Length is > 0 and <= 256 ? stackalloc char[id.Length] : new char[id.Length];
        for (var i = 0; i < id.Length; i++)
            buffer[i] = invalid.Contains(id[i]) ? '_' : id[i];
        var safe = new string(buffer);
        return string.IsNullOrWhiteSpace(safe) ? "tool-call" : safe;
    }
}

// -----------------------------------------------------------------------
// <copyright file="LlmFacingToolName.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;

namespace Netclaw.Tools;

/// <summary>
/// The form of a tool name that is safe to surface to an LLM provider's
/// tool-use API. Anthropic enforces <c>^[a-zA-Z0-9_-]{1,128}$</c> on tool
/// names; OpenAI and others are at least as permissive. This type exists
/// to keep that constraint at a type boundary instead of relying on
/// every emit site to remember which form is required.
/// </summary>
/// <remarks>
/// Inside Netclaw, tool identity is always the canonical
/// <see cref="ToolName"/> (e.g. <c>notion/notion-create-pages</c> for
/// MCP, <c>shell_execute</c> for first-party). The LLM-facing alias
/// replaces characters disallowed by the regex above — currently only
/// <c>/</c> → <c>__</c> — and is surfaced only at the two LLM boundaries:
/// when emitting tool definitions, and when echoing tool result messages
/// back. Internal code (audit logs, approvals, prompts, CLI) should
/// keep working in canonical <see cref="ToolName"/> values.
/// </remarks>
public readonly record struct LlmFacingToolName
{
    private static readonly Regex AnthropicSafeName =
        new("^[a-zA-Z0-9_-]{1,128}$", RegexOptions.Compiled);

    private LlmFacingToolName(string value) => Value = value;

    /// <summary>The LLM-facing string. Guaranteed to satisfy the
    /// Anthropic tool-name regex.</summary>
    public string Value { get; }

    /// <summary>
    /// Produce an LLM-facing alias from a canonical tool name. The only
    /// transformation today is <c>/</c> → <c>__</c> (MCP namespacing);
    /// names without disallowed characters round-trip unchanged. Throws
    /// <see cref="ArgumentException"/> if the result still fails the
    /// regex — that means a name with other disallowed characters (space,
    /// dot, colon, etc.) made it this far, which is a bug at the source.
    /// </summary>
    public static LlmFacingToolName FromCanonical(string canonical)
    {
        ArgumentException.ThrowIfNullOrEmpty(canonical);

        var sanitized = canonical.Replace("/", "__", StringComparison.Ordinal);
        if (!AnthropicSafeName.IsMatch(sanitized))
        {
            throw new ArgumentException(
                $"Tool name '{canonical}' cannot be made LLM-safe by replacing '/' with '__'. " +
                $"Result '{sanitized}' contains characters outside [a-zA-Z0-9_-] or exceeds 128 chars. " +
                "Pick a name that satisfies the Anthropic tool-name regex.",
                nameof(canonical));
        }

        return new LlmFacingToolName(sanitized);
    }

    public override string ToString() => Value;

    /// <summary>
    /// Pure-string reversal of the LLM-facing alias to a canonical
    /// candidate, used by surfaces that don't have a
    /// <c>ToolRegistry</c> handy (config loader, doctor checks, CLI
    /// against the on-disk approval store). Returns the canonical
    /// candidate if <paramref name="name"/> matches the
    /// <c>{server}__{tool}</c> shape; returns <c>null</c> if the input
    /// already looks canonical (contains <c>/</c>) or doesn't contain a
    /// <c>__</c> separator. First-party tool names use single
    /// underscores by convention, so a name with <c>__</c> in it is
    /// reliably an MCP alias.
    /// </summary>
    /// <remarks>
    /// Heuristic, not authoritative: a tool literally named
    /// <c>foo__bar</c> (no MCP server prefix) would be misread as
    /// <c>foo/bar</c>. The convention is enforced by code review on
    /// new first-party tools; if a future tool legitimately needs
    /// <c>__</c> in its name, this method needs to grow a registry-
    /// aware overload.
    /// </remarks>
    public static string? TryReverseSanitizedToCanonical(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        if (name.Contains('/', StringComparison.Ordinal))
            return null;
        var idx = name.IndexOf("__", StringComparison.Ordinal);
        if (idx <= 0 || idx + 2 >= name.Length)
            return null;
        return string.Concat(name.AsSpan(0, idx), "/", name.AsSpan(idx + 2));
    }
}

// -----------------------------------------------------------------------
// <copyright file="ToolConfig.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Shared configuration for first-party tool execution.
/// </summary>
public sealed class ToolConfig
{
    public ShellExecutionMode? ShellMode { get; set; }

    /// <summary>
    /// The capture ceiling: the maximum characters of tool output captured (in
    /// bounded memory) to become the spill body written to a session file. It is
    /// NOT the inline budget — <c>SessionTuning.MaxInlineToolResultChars</c> (<c>N</c>)
    /// owns what the model sees inline. Output beyond this ceiling is drained-and-
    /// discarded (the source keeps draining so a live child never deadlocks) and the
    /// spill is a head+tail view. Sized so the spill is useful while staying
    /// redactable in a single in-memory pass.
    /// </summary>
    public int MaxOutputChars { get; set; } = 256_000;

    public ToolAudienceProfiles AudienceProfiles { get; set; } = new();
    public WebFetchConfig WebFetch { get; set; } = new();

    /// <summary>
    /// Additional shell command patterns to add to the hard deny list.
    /// These are verb-chain prefixes that are categorically blocked
    /// and cannot be approved. Added to the compiled-in defaults.
    /// </summary>
    public List<string> HardDenyPatterns { get; set; } = [];
}

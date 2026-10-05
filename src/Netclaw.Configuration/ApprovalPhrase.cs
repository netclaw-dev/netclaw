// -----------------------------------------------------------------------
// <copyright file="ApprovalPhrase.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

namespace Netclaw.Configuration;

/// <summary>
/// Canonical shell identity for a persistent shell approval phrase.
/// </summary>
public enum ApprovalShell
{
    /// <summary>Bash grammar.</summary>
    Bash = 0,

    /// <summary>PowerShell grammar.</summary>
    PowerShell = 1,
}

/// <summary>
/// Match rule for a persistent shell approval phrase.
/// </summary>
public enum ApprovalMatchKind
{
    /// <summary>
    /// Match the stored tokens against the candidate's ShellSyntaxTree command
    /// words (<see cref="ToolApprovalEntryComparer.CoversCommandWords"/>). A
    /// grant of two or more tokens covers the words that start with its tokens.
    /// A grant of one token (the program) covers that word alone.
    /// </summary>
    TokenPrefix = 0,

    /// <summary>
    /// Match a version-2 phrase. The space-separated words of the phrase get
    /// the same rule as <see cref="TokenPrefix"/> tokens. The kind keeps the
    /// stored form of an upgraded grant; it does not give a different reach.
    /// </summary>
    LegacyExact = 1,
}

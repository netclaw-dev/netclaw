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
    /// <summary>Match whole initial tokens.</summary>
    TokenPrefix = 0,

    /// <summary>Match the complete legacy phrase only.</summary>
    LegacyExact = 1,
}

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
    /// Match the exact verb chain. The candidate's verb tokens must equal the
    /// stored tokens; the arguments are free. The name is historical: the store
    /// writes it as <c>TokenPrefix</c>, but a grant never covers a longer chain.
    /// </summary>
    TokenPrefix = 0,

    /// <summary>
    /// Match the complete legacy phrase. The phrase must equal the candidate's
    /// whole verb chain; the arguments are free.
    /// </summary>
    LegacyExact = 1,
}

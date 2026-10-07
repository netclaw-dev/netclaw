// -----------------------------------------------------------------------
// <copyright file="Coverage.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Security.Authorization.Consent;

namespace Netclaw.Actors.Authorization.Consent;

/// <summary>
/// Why one candidate needs no prompt. An uncovered candidate has no value.
/// </summary>
/// <remarks>
/// A stored grant carries its own <see cref="GrantScope"/>, so no parallel enum
/// names the scope again. The decision trace translates a coverage value to its
/// diagnostic vocabulary (<see cref="Tools.ShellPolicyTraceCoverage"/>).
/// </remarks>
internal abstract record Coverage
{
    private Coverage()
    {
    }

    /// <summary>
    /// An approval-exempt side effect, for example <c>echo</c> or <c>true</c>.
    /// </summary>
    public sealed record Exempt : Coverage
    {
        private Exempt()
        {
        }

        public static Exempt Instance { get; } = new();
    }

    /// <summary>The operator's "Once" answer for this exact prompt.</summary>
    public sealed record OneTime : Coverage
    {
        private OneTime()
        {
        }

        public static OneTime Instance { get; } = new();
    }

    /// <summary>A session or persistent grant.</summary>
    /// <param name="Scope">Where the grant applies.</param>
    /// <param name="GrantedAt">When the store first wrote the grant, when known.</param>
    public sealed record Stored(GrantScope Scope, DateTimeOffset? GrantedAt) : Coverage;

    /// <summary>A reviewed-safe diagnostic phrase inside a reviewed root.</summary>
    public sealed record ReviewedSafe(ReviewedSafeRoot Root) : Coverage;
}

/// <summary>Which directory placed a reviewed-safe phrase inside a reviewed root.</summary>
internal enum ReviewedSafeRoot
{
    /// <summary>The candidate's own resolved directory.</summary>
    Real,

    /// <summary>The directory that a covered prerequisite (for example <c>cd</c>) selects.</summary>
    Intent,
}

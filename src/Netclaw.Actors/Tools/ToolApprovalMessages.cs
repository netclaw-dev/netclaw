// -----------------------------------------------------------------------
// <copyright file="ToolApprovalMessages.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Internal cross-actor message contract between the session pipeline and
/// <c>ToolApprovalActor</c>. Assembly-internal: not a public protocol surface.
/// </summary>
internal static class ToolApprovalProtocol
{
    /// <summary>Marker for tool-approval commands.</summary>
    internal interface IToolApprovalCommand;

    /// <summary>Marker for tool-approval queries.</summary>
    internal interface IToolApprovalQuery;

    /// <summary>Marker for tool-approval responses.</summary>
    internal interface IToolApprovalResponse;

    // ===== Queries =====

    internal sealed record GetUnapprovedPatterns(
        SessionId? SessionId,
        TrustAudience Audience,
        ToolName ToolName,
        IReadOnlyList<ApprovalCandidate> Candidates,
        string? Cwd) : IToolApprovalQuery;

    // ===== Responses =====

    internal sealed record UnapprovedPatternsResponse(ToolApprovalCheckResult Result) : IToolApprovalResponse;

    // ===== Commands =====

    internal sealed record RecordToolApproval(
        SessionId SessionId,
        TrustAudience Audience,
        ToolName ToolName,
        IReadOnlyList<string> Patterns,
        bool Persistent,
        string? Cwd) : IToolApprovalCommand;
}

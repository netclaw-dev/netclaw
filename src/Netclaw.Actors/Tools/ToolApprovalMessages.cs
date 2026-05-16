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

internal sealed record GetUnapprovedPatterns(
    SessionId? SessionId,
    TrustAudience Audience,
    ToolName ToolName,
    IReadOnlyList<ApprovalCandidate> Candidates,
    string? Cwd);

internal sealed record UnapprovedPatternsResponse(ToolApprovalCheckResult Result);

internal sealed record RecordToolApproval(
    SessionId SessionId,
    TrustAudience Audience,
    ToolName ToolName,
    IReadOnlyList<string> Patterns,
    bool Persistent,
    string? Cwd);

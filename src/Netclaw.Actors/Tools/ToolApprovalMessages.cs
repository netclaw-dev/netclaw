// -----------------------------------------------------------------------
// <copyright file="ToolApprovalMessages.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

internal sealed record GetUnapprovedPatterns(
    string? SessionId,
    TrustAudience Audience,
    ToolName ToolName,
    IReadOnlyList<string> Patterns);

internal sealed record UnapprovedPatternsResponse(IReadOnlyList<string> Patterns);

internal sealed record RecordToolApproval(
    string SessionId,
    TrustAudience Audience,
    ToolName ToolName,
    IReadOnlyList<string> Patterns,
    bool Persistent);

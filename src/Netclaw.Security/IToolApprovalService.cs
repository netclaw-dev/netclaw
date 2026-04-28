// -----------------------------------------------------------------------
// <copyright file="IToolApprovalService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Security;

public interface IToolApprovalService
{
    Task<IReadOnlyList<string>> GetUnapprovedPatternsAsync(
        string? sessionId,
        TrustAudience audience,
        ToolName toolName,
        IReadOnlyList<string> patterns,
        CancellationToken ct = default);

    Task RecordApprovalAsync(
        string sessionId,
        TrustAudience audience,
        ToolName toolName,
        IReadOnlyList<string> patterns,
        bool persistent,
        CancellationToken ct = default);
}

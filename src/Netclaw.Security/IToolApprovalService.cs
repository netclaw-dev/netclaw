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
    /// <summary>
    /// Returns the subset of <paramref name="patterns"/> (candidate verb chains)
    /// that are not approved for the given audience and tool. The
    /// <paramref name="cwd"/> is the candidate's resolved working directory; it
    /// is used by the v2 matcher to evaluate folder-scoped
    /// <see cref="Netclaw.Configuration.ApprovalEntry"/> records. May be null
    /// for tools whose approvals are not directory-anchored.
    /// </summary>
    Task<IReadOnlyList<string>> GetUnapprovedPatternsAsync(
        string? sessionId,
        TrustAudience audience,
        ToolName toolName,
        IReadOnlyList<string> patterns,
        string? cwd,
        CancellationToken ct = default);

    Task RecordApprovalAsync(
        string sessionId,
        TrustAudience audience,
        ToolName toolName,
        IReadOnlyList<string> patterns,
        bool persistent,
        string? cwd,
        CancellationToken ct = default);
}

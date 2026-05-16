// -----------------------------------------------------------------------
// <copyright file="AkkaToolApprovalService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

public sealed class AkkaToolApprovalService : IToolApprovalService
{
    private readonly IRequiredActor<ToolApprovalActorKey> _actorProvider;

    public AkkaToolApprovalService(IRequiredActor<ToolApprovalActorKey> actorProvider)
    {
        _actorProvider = actorProvider;
    }

    public async Task<IReadOnlyList<string>> GetUnapprovedPatternsAsync(
        string? sessionId,
        TrustAudience audience,
        ToolName toolName,
        IReadOnlyList<string> patterns,
        string? cwd,
        CancellationToken ct = default)
    {
        var candidates = patterns.Select(pattern => new ApprovalCandidate(pattern, Directory: null)).ToList();
        var result = await CheckApprovalAsync(sessionId, audience, toolName, candidates, cwd, ct);
        return result.UnapprovedPatterns;
    }

    public async Task<ToolApprovalCheckResult> CheckApprovalAsync(
        string? sessionId,
        TrustAudience audience,
        ToolName toolName,
        IReadOnlyList<ApprovalCandidate> candidates,
        string? cwd,
        CancellationToken ct = default)
    {
        var actor = await _actorProvider.GetAsync(ct);
        var response = await actor.Ask<UnapprovedPatternsResponse>(
            new GetUnapprovedPatterns(sessionId is not null ? (SessionId)sessionId : null, audience, toolName, candidates, cwd),
            TimeSpan.FromSeconds(5),
            ct);

        return response.Result;
    }

    public async Task RecordApprovalAsync(
        string sessionId,
        TrustAudience audience,
        ToolName toolName,
        IReadOnlyList<string> patterns,
        bool persistent,
        string? cwd,
        CancellationToken ct = default)
    {
        var actor = await _actorProvider.GetAsync(ct);
        await actor.Ask<ToolApprovalRecorded>(
            new RecordToolApproval((SessionId)sessionId, audience, toolName, patterns, persistent, cwd),
            TimeSpan.FromSeconds(5),
            ct);
    }
}

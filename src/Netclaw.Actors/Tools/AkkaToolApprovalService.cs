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
using static Netclaw.Actors.Tools.ToolApprovalProtocol;

namespace Netclaw.Actors.Tools;

public sealed class AkkaToolApprovalService :
    IToolApprovalService,
    IShellApprovalMatchService
{
    private readonly IRequiredActor<ToolApprovalActorKey> _actorProvider;

    public AkkaToolApprovalService(IRequiredActor<ToolApprovalActorKey> actorProvider)
    {
        _actorProvider = actorProvider;
    }

    public async Task<ToolApprovalCheckResult> CheckApprovalAsync(
        ToolApprovalSessionId? sessionId,
        TrustAudience audience,
        ToolName toolName,
        IReadOnlyList<ApprovalCandidate> candidates,
        string? cwd,
        CancellationToken ct = default)
    {
        var actor = await _actorProvider.GetAsync(ct);
        var protocolSessionId = sessionId.HasValue ? (SessionId)sessionId.Value.Value : (SessionId?)null;
        var response = await actor.Ask<UnapprovedPatternsResponse>(
            new GetUnapprovedPatterns(protocolSessionId, audience, toolName, candidates, cwd),
            TimeSpan.FromSeconds(5),
            ct);

        return response.Result;
    }

    async Task<ShellApprovalMatchResult> IShellApprovalMatchService.MatchShellCandidatesAsync(
        ShellApprovalMatchRequest request,
        CancellationToken cancellationToken)
    {
        var actor = await _actorProvider.GetAsync(cancellationToken);
        var protocolSessionId = request.SessionId.HasValue
            ? (SessionId)request.SessionId.Value.Value
            : (SessionId?)null;
        var response = await actor.Ask<ShellApprovalMatchResponse>(
            new MatchShellCandidates(
                protocolSessionId,
                request.Audience,
                request.ToolName,
                request.Candidates),
            TimeSpan.FromSeconds(5),
            cancellationToken);

        return response.Result;
    }

    public async Task RecordApprovalCandidatesAsync(
        ToolApprovalSessionId sessionId,
        TrustAudience audience,
        ToolName toolName,
        IReadOnlyList<ToolApprovalGrant> grants,
        CancellationToken ct = default)
    {
        var actor = await _actorProvider.GetAsync(ct);
        var result = await actor.Ask<ToolApprovalRecorded>(
            new RecordStructuredToolApproval(
                (SessionId)sessionId.Value,
                audience,
                toolName,
                grants),
            TimeSpan.FromSeconds(5),
            ct);
        if (result.Failure is { } failure)
        {
            throw new InvalidOperationException($"The approval store is unavailable ({failure}).");
        }
    }
}

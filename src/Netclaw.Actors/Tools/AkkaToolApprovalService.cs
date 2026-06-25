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
        => await GetUnapprovedPatternsAsync(ToApprovalSessionId(sessionId), audience, toolName, patterns, cwd, ct);

    public async Task<IReadOnlyList<string>> GetUnapprovedPatternsAsync(
        ToolApprovalSessionId? sessionId,
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
        => await CheckApprovalAsync(ToApprovalSessionId(sessionId), audience, toolName, candidates, cwd, ct);

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

    public async Task RecordApprovalAsync(
        string sessionId,
        TrustAudience audience,
        ToolName toolName,
        IReadOnlyList<string> patterns,
        bool persistent,
        string? cwd,
        CancellationToken ct = default)
        => await RecordApprovalAsync((ToolApprovalSessionId)sessionId, audience, toolName, patterns, persistent, cwd, ct);

    public async Task RecordApprovalAsync(
        ToolApprovalSessionId sessionId,
        TrustAudience audience,
        ToolName toolName,
        IReadOnlyList<string> patterns,
        bool persistent,
        string? cwd,
        CancellationToken ct = default)
    {
        var actor = await _actorProvider.GetAsync(ct);
        await actor.Ask<ToolApprovalRecorded>(
            new RecordToolApproval((SessionId)sessionId.Value, audience, toolName, patterns, persistent, cwd),
            TimeSpan.FromSeconds(5),
            ct);
    }

    private static ToolApprovalSessionId? ToApprovalSessionId(string? sessionId)
        => sessionId is null ? null : (ToolApprovalSessionId)sessionId;
}

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
        CancellationToken ct = default)
    {
        var actor = await _actorProvider.GetAsync(ct);
        var response = await actor.Ask<UnapprovedPatternsResponse>(
            new GetUnapprovedPatterns(sessionId is not null ? (SessionId)sessionId : null, audience, toolName, patterns),
            TimeSpan.FromSeconds(5),
            ct);

        return response.Patterns;
    }

    public async Task RecordApprovalAsync(
        string sessionId,
        TrustAudience audience,
        ToolName toolName,
        IReadOnlyList<string> patterns,
        bool persistent,
        CancellationToken ct = default)
    {
        var actor = await _actorProvider.GetAsync(ct);
        await actor.Ask<ToolApprovalRecorded>(
            new RecordToolApproval((SessionId)sessionId, audience, toolName, patterns, persistent),
            TimeSpan.FromSeconds(5),
            ct);
    }
}

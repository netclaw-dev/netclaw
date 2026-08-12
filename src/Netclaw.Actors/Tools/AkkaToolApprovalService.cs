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

public sealed class AkkaToolApprovalService : IToolApprovalService, IStructuredToolApprovalService
{
    private readonly IRequiredActor<ToolApprovalActorKey> _actorProvider;
    private readonly ShellExecutionEnvironment? _compatibilityEnvironment;

    public AkkaToolApprovalService(IRequiredActor<ToolApprovalActorKey> actorProvider)
    {
        _actorProvider = actorProvider;
    }

    public AkkaToolApprovalService(
        IRequiredActor<ToolApprovalActorKey> actorProvider,
        ShellExecutionEnvironment compatibilityEnvironment)
    {
        _actorProvider = actorProvider;
        _compatibilityEnvironment = compatibilityEnvironment;
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
        var candidates = patterns.Select(CreateCompatibilityCandidate).ToList();
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
        if (string.Equals(toolName.Value, ShellTool.ToolName, StringComparison.Ordinal))
        {
            var grants = patterns
                .Select(pattern => new ToolApprovalGrant(
                    CreateCompatibilityCandidate(pattern),
                    cwd))
                .ToArray();
            if (grants.Any(static grant =>
                    grant.Candidate.Shell is null || grant.Candidate.VerbTokens is null))
            {
                throw new InvalidOperationException(
                    "A shell approval requires structured parser facts.");
            }

            await RecordApprovalCandidatesAsync(
                sessionId,
                audience,
                toolName,
                grants,
                persistent,
                ct);
            return;
        }

        var actor = await _actorProvider.GetAsync(ct);
        var result = await actor.Ask<ToolApprovalRecorded>(
            new RecordToolApproval((SessionId)sessionId.Value, audience, toolName, patterns, persistent, cwd),
            TimeSpan.FromSeconds(5),
            ct);
        if (result.Failure is { } failure)
        {
            throw new InvalidOperationException($"The approval store is unavailable ({failure}).");
        }
    }

    public async Task RecordApprovalCandidatesAsync(
        ToolApprovalSessionId sessionId,
        TrustAudience audience,
        ToolName toolName,
        IReadOnlyList<ToolApprovalGrant> grants,
        bool persistent,
        CancellationToken ct = default)
    {
        var actor = await _actorProvider.GetAsync(ct);
        var result = await actor.Ask<ToolApprovalRecorded>(
            new RecordStructuredToolApproval(
                (SessionId)sessionId.Value,
                audience,
                toolName,
                grants,
                persistent),
            TimeSpan.FromSeconds(5),
            ct);
        if (result.Failure is { } failure)
        {
            throw new InvalidOperationException($"The approval store is unavailable ({failure}).");
        }
    }

    private static ToolApprovalSessionId? ToApprovalSessionId(string? sessionId)
        => sessionId is null ? null : (ToolApprovalSessionId)sessionId;

    private ApprovalCandidate CreateCompatibilityCandidate(string pattern)
    {
        if (_compatibilityEnvironment is not null &&
            ShellApprovalGrantParser.TryCreateTokenPrefix(
                _compatibilityEnvironment,
                pattern,
                out var entry,
                out _))
        {
            return new ApprovalCandidate(pattern, Directory: null)
            {
                Shell = entry.Shell,
                VerbTokens = entry.VerbTokens,
            };
        }

        return new ApprovalCandidate(pattern, Directory: null);
    }

}

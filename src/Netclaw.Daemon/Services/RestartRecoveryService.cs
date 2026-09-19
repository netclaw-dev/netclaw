// -----------------------------------------------------------------------
// <copyright file="RestartRecoveryService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Pattern;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Channels;
using Netclaw.Daemon.Gateway;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Warms prior sessions and resumes only journal-confirmed work with a ready output route.
/// </summary>
public sealed class RestartRecoveryService : IHostedService
{
    private const string RestartNotice = "The daemon restarted. Recovery resumed from the last durable checkpoint.";
    private static readonly TimeSpan AttemptInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(10);

    private readonly RestartManifestStore _manifestStore;
    private readonly IRequiredActor<SessionManagerActorKey> _sessionManagerProvider;
    private readonly SessionCatalogService _sessionCatalog;
    private readonly IReadOnlyList<IChannel> _channels;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RestartRecoveryService> _logger;
    private readonly SemaphoreSlim _candidateGate = new(1, 1);
    private CancellationTokenSource? _lifetime;
    private Task? _recoveryTask;

    internal Task RecoveryTask => _recoveryTask ?? Task.CompletedTask;

    public RestartRecoveryService(
        RestartManifestStore manifestStore,
        IRequiredActor<SessionManagerActorKey> sessionManagerProvider,
        SessionCatalogService sessionCatalog,
        IEnumerable<IChannel> channels,
        TimeProvider timeProvider,
        ILogger<RestartRecoveryService> logger)
    {
        _manifestStore = manifestStore;
        _sessionManagerProvider = sessionManagerProvider;
        _sessionCatalog = sessionCatalog;
        _channels = channels.ToArray();
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionCatalog.ReconcileStaleActiveSessions();
        var manifest = await _manifestStore.ReadAsync(cancellationToken);
        if (manifest is null)
            return;

        _lifetime = new CancellationTokenSource();
        _recoveryTask = RecoverManifestAsync(manifest, _lifetime.Token);
    }

    private async Task RecoverManifestAsync(RestartManifest manifest, CancellationToken cancellationToken)
    {
        try
        {
            var sessionManager = await _sessionManagerProvider.GetAsync(cancellationToken);
            await Task.WhenAll(manifest.SessionIds.Select(async sessionIdValue =>
            {
                var sessionId = new SessionId(sessionIdValue);
                try
                {
                    var ack = await sessionManager.Ask<CommandAck>(
                        new WarmSession(sessionId, RestartNotice), AskTimeout, cancellationToken);
                    if (ack.SessionId != sessionId)
                        throw new InvalidOperationException("The warm ack returned a different session.");
                    _sessionCatalog.MarkSessionActive(sessionId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Failed to warm session {SessionId} during restart recovery.", sessionIdValue);
                }
            }));

            var remaining = manifest.ResumeCandidates.ToDictionary(
                candidate => candidate.SessionId.Value, StringComparer.Ordinal);
            if (remaining.Count == 0)
            {
                await _manifestStore.TryUpdateCandidatesAsync(manifest.GenerationId, [], cancellationToken);
                return;
            }

            await Task.WhenAll(remaining.Values.ToArray().Select(candidate =>
                RecoverCandidateAsync(sessionManager, manifest.GenerationId,
                    remaining, candidate, cancellationToken)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Restart recovery stopped with the daemon.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restart recovery stopped before it resolved all candidates.");
        }
    }

    private async Task RecoverCandidateAsync(
        IActorRef sessionManager,
        Guid generationId,
        Dictionary<string, RestartResumeCandidate> remaining,
        RestartResumeCandidate candidate,
        CancellationToken cancellationToken)
    {
        string? lastReason = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            if (candidate.DeadlineMs <= now)
            {
                _logger.LogWarning(
                    "Restart resume candidate for {SessionId} expired at {DeadlineMs}; accepted input remains in the journal.",
                    candidate.SessionId.Value, candidate.DeadlineMs);
                await CompleteCandidateAsync(generationId, remaining, candidate, cancellationToken);
                return;
            }

            var (terminal, success, reason) = await AttemptCandidateAsync(
                sessionManager, candidate, cancellationToken);
            if (terminal)
            {
                if (!success)
                    _logger.LogWarning(
                        "Restart resume candidate for {SessionId} was rejected: {Reason}",
                        candidate.SessionId.Value, reason);
                await CompleteCandidateAsync(generationId, remaining, candidate, cancellationToken);
                return;
            }

            if (lastReason != reason)
            {
                _logger.LogWarning(
                    "Restart resume candidate for {SessionId} awaits a safe output route: {Reason}",
                    candidate.SessionId.Value, reason);
                lastReason = reason;
            }

            var wait = TimeSpan.FromMilliseconds(Math.Min(
                AttemptInterval.TotalMilliseconds,
                candidate.DeadlineMs - _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()));
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, _timeProvider, cancellationToken);
        }
    }

    private async Task<(bool Terminal, bool Success, string Reason)> AttemptCandidateAsync(
        IActorRef sessionManager,
        RestartResumeCandidate candidate,
        CancellationToken cancellationToken)
    {
        try
        {
            var route = await sessionManager.Ask<RestartResumeRouteResult>(
                new GetRestartResumeRoute(candidate.SessionId, candidate), AskTimeout, cancellationToken);
            if (route.BlockedReason is { } blocked)
                return (true, false, blocked);
            if (route.Contexts.Count == 0 || route.SessionId != candidate.SessionId
                || !ChannelTypeExtensions.TryFromWireValue(route.Contexts[0].ChannelType, out var channelType))
                return (true, false, "The stored output route is invalid.");

            var channel = _channels.SingleOrDefault(item => item.ChannelType == channelType);
            if (channel is IRestartOutputBinder binder)
            {
                var binding = await binder.BindForRestartAsync(route, cancellationToken);
                if (!binding.Ready)
                    return (false, false, binding.Reason ?? "The output route is not ready.");
            }
            else if (channelType is not (ChannelType.Tui or ChannelType.SignalR or ChannelType.Headless))
            {
                return (false, false, $"No output binding exists for {channelType}.");
            }

            var response = await sessionManager.Ask<object>(
                new ResumeInterruptedSession(candidate.SessionId, candidate), AskTimeout, cancellationToken);
            return response switch
            {
                CommandAck ack when ack.SessionId == candidate.SessionId => (true, true, string.Empty),
                CommandNack nack => (false, false, nack.Reason),
                _ => (false, false, "The session returned an unexpected resume response.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, false, ex.Message);
        }
    }

    private async Task CompleteCandidateAsync(
        Guid generationId,
        Dictionary<string, RestartResumeCandidate> remaining,
        RestartResumeCandidate candidate,
        CancellationToken cancellationToken)
    {
        await _candidateGate.WaitAsync(cancellationToken);
        try
        {
            remaining.Remove(candidate.SessionId.Value);
            if (!await _manifestStore.TryUpdateCandidatesAsync(
                    generationId, remaining.Values.ToArray(), cancellationToken))
                _logger.LogWarning(
                    "Restart manifest changed before candidate {SessionId} reached a terminal decision.",
                    candidate.SessionId.Value);
        }
        finally
        {
            _candidateGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_lifetime is not { } lifetime)
            return;

        await lifetime.CancelAsync();
        if (_recoveryTask is { } task)
            await task.WaitAsync(cancellationToken);
        lifetime.Dispose();
    }
}

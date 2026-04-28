// -----------------------------------------------------------------------
// <copyright file="RestartRecoveryService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Pattern;
using Akka.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Daemon.Gateway;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Rehydrates the sessions that were active before coordinated restart began.
/// </summary>
public sealed class RestartRecoveryService : IHostedService
{
    private const string RestartNotice = "The daemon restarted due to a configuration change. Recovery resumed from the last durable checkpoint.";

    private readonly RestartManifestStore _manifestStore;
    private readonly IRequiredActor<SessionManagerActorKey> _sessionManagerProvider;
    private readonly SessionCatalogService _sessionCatalog;
    private readonly ILogger<RestartRecoveryService> _logger;

    public RestartRecoveryService(
        RestartManifestStore manifestStore,
        IRequiredActor<SessionManagerActorKey> sessionManagerProvider,
        SessionCatalogService sessionCatalog,
        ILogger<RestartRecoveryService> logger)
    {
        _manifestStore = manifestStore;
        _sessionManagerProvider = sessionManagerProvider;
        _sessionCatalog = sessionCatalog;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Reconcile stale 'active' sessions from the previous daemon lifetime.
        // Must run before reading the manifest so that re-warmed sessions get
        // a clean 'inactive' → 'active' transition via MarkSessionActive().
        _sessionCatalog.ReconcileStaleActiveSessions();

        var manifest = await _manifestStore.ReadAsync(cancellationToken);
        if (manifest is null)
            return;

        try
        {
            if (manifest.SessionIds.Count == 0)
                return;

            var sessionManager = await _sessionManagerProvider.GetAsync(cancellationToken);
            foreach (var sessionIdValue in manifest.SessionIds)
            {
                var sessionId = new SessionId(sessionIdValue);

                try
                {
                    await sessionManager.Ask<CommandAck>(
                        new WarmSession
                        {
                            SessionId = sessionId,
                            RestartNotice = RestartNotice
                        },
                        timeout: TimeSpan.FromSeconds(10),
                        cancellationToken: cancellationToken);

                    _sessionCatalog.MarkSessionActive(sessionId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to warm session {SessionId} during restart recovery.", sessionIdValue);
                }
            }

            _logger.LogInformation(
                "Restart recovery warmed {SessionCount} session(s); {TimedOutCount} were previously timed out during drain.",
                manifest.SessionIds.Count,
                manifest.TimedOutSessionIds.Count);
        }
        finally
        {
            await _manifestStore.DeleteAsync();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}

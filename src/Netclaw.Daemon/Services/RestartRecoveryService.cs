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
using Netclaw.Actors.Reminders;
using Netclaw.Daemon.Gateway;
using static Netclaw.Actors.Reminders.ReminderProtocol;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Registers short-lived reminders for work that a graceful stop interrupted.
/// </summary>
public sealed class RestartRecoveryService : IHostedService
{
    private static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReminderStartDelay = TimeSpan.FromMilliseconds(250);

    private readonly RestartManifestStore _manifestStore;
    private readonly IRequiredActor<ReminderManagerActorKey> _reminderManagerProvider;
    private readonly SessionCatalogService _sessionCatalog;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RestartRecoveryService> _logger;

    public RestartRecoveryService(
        RestartManifestStore manifestStore,
        IRequiredActor<ReminderManagerActorKey> reminderManagerProvider,
        SessionCatalogService sessionCatalog,
        TimeProvider timeProvider,
        ILogger<RestartRecoveryService> logger)
    {
        _manifestStore = manifestStore;
        _reminderManagerProvider = reminderManagerProvider;
        _sessionCatalog = sessionCatalog;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // The reminder path activates only the sessions that have work to resume.
        _sessionCatalog.ReconcileStaleActiveSessions();

        var manifest = await _manifestStore.ReadAsync(cancellationToken);
        if (manifest is null)
            return;

        if (manifest.RestartReminders.Count == 0)
        {
            await _manifestStore.DeleteAsync();
            return;
        }

        var reminderManager = await _reminderManagerProvider.GetAsync(cancellationToken);
        var now = _timeProvider.GetUtcNow();
        var results = await Task.WhenAll(manifest.RestartReminders.Select(
            reminder => RegisterAsync(reminderManager, reminder, now, cancellationToken)));

        if (results.All(static result => result))
            await _manifestStore.DeleteAsync();

        _logger.LogInformation(
            "Restart recovery read {ReminderCount} restart reminder(s).",
            manifest.RestartReminders.Count);
    }

    private async Task<bool> RegisterAsync(
        IActorRef reminderManager,
        ReminderDefinition stored,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (stored.ExpiresAt is not { } expiresAt || expiresAt <= now + ReminderStartDelay)
        {
            _logger.LogWarning(
                "Restart reminder {ReminderId} expired before startup recovery; the session stays quiet.",
                stored.Id.Value);
            return true;
        }

        var reminder = stored with
        {
            Schedule = stored.Schedule with { FireAt = now + ReminderStartDelay }
        };
        try
        {
            var result = await reminderManager.Ask<ReminderSavedResponse>(
                new SaveReminderCommand(
                    reminder,
                    ReminderWriteMode.CreateOnly,
                    new ReminderAudienceAuthorizationContext(
                        reminder.Audience,
                        "restart manifest")),
                timeout: AskTimeout,
                cancellationToken: cancellationToken);

            if (result.Success || result.Error == ReminderSaveError.Conflict)
                return true;

            _logger.LogWarning(
                "Restart reminder {ReminderId} could not register: {Reason}",
                reminder.Id.Value,
                result.ErrorMessage ?? result.Error.ToString());
            return false;
        }
        catch (AskTimeoutException ex)
        {
            _logger.LogWarning(
                ex,
                "Restart reminder {ReminderId} timed out during registration.",
                reminder.Id.Value);
            return false;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}

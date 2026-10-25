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
using Netclaw.Actors.Sessions;
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
    // The expiry signal must outlive its fire time, so the reminder manager delivers it after a slow startup.
    private static readonly TimeSpan ExpirySignalLifetime = TimeSpan.FromMinutes(10);
    // The reminder manager ends a one-shot whose expiry passed at its fire, with no delivery. A saved restart reminder
    // stays valid at least this long after its fire time, or startup sends the expiry signal in its place.
    private static readonly TimeSpan MinimumFireWindow = TimeSpan.FromMinutes(1);

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
        var fireAt = now + ReminderStartDelay;
        var reminder = stored with { Schedule = stored.Schedule with { FireAt = fireAt } };
        // An expired restart reminder becomes the expiry signal (#2659 design D1). It takes the same delivery route, so
        // the session closes the stopped work and the restart notice reaches the thread. The signal resumes nothing.
        if (stored.ExpiresAt is not { } expiresAt || expiresAt < fireAt + MinimumFireWindow)
        {
            _logger.LogWarning(
                "Restart reminder {ReminderId} expired before startup recovery; the session closes the stopped work with a notice.",
                stored.Id.Value);
            reminder = reminder with
            {
                Instructions = SessionProtocol.RestartExpiredInstruction,
                ExpiresAt = fireAt + ExpirySignalLifetime
            };
        }

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

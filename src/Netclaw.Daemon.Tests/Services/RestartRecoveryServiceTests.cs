// -----------------------------------------------------------------------
// <copyright file="RestartRecoveryServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Channels;
using Netclaw.Configuration;
using Netclaw.Daemon.Gateway;
using Netclaw.Daemon.Services;
using Netclaw.Tests.Utilities;
using Xunit;
using static Netclaw.Actors.Reminders.ReminderProtocol;

namespace Netclaw.Daemon.Tests.Services;

public sealed class RestartRecoveryServiceTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly ActorSystem _system;
    private readonly NetclawPaths _paths;

    public RestartRecoveryServiceTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        _system = ActorSystem.Create($"restart-recovery-tests-{Guid.NewGuid():N}");
    }

    /// <summary>
    /// A fresh restart reminder registers with the resume instruction. An expired one registers as the expiry signal
    /// (#2659): the same reminder with the expiry instruction, a fire time after startup, and an expiry after that fire
    /// time, so the reminder manager delivers it. The session closes the stopped work with one notice
    /// (RestartDrainSpecs row ExpiredReminderAfterToolBatch consumes this shape). A conflict counts as registered.
    /// Planted bug: startup drops the expired reminder, delivers it with the resume instruction, or keeps the past expiry.
    /// </summary>
    [Fact]
    public async Task StartAsync_registers_fresh_reminders_and_expiry_signals_for_expired_ones()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
        var saved = new ConcurrentQueue<SaveReminderCommand>();
        var reminderActor = _system.ActorOf(Props.Create(() => new ReminderActor(saved)));
        var manifestStore = new RestartManifestStore(_paths);
        var catalog = new SessionCatalogService(
            _paths,
            time,
            new TestSessionStorageResolver(_paths),
            NullLogger<SessionCatalogService>.Instance);

        await manifestStore.WriteAsync(new RestartManifest
        {
            RestartReminders =
            [
                CreateReminder("fresh", time.GetUtcNow().AddMinutes(10)),
                CreateReminder("stale", time.GetUtcNow().AddSeconds(-1))
            ]
        }, CancellationToken.None);

        var sut = new RestartRecoveryService(
            manifestStore,
            new StubRequiredActor<ReminderManagerActorKey>(reminderActor),
            catalog,
            time,
            NullLogger<RestartRecoveryService>.Instance);

        await sut.StartAsync(CancellationToken.None);

        Assert.Equal(["fresh", "stale"], saved.Select(static command => command.Definition.Id.Value).Order());
        var fresh = saved.Single(static command => command.Definition.Id.Value == "fresh");
        Assert.True(fresh.Definition.Schedule.FireAt > time.GetUtcNow());
        Assert.Equal(ResumeInstruction, fresh.Definition.Instructions);
        Assert.Equal(TrustAudience.Personal, fresh.Authorization?.SourceAudience);

        var signal = saved.Single(static command => command.Definition.Id.Value == "stale").Definition;
        Assert.Equal(SessionProtocol.RestartExpiredInstruction, signal.Instructions);
        Assert.True(signal.Schedule.FireAt > time.GetUtcNow(), "The expiry signal must fire after startup.");
        Assert.True(signal.ExpiresAt > signal.Schedule.FireAt, "The expiry signal must not expire before it fires.");
        Assert.Equal(DeliveryKind.CurrentSession, signal.Delivery.Kind);
        Assert.Equal("signalr/restart", signal.Delivery.SessionId);
        Assert.Equal(ChannelType.SignalR, signal.Delivery.OriginChannelType);
        Assert.Equal(TrustAudience.Personal, signal.Audience);
        Assert.Equal(TrustBoundary.Personal, signal.Boundary);
        Assert.Null(await manifestStore.ReadAsync(CancellationToken.None));
    }

    /// <summary>
    /// A restart reminder must never expire before the reminder manager can fire it (#2659). The manager ends a one-shot
    /// whose expiry has passed at its fire, with no delivery, so the session would get neither a resume nor the expiry
    /// signal. Every reminder that startup saves must stay valid for at least one minute after its fire time. Rows:
    /// near-expiry (the stored reminder expires 59 seconds after the startup fire time, inside the margin) and fresh
    /// (it expires 10 minutes after; the control).
    /// Planted bug: startup keeps the resume instruction and the stored expiry for a reminder that expires seconds after
    /// it fires.
    /// </summary>
    [Theory]
    [InlineData("near-expiry", 59)]
    [InlineData("fresh", 600)]
    public async Task StartAsync_never_saves_a_restart_reminder_that_expires_before_it_can_fire(string row, int secondsAfterFire)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
        var saved = new ConcurrentQueue<SaveReminderCommand>();
        var reminderActor = _system.ActorOf(Props.Create(() => new ReminderActor(saved)));
        var manifestStore = new RestartManifestStore(_paths);
        var catalog = new SessionCatalogService(
            _paths,
            time,
            new TestSessionStorageResolver(_paths),
            NullLogger<SessionCatalogService>.Instance);
        // Startup fires a restart reminder 250 ms after it starts.
        var fireAt = time.GetUtcNow().AddMilliseconds(250);
        await manifestStore.WriteAsync(new RestartManifest
        {
            RestartReminders = [CreateReminder(row, fireAt.AddSeconds(secondsAfterFire))]
        }, CancellationToken.None);
        var sut = new RestartRecoveryService(
            manifestStore,
            new StubRequiredActor<ReminderManagerActorKey>(reminderActor),
            catalog,
            time,
            NullLogger<RestartRecoveryService>.Instance);

        await sut.StartAsync(CancellationToken.None);

        var definition = Assert.Single(saved).Definition;
        var window = definition.ExpiresAt - definition.Schedule.FireAt;
        Assert.True(window >= TimeSpan.FromMinutes(1),
            $"Row {row}: startup saved a reminder with the instruction \"{definition.Instructions}\" that expires {window} after it fires.");
        if (row == "fresh")
            Assert.Equal(ResumeInstruction, definition.Instructions);
    }

    public void Dispose()
    {
        _system.Terminate().GetAwaiter().GetResult();
        SqliteTestPools.Clear(_paths);
        _dir.Dispose();
    }

    private const string ResumeInstruction = "Resume the work that was interrupted by the daemon restart.";

    private static ReminderDefinition CreateReminder(string id, DateTimeOffset expiresAt)
        => new()
        {
            Id = new ReminderId(id),
            Title = "Resume after daemon restart",
            Instructions = ResumeInstruction,
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.OneShot,
                FireAt = expiresAt.AddMinutes(-10)
            },
            Delivery = new ReminderDelivery
            {
                Kind = DeliveryKind.CurrentSession,
                SessionId = "signalr/restart",
                OriginChannelType = ChannelType.SignalR
            },
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.Personal,
            ExpiresAt = expiresAt
        };

    private sealed class StubRequiredActor<TKey> : IRequiredActor<TKey>
    {
        public StubRequiredActor(IActorRef actorRef)
        {
            ActorRef = actorRef;
        }

        public IActorRef ActorRef { get; }

        public Task<IActorRef> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ActorRef);
    }

    private sealed class ReminderActor : ReceiveActor
    {
        public ReminderActor(ConcurrentQueue<SaveReminderCommand> saved)
        {
            Receive<SaveReminderCommand>(command =>
            {
                saved.Enqueue(command);
                Sender.Tell(new ReminderSavedResponse(
                    command.Definition.Id,
                    command.Definition.Title,
                    Success: false,
                    NextFire: null,
                    Error: ReminderSaveError.Conflict,
                    ErrorMessage: "The reminder already exists."));
            });
        }
    }
}

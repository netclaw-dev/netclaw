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

    [Fact]
    public async Task StartAsync_registers_only_fresh_reminders_and_accepts_an_existing_definition()
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

        var command = Assert.Single(saved);
        Assert.Equal("fresh", command.Definition.Id.Value);
        Assert.True(command.Definition.Schedule.FireAt > time.GetUtcNow());
        Assert.Equal(TrustAudience.Personal, command.Authorization?.SourceAudience);
        Assert.Null(await manifestStore.ReadAsync(CancellationToken.None));
    }

    public void Dispose()
    {
        _system.Terminate().GetAwaiter().GetResult();
        SqliteTestPools.Clear(_paths);
        _dir.Dispose();
    }

    private static ReminderDefinition CreateReminder(string id, DateTimeOffset expiresAt)
        => new()
        {
            Id = new ReminderId(id),
            Title = "Resume after daemon restart",
            Instructions = "Resume the work that was interrupted by the daemon restart.",
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

// -----------------------------------------------------------------------
// <copyright file="RestartRecoveryServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Daemon.Gateway;
using Netclaw.Daemon.Services;
using Netclaw.Tests.Utilities;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

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
    public async Task StartAsync_warms_manifest_sessions_and_marks_catalog_active()
    {
        var warmedSessions = new ConcurrentQueue<WarmSession>();
        var actor = _system.ActorOf(Props.Create(() => new WarmSessionActor(warmedSessions)));
        var manifestStore = new RestartManifestStore(_paths);
        var catalog = new SessionCatalogService(
            _paths,
            TimeProvider.System,
            new TestSessionStorageResolver(_paths),
            NullLogger<SessionCatalogService>.Instance);
        var sessionId = new SessionId("slack/C123/1710000000.000001");

        catalog.OnSessionActivated(sessionId, Netclaw.Actors.Channels.ChannelType.Slack);
        catalog.OnSessionDeactivated(sessionId);

        await manifestStore.WriteAsync(new RestartManifest
        {
            Reason = "config-reload",
            RequestedAt = TimeProvider.System.GetUtcNow(),
            SessionIds = [sessionId.Value],
            TimedOutSessionIds = [sessionId.Value]
        }, CancellationToken.None);

        var sut = new RestartRecoveryService(
            manifestStore,
            new StubRequiredActor(actor),
            catalog,
            NullLogger<RestartRecoveryService>.Instance);

        await sut.StartAsync(CancellationToken.None);

        Assert.True(warmedSessions.TryDequeue(out var warmed));
        Assert.Equal(sessionId, warmed!.SessionId);
        Assert.Contains("last durable checkpoint", warmed.RestartNotice, StringComparison.Ordinal);
        Assert.Null(await manifestStore.ReadAsync(CancellationToken.None));

        var entry = Assert.Single(catalog.ListRecent());
        Assert.Equal("active", entry.Status);
    }

    public void Dispose()
    {
        _system.Terminate().GetAwaiter().GetResult();
        SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }

    private sealed class StubRequiredActor : IRequiredActor<SessionManagerActorKey>
    {
        public StubRequiredActor(IActorRef actorRef)
        {
            ActorRef = actorRef;
        }

        public IActorRef ActorRef { get; }

        public Task<IActorRef> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ActorRef);
    }

    private sealed class WarmSessionActor : ReceiveActor
    {
        public WarmSessionActor(ConcurrentQueue<WarmSession> warmedSessions)
        {
            Receive<WarmSession>(msg =>
            {
                warmedSessions.Enqueue(msg);
                Sender.Tell(CommandAck.For(msg.SessionId));
            });
        }
    }
}

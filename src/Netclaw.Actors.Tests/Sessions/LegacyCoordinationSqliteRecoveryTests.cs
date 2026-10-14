// -----------------------------------------------------------------------
// <copyright file="LegacyCoordinationSqliteRecoveryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;
using Akka;
using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence;
using Akka.Persistence.Hosting;
using Akka.Persistence.Sql.Hosting;
using Akka.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Serialization;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class LegacyCoordinationSqliteRecoveryTests : LlmSessionTestBase
{
    private const string SessionValue = "signalr/child-ledger-owner";
    private TestSessionTempDirectory? _sqliteDirectory;
    private readonly FakeChatClient _chatClient = new();
    private readonly CountingToolExecutor _toolExecutor = new();

    public LegacyCoordinationSqliteRecoveryTests(ITestOutputHelper output) : base(output) { }

    protected override void ConfigureSessionPersistence(AkkaConfigurationBuilder builder)
    {
        _sqliteDirectory = TestSessionTempDirectory.Create("netclaw-legacy-sqlite-");
        builder.WithSqlPersistence($"Data Source={Path.Combine(_sqliteDirectory.Path, "journal.db")}", "SQLite.MS");
    }

    protected override void ConfigureSessionSerialization(AkkaConfigurationBuilder builder)
        => builder
            .WithCustomSerializer(
                "netclaw-protobuf",
                [typeof(INetclawSerializableMessage), typeof(LegacyFixtureRecord)],
                system => new LegacyFixtureWriterSerializer(system))
            .WithStrictSerialization();

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chatClient));
        services.AddSingleton(new ModelCapabilities { ModelId = "legacy-recovery-model", ContextWindowTokens = 128_000 });
        services.AddSingleton(new SessionConfig { Tuning = new SessionTuning { TitleGenerationInterval = 0 } });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider("You are a test assistant."));
        services.AddSingleton<IToolExecutor>(_toolExecutor);
        services.AddSingleton(new ToolRegistry());
    }

    protected override void DeleteOwnedDirectories()
    {
        SqliteConnection.ClearAllPools();
        _sqliteDirectory?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Sqlite_recovery_replays_a_deployed_child_pair_and_settles_it_without_reexecution()
    {
        var sessionId = new SessionId(SessionValue);
        var persistenceId = $"session-{sessionId.Value}";
        var seeder = Sys.ActorOf(Props.Create(() => new LegacyFixtureSeeder(persistenceId)), "legacy-fixture-seeder");
        foreach (var name in new[]
        {
            "actor-origin-input-one", "actor-origin-input-two", "actor-parent-task-adoption",
            "actor-start-batch", "child-run-accepted", "child-started", "child-checkpoint",
            "child-terminal", "child-result-prepared", "child-delivery-admitted",
            "valid-child-delivery-adoption", "child-downstream-tool-batch",
            "child-downstream-tool-result", "child-downstream-final-turn"
        })
        {
            await seeder.Ask<Done>(new LegacyFixtureRecord(name), TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);
        }

        Watch(seeder);
        Sys.Stop(seeder);
        await ExpectTerminatedAsync(seeder, cancellationToken: TestContext.Current.CancellationToken);
        BackupAndRestoreSqliteDatabase();

        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("legacy-sqlite-session");
        var joined = await JoinSessionAsync(manager, subscriber, sessionId, OutputFilter.Full);

        Assert.Equal(1, joined.TurnCount);
        Assert.Equal(0, _chatClient.CallCount);
        Assert.Empty(_toolExecutor.Calls);

        await manager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Continue after the recovered child result."
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<TurnCompleted>(_ => true, TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, _chatClient.CallCount);
        Assert.Empty(_toolExecutor.Calls);
        var messages = Assert.Single(_chatClient.ReceivedMessages);
        var originalCalls = messages.SelectMany(static message => message.Contents.OfType<FunctionCallContent>())
            .Where(static call => call.CallId == "legacy-delivery-call")
            .ToArray();
        var originalResults = messages.SelectMany(static message => message.Contents.OfType<FunctionResultContent>())
            .Where(static result => result.CallId == "legacy-delivery-call")
            .ToArray();
        Assert.Single(originalCalls);
        Assert.Equal("spawn_agent", originalCalls[0].Name);
        Assert.Single(originalResults);
        Assert.Equal(originalCalls[0].CallId, originalResults[0].CallId);
        Assert.Contains("accepted-run", originalResults[0].Result?.ToString(), StringComparison.Ordinal);
        Assert.Contains("The child accepted-run confirmed its result.", originalResults[0].Result?.ToString(),
            StringComparison.Ordinal);

        var downstreamCall = Assert.Single(messages.SelectMany(static message => message.Contents.OfType<FunctionCallContent>()),
            static call => call.CallId == "downstream-call");
        Assert.Equal("file_read", downstreamCall.Name);
        var downstreamResult = Assert.Single(messages.SelectMany(static message => message.Contents.OfType<FunctionResultContent>()),
            static result => result.CallId == "downstream-call");
        Assert.Contains("Neutral downstream result.", downstreamResult.Result?.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(messages, static message => message.Role == Microsoft.Extensions.AI.ChatRole.User
            && message.Text?.Contains("The child accepted-run confirmed", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Sqlite_recovery_keeps_an_ambiguous_snapshot_inert_and_out_of_the_input_queue()
    {
        var sessionId = new SessionId(SessionValue);
        var persistenceId = $"session-{sessionId.Value}";
        var seeder = Sys.ActorOf(Props.Create(() => new LegacyFixtureSeeder(persistenceId)), "ambiguous-snapshot-seeder");
        await seeder.Ask<Done>(new LegacySnapshotRecord("ambiguous-child-delivery-snapshot"),
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Watch(seeder);
        Sys.Stop(seeder);
        await ExpectTerminatedAsync(seeder, cancellationToken: TestContext.Current.CancellationToken);
        BackupAndRestoreSqliteDatabase();

        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("ambiguous-snapshot-session");
        var joined = await JoinSessionAsync(manager, subscriber, sessionId, OutputFilter.Full);

        Assert.Equal(0, joined.TurnCount);
        Assert.Equal(0, _chatClient.CallCount);
        Assert.Empty(_toolExecutor.Calls);

        await manager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Continue after the quarantined legacy snapshot."
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<TurnCompleted>(_ => true, TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, _chatClient.CallCount);
        Assert.Empty(_toolExecutor.Calls);
        Assert.DoesNotContain(_chatClient.ReceivedMessages.SelectMany(static messages => messages),
            static message => message.Role == Microsoft.Extensions.AI.ChatRole.User
                && message.Text?.Contains("The child accepted-run confirmed", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Sqlite_snapshot_cold_restore_delivers_one_prepared_child_pair()
    {
        var sessionId = new SessionId(SessionValue);
        var persistenceId = $"session-{sessionId.Value}";
        var seeder = Sys.ActorOf(Props.Create(() => new LegacyFixtureSeeder(persistenceId)), "prepared-snapshot-seeder");
        await seeder.Ask<Done>(new LegacySnapshotRecord("prepared-undelivered-child-snapshot"),
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Watch(seeder);
        Sys.Stop(seeder);
        await ExpectTerminatedAsync(seeder, cancellationToken: TestContext.Current.CancellationToken);
        BackupAndRestoreSqliteDatabase();

        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("prepared-snapshot-session");
        await JoinSessionAsync(manager, subscriber, sessionId, OutputFilter.Full);
        await _chatClient.FirstCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<TurnCompleted>(_ => true, TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

        var messages = Assert.Single(_chatClient.ReceivedMessages);
        var childCalls = messages.SelectMany(static message => message.Contents.OfType<FunctionCallContent>())
            .Where(static call => call.Name == "child_run_result")
            .ToArray();
        var childResults = messages.SelectMany(static message => message.Contents.OfType<FunctionResultContent>())
            .Where(result => childCalls.Any(call => call.CallId == result.CallId))
            .ToArray();
        var childCall = Assert.Single(childCalls);
        Assert.Single(childResults);
        Assert.Equal(childCall.CallId, childResults[0].CallId);
        Assert.Equal("accepted-run", childCall.Arguments?["run_id"]?.ToString());
        Assert.Contains("The child accepted-run confirmed its result.", childResults[0].Result?.ToString(),
            StringComparison.Ordinal);
        Assert.Empty(_toolExecutor.Calls);
        Assert.Equal(1, _chatClient.CallCount);
    }

    private sealed record LegacyFixtureRecord(string Name);
    private sealed record LegacySnapshotRecord(string Name);

    private void BackupAndRestoreSqliteDatabase()
    {
        var directory = _sqliteDirectory?.Path
            ?? throw new InvalidOperationException("The test SQLite directory was not configured.");
        var databasePath = Path.Combine(directory, "journal.db");
        var backupPath = Path.Combine(directory, "restored-backup.db");
        var connectionString = $"Data Source={databasePath}";
        using (var source = new SqliteConnection(connectionString))
        using (var destination = new SqliteConnection($"Data Source={backupPath}"))
        {
            source.Open();
            destination.Open();
            source.BackupDatabase(destination);
        }

        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var sidecarPath = databasePath + suffix;
            if (File.Exists(sidecarPath))
                File.Delete(sidecarPath);
        }
        File.Copy(backupPath, databasePath, overwrite: true);
    }

    private sealed class LegacyFixtureSeeder : ReceivePersistentActor
    {
        private IActorRef? _snapshotReplyTo;

        public override string PersistenceId { get; }

        public LegacyFixtureSeeder(string persistenceId)
        {
            PersistenceId = persistenceId;
            RecoverAny(_ => { });
            Command<LegacyFixtureRecord>(fixture =>
            {
                var reply = Sender;
                Persist(fixture, _ => reply.Tell(Done.Instance));
            });
            Command<LegacySnapshotRecord>(fixture =>
            {
                _snapshotReplyTo = Sender;
                SaveSnapshot(new LegacyFixtureRecord(fixture.Name));
            });
            Command<SaveSnapshotSuccess>(_ =>
            {
                _snapshotReplyTo?.Tell(Done.Instance);
                _snapshotReplyTo = null;
            });
            Command<SaveSnapshotFailure>(failure =>
            {
                _snapshotReplyTo?.Tell(new Status.Failure(failure.Cause));
                _snapshotReplyTo = null;
            });
        }
    }

    private sealed class LegacyFixtureWriterSerializer : SerializerWithStringManifest
    {
        private readonly NetclawProtobufSerializer _reader;
        private readonly IReadOnlyDictionary<string, FixtureRecord> _records;

        public LegacyFixtureWriterSerializer(ExtendedActorSystem system) : base(system)
        {
            _reader = new NetclawProtobufSerializer(system);
            var corpus = JsonSerializer.Deserialize<FixtureCorpus>(File.ReadAllBytes(Path.Combine(
                AppContext.BaseDirectory, "Fixtures", "CoordinationLegacy", "legacy-writer-records.json")))!;
            Assert.Equal("9f79c0c4b637f005ffba5572d0bf50aa550464f2", corpus.SourceRevision);
            Assert.Equal(150, corpus.SerializerIdentifier);
            _records = corpus.Records.ToDictionary(static record => record.Name, StringComparer.Ordinal);
        }

        public override int Identifier => 150;

        public override string Manifest(object o)
            => o is LegacyFixtureRecord fixture
                ? _records[fixture.Name].Manifest
                : _reader.Manifest(o);

        public override byte[] ToBinary(object obj)
            => obj is LegacyFixtureRecord fixture
                ? Convert.FromBase64String(_records[fixture.Name].Base64)
                : _reader.ToBinary(obj);

        public override object FromBinary(byte[] bytes, string manifest)
            => _reader.FromBinary(bytes, manifest);
    }

    private sealed class FixtureCorpus
    {
        [JsonPropertyName("sourceRevision")] public string SourceRevision { get; init; } = string.Empty;
        [JsonPropertyName("serializerIdentifier")] public int SerializerIdentifier { get; init; }
        [JsonPropertyName("records")] public FixtureRecord[] Records { get; init; } = [];
    }

    private sealed class FixtureRecord
    {
        [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
        [JsonPropertyName("manifest")] public string Manifest { get; init; } = string.Empty;
        [JsonPropertyName("base64")] public string Base64 { get; init; } = string.Empty;
    }

    private sealed class SingleClientProvider(IChatClient client) : IChatClientProvider
    {
        public IChatClient GetClient(ModelRole role) => client;
    }

    private sealed class CountingToolExecutor : IToolExecutor
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _calls = new();

        public IReadOnlyCollection<string> Calls => _calls.ToArray();

        public Task<string> ExecuteAsync(FunctionCallContent toolCall, ToolExecutionContext context,
            CancellationToken ct = default)
        {
            _calls.Enqueue(toolCall.Name);
            return Task.FromResult("unused");
        }
    }
}

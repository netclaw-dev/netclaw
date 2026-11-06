// -----------------------------------------------------------------------
// <copyright file="RecordedJournalTestBase.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;
using Akka;
using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence;
using Akka.Persistence.Sql.Hosting;
using Akka.Serialization;
using Microsoft.Data.Sqlite;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Serialization;
using Netclaw.Tests.Utilities;
using Xunit;
using AkkaPersistence = Akka.Persistence.Persistence;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Shared setup for specs that replay journal bytes which an earlier writer recorded. A corpus file under
/// <c>Fixtures</c> holds each record as a manifest and a payload. The base writes the named records into a real SQLite
/// journal or snapshot store, copies the database to settle it, and lets a spec start a session over those rows. The
/// session then recovers through the production reader, as a daemon does after an upgrade.
/// </summary>
public abstract class RecordedJournalTestBase : ChildNoticeTestBase
{
    private readonly string _corpusFolder;
    private readonly string _corpusFile;
    private readonly string _sourceRevision;
    private TestSessionTempDirectory? _sqliteDirectory;

    protected RecordedJournalTestBase(ITestOutputHelper output, string corpusFolder, string corpusFile, string sourceRevision)
        : base(output)
    {
        _corpusFolder = corpusFolder;
        _corpusFile = corpusFile;
        _sourceRevision = sourceRevision;
    }

    protected sealed override void ConfigureSessionPersistence(AkkaConfigurationBuilder builder)
    {
        _sqliteDirectory = TestSessionTempDirectory.Create("netclaw-recorded-sqlite-");
        builder.WithSqlPersistence($"Data Source={Path.Combine(_sqliteDirectory.Path, "journal.db")}", "SQLite.MS");
    }

    protected sealed override void ConfigureSessionSerialization(AkkaConfigurationBuilder builder)
        => builder
            .WithCustomSerializer(
                "netclaw-protobuf",
                [typeof(INetclawSerializableMessage), typeof(RecordedRow)],
                system => new RecordedWriterSerializer(system, _corpusFolder, _corpusFile, _sourceRevision))
            .WithStrictSerialization();

    protected override void DeleteOwnedDirectories()
        => SqlPersistencePlugins.WaitForStopThenDelete(Sys, _sqliteDirectory);

    /// <summary>Writes the named corpus records into the journal of one persistence id, in order.</summary>
    protected async Task SeedJournalAsync(string persistenceId, string seederName, params string[] recordNames)
    {
        var seeder = await StartSeederAsync(persistenceId, seederName);
        foreach (var name in recordNames)
        {
            await seeder.Ask<Done>(new RecordedRow(name), TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }

        await StopSeederAsync(seeder);
    }

    /// <summary>Writes the named corpus record as the snapshot of one persistence id.</summary>
    protected async Task SeedSnapshotAsync(string persistenceId, string seederName, string recordName)
    {
        var seeder = await StartSeederAsync(persistenceId, seederName);
        await seeder.Ask<Done>(new RecordedSnapshot(recordName), TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await StopSeederAsync(seeder);
    }

    private async Task StopSeederAsync(IActorRef seeder)
    {
        Watch(seeder);
        Sys.Stop(seeder);
        await ExpectTerminatedAsync(seeder, cancellationToken: TestContext.Current.CancellationToken);
        BackupAndRestoreSqliteDatabase();
    }

    /// <summary>
    /// Starts a seeder and waits for its recovery. The first call starts the SQL persistence plugins, one at a time,
    /// and each plugin creates its tables on the new SQLite file. That work gets the cold recovery ceiling. The
    /// fixture Asks after recovery time only warm writes.
    /// </summary>
    private async Task<IActorRef> StartSeederAsync(string persistenceId, string actorName)
    {
        await SqlPersistencePlugins.StartOneAtATimeAsync(this, ColdRecoveryCeiling, SqliteDatabasePath());
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seeder = Sys.ActorOf(Props.Create(() => new RecordedRowSeeder(persistenceId, recovered)), actorName);
        await recovered.Task.WaitAsync(ColdRecoveryCeiling, TestContext.Current.CancellationToken);
        return seeder;
    }

    private string SqliteDatabasePath()
        => Path.Combine(
            _sqliteDirectory?.Path ?? throw new InvalidOperationException("The test SQLite directory was not configured."),
            "journal.db");

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

    private sealed record RecordedRow(string Name);
    private sealed record RecordedSnapshot(string Name);

    /// <summary>
    /// Writes recorded rows. A journal or snapshot failure goes back to the waiting test as an exception. Without
    /// these replies, the seeder stops silently and the test sees only an Ask timeout.
    /// </summary>
    private sealed class RecordedRowSeeder : ReceivePersistentActor
    {
        private readonly TaskCompletionSource _recovered;
        private IActorRef? _replyTo;

        public override string PersistenceId { get; }

        public RecordedRowSeeder(string persistenceId, TaskCompletionSource recovered)
        {
            PersistenceId = persistenceId;
            _recovered = recovered;
            Recover<RecoveryCompleted>(_ => _recovered.TrySetResult());
            RecoverAny(_ => { });
            // Each test waits for each reply before it sends the next command.
            // Thus only one reply target is open at a time. SaveSnapshot does
            // not stash commands, so the actor itself gives no such guarantee.
            Command<RecordedRow>(row =>
            {
                _replyTo = Sender;
                Persist(row, _ => Reply(Done.Instance));
            });
            Command<RecordedSnapshot>(row =>
            {
                _replyTo = Sender;
                SaveSnapshot(new RecordedRow(row.Name));
            });
            Command<SaveSnapshotSuccess>(_ => Reply(Done.Instance));
            Command<SaveSnapshotFailure>(failure => Reply(new Status.Failure(failure.Cause)));
        }

        protected override void OnRecoveryFailure(Exception reason, object? message = null)
        {
            _recovered.TrySetException(reason);
            base.OnRecoveryFailure(reason, message);
        }

        protected override void OnPersistFailure(Exception cause, object @event, long sequenceNr)
        {
            Reply(new Status.Failure(cause));
            base.OnPersistFailure(cause, @event, sequenceNr);
        }

        protected override void OnPersistRejected(Exception cause, object @event, long sequenceNr)
        {
            Reply(new Status.Failure(cause));
            base.OnPersistRejected(cause, @event, sequenceNr);
        }

        protected override void PostStop()
        {
            // A stop before RecoveryCompleted (for example a plugin start
            // failure) must fail the start wait at once.
            _recovered.TrySetException(new InvalidOperationException(
                $"The seeder for {PersistenceId} stopped before its recovery completed."));
            base.PostStop();
        }

        private void Reply(object message)
        {
            _replyTo?.Tell(message);
            _replyTo = null;
        }
    }

    private sealed class RecordedWriterSerializer : SerializerWithStringManifest
    {
        private readonly NetclawProtobufSerializer _reader;
        private readonly IReadOnlyDictionary<string, CorpusRecord> _records;

        public RecordedWriterSerializer(ExtendedActorSystem system, string folder, string file, string sourceRevision)
            : base(system)
        {
            _reader = new NetclawProtobufSerializer(system);
            var corpus = JsonSerializer.Deserialize<Corpus>(File.ReadAllBytes(Path.Combine(
                AppContext.BaseDirectory, "Fixtures", folder, file)))!;
            Assert.Equal(sourceRevision, corpus.SourceRevision);
            Assert.Equal(150, corpus.SerializerIdentifier);
            _records = corpus.Records.ToDictionary(static record => record.Name, StringComparer.Ordinal);
        }

        public override int Identifier => 150;

        public override string Manifest(object o)
            => o is RecordedRow row
                ? _records[row.Name].Manifest
                : _reader.Manifest(o);

        public override byte[] ToBinary(object obj)
            => obj is RecordedRow row
                ? Convert.FromBase64String(_records[row.Name].Base64)
                : _reader.ToBinary(obj);

        public override object FromBinary(byte[] bytes, string manifest)
            => _reader.FromBinary(bytes, manifest);
    }

    private sealed class Corpus
    {
        [JsonPropertyName("sourceRevision")] public string SourceRevision { get; init; } = string.Empty;
        [JsonPropertyName("serializerIdentifier")] public int SerializerIdentifier { get; init; }
        [JsonPropertyName("records")] public CorpusRecord[] Records { get; init; } = [];
    }

    private sealed class CorpusRecord
    {
        [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
        [JsonPropertyName("manifest")] public string Manifest { get; init; } = string.Empty;
        [JsonPropertyName("base64")] public string Base64 { get; init; } = string.Empty;
    }
}

/// <summary>The start of the SQL persistence plugins for a spec on a new SQLite file.</summary>
internal static class SqlPersistencePlugins
{
    /// <summary>
    /// Starts the journal and then the snapshot store, before a session or a seeder starts them both at once. Each SQL
    /// plugin creates its tables in <c>PreStart</c> and replies only after that. Two plugins that start together make two
    /// connections fight for the write lock of the new SQLite file. Microsoft.Data.Sqlite sets no SQLite busy
    /// handler. The connection that loses sleeps 150 ms and tries again, and only its 30 s command timeout ends the
    /// fight. That timeout is equal to the cold ceiling, so a long fight fails the start wait with no cause (#2599).
    /// One plugin at a time has no fight. A warm plugin answers at once.
    /// </summary>
    public static async Task StartOneAtATimeAsync(Akka.TestKit.TestKitBase kit, TimeSpan ceiling, string databasePath)
    {
        const string unusedId = "recorded-journal-plugin-start";
        var persistence = AkkaPersistence.Instance.Apply(kit.Sys);
        var probe = kit.CreateTestProbe();
        var journal = persistence.JournalFor(null);
        journal.Tell(new ReplayMessages(0, long.MaxValue, long.MaxValue, unusedId, probe.Ref));
        await ExpectStartReplyAsync<RecoverySuccess>(probe, ceiling, "journal", journal, databasePath);
        var snapshotStore = persistence.SnapshotStoreFor(null);
        snapshotStore.Tell(
            new LoadSnapshot(unusedId, SnapshotSelectionCriteria.Latest, long.MaxValue), probe.Ref);
        await ExpectStartReplyAsync<LoadSnapshotResult>(probe, ceiling, "snapshot store", snapshotStore, databasePath);
    }

    // The plugin that does not reply is the one that failed to start. The message names it and the database file,
    // so a stall shows where it happened. A plain timeout would not say which plugin blocked.
    private static async Task ExpectStartReplyAsync<TReply>(
        Akka.TestKit.TestProbe probe, TimeSpan ceiling, string plugin, IActorRef pluginActor, string databasePath)
    {
        try
        {
            await probe.ExpectMsgAsync<TReply>(ceiling, cancellationToken: TestContext.Current.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"The SQL {plugin} plugin at {pluginActor.Path} did not send {typeof(TReply).Name} within "
                + $"{ceiling.TotalSeconds:0} s. The plugins start in this order: journal, snapshot store. "
                + $"Database: {databasePath} (file exists: {File.Exists(databasePath)}).", ex);
        }
    }

    // A fault ceiling for the stop of the actor system. It is not an orchestration delay.
    private static readonly TimeSpan TerminationCeiling = TimeSpan.FromSeconds(15);

    private const int DeleteAttempts = 3;

    /// <summary>
    /// Waits for the actor system to terminate, and then deletes the SQLite folder. The journal and the snapshot store
    /// are actors of the system. Each one holds a connection until it stops. A plugin that blocks in a SQL call
    /// cannot stop, and its connection stays open. Windows then refuses the delete with "journal.db in use". The wait is
    /// bounded. If it ends first, the method still tries the delete, and then reports the stall. The seeders of the
    /// base run in the same system, so this one wait covers them.
    /// </summary>
    public static void WaitForStopThenDelete(ActorSystem system, TestSessionTempDirectory? directory)
    {
        TimeoutException? stall = null;
        if (!system.WhenTerminated.Wait(TerminationCeiling))
        {
            stall = new TimeoutException(
                $"The actor system did not terminate within {TerminationCeiling.TotalSeconds:0} s. "
                + "A SQL persistence plugin actor probably still holds its connection.");
        }

        try
        {
            DeleteWithPoolClear(directory);
        }
        catch (IOException ex) when (stall is not null)
        {
            throw new AggregateException(stall, ex);
        }

        if (stall is not null)
            throw stall;
    }

    // Each attempt clears the pools first. A connection that a plugin returns late goes back to the pool, and only
    // a new clear closes it. DisposableTempDir retries the delete between the attempts.
    [SlopwatchSuppress("SW003", "A failed delete attempt is retried; the last attempt throws.")]
    private static void DeleteWithPoolClear(TestSessionTempDirectory? directory)
    {
        for (var attempt = 1; attempt <= DeleteAttempts; attempt++)
        {
            SqliteConnection.ClearAllPools();
            try
            {
                directory?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                return;
            }
            catch (IOException) when (attempt < DeleteAttempts)
            {
            }
        }
    }
}

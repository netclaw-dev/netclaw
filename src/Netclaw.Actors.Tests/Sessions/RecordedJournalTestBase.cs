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
    {
        SqliteConnection.ClearAllPools();
        _sqliteDirectory?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

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
    /// Starts a seeder and waits for its recovery. The first start of the SQL persistence plugins happens here: plugin
    /// creation, table creation on a new SQLite file, and recovery. That work gets the cold recovery ceiling. The
    /// fixture Asks after recovery time only warm writes.
    /// </summary>
    private async Task<IActorRef> StartSeederAsync(string persistenceId, string actorName)
    {
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seeder = Sys.ActorOf(Props.Create(() => new RecordedRowSeeder(persistenceId, recovered)), actorName);
        await recovered.Task.WaitAsync(ColdRecoveryCeiling, TestContext.Current.CancellationToken);
        return seeder;
    }

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

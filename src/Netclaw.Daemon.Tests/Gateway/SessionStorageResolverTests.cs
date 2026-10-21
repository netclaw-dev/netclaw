// -----------------------------------------------------------------------
// <copyright file="SessionStorageResolverTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Daemon.Gateway;
using Netclaw.Daemon.Services;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests.Gateway;

public sealed class SessionStorageResolverTests : IDisposable
{
    private readonly string _basePath = Path.Combine(
        Path.GetTempPath(),
        $"netclaw-session-storage-{Guid.NewGuid():N}");

    // These tests do not need the /tmp base, so the folder stays in the session envelope.
    private static readonly ManagedTemporaryLayout Deep = ManagedTemporaryLayout.InsideSessionStorage.Instance;

    public void Dispose()
    {
        SqliteTestPools.Clear(new NetclawPaths(_basePath));
        if (Directory.Exists(_basePath))
            Directory.Delete(_basePath, recursive: true);
    }

    [Fact]
    public async Task Concurrent_first_consumers_receive_one_persisted_envelope()
    {
        var paths = CreatePaths();
        await MigrateAsync(paths, paths.SqliteDbPath);
        var resolver = new SqliteSessionStorageResolver(paths, new FakeTimeProvider(), Deep);
        var sessionId = new SessionId("signalr/new-session");
        using var ready = new CountdownEvent(16);
        using var start = new ManualResetEventSlim();

        var tasks = Enumerable.Range(0, 16)
            .Select(_ => Task.Factory.StartNew(
                () => ResolveAfterSignal(resolver, sessionId, ready, start),
                TestContext.Current.CancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();
        ready.Wait(TestContext.Current.CancellationToken);
        start.Set();
        var resolutions = await Task.WhenAll(tasks);

        var root = Assert.IsType<SessionStorageBinding>(resolutions[0].Binding).EnvelopeRoot;
        Assert.All(resolutions, result => Assert.Equal(root, result.Binding?.EnvelopeRoot));
        Assert.Equal(1, CountBindings(paths));
    }

    [Fact]
    public async Task Resolution_does_not_inherit_state_from_a_shared_pooled_connection()
    {
        var paths = CreatePaths();
        await MigrateAsync(paths, paths.SqliteDbPath);
        using var pooledConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = paths.SqliteDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        pooledConnection.Open();
        using (var command = pooledConnection.CreateCommand())
        {
            // A pool can return a native handle with state from a previous owner.
            command.CommandText = "PRAGMA query_only=ON";
            command.ExecuteNonQuery();
        }
        pooledConnection.Close();

        var storage = new SqliteSessionStorageResolver(paths, new FakeTimeProvider(), Deep)
            .Resolve(new SessionId("signalr/isolated-connection"));

        Assert.NotNull(storage.Binding);
        Assert.Equal(1, CountBindings(paths));
    }

    [Fact]
    public async Task Fixture_cleanup_preserves_another_database_pool()
    {
        var paths = CreatePaths();
        await MigrateAsync(paths, paths.SqliteDbPath);
        Assert.Equal(0, CountBindings(paths));
        using var otherFixture = new SessionStorageResolverTests();
        var otherPaths = otherFixture.CreatePaths();
        using var otherConnection = new SqliteConnection($"Data Source={otherPaths.SqliteDbPath}");
        otherConnection.Open();
        var otherHandle = otherConnection.Handle;
        otherConnection.Close();

        Dispose();

        Assert.False(Directory.Exists(_basePath));
        otherConnection.Open();
        Assert.Same(otherHandle, otherConnection.Handle);
    }

    [Fact]
    public async Task Independent_resolvers_racing_first_use_receive_one_persisted_envelope()
    {
        var paths = CreatePaths();
        await MigrateAsync(paths, paths.SqliteDbPath);
        var sessionId = new SessionId("signalr/database-race");
        var first = new SqliteSessionStorageResolver(
            paths,
            new FakeTimeProvider(),
            Path.Combine(_basePath, "candidate-a"),
            Deep);
        var second = new SqliteSessionStorageResolver(
            paths,
            new FakeTimeProvider(),
            Path.Combine(_basePath, "candidate-b"),
            Deep);
        using var ready = new CountdownEvent(2);
        using var start = new ManualResetEventSlim();

        var firstTask = Task.Run(() => ResolveAfterSignal(first, sessionId, ready, start));
        var secondTask = Task.Run(() => ResolveAfterSignal(second, sessionId, ready, start));
        ready.Wait(TestContext.Current.CancellationToken);
        start.Set();
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.Equal(results[0].Binding, results[1].Binding);
        Assert.Equal(1, CountBindings(paths));
    }

    [Fact]
    public async Task Current_sessions_directory_wins_over_the_stored_root_when_it_changes()
    {
        var paths = CreatePaths();
        await MigrateAsync(paths, paths.SqliteDbPath);
        var resolver = new SqliteSessionStorageResolver(paths, new FakeTimeProvider(), Deep);
        var sessionId = new SessionId("signalr/stable-session");
        var first = resolver.Resolve(sessionId);
        Directory.CreateDirectory(first.Binding!.EnvelopeRoot.Value);
        var alternateSessionsRoot = Path.Combine(_basePath, "alternate-sessions");

        var second = new SqliteSessionStorageResolver(
            paths,
            new FakeTimeProvider(),
            alternateSessionsRoot,
            Deep).Resolve(sessionId);

        Assert.Equal(
            Path.Combine(alternateSessionsRoot, Path.GetFileName(first.Binding.EnvelopeRoot.Value)),
            second.Binding!.EnvelopeRoot.Value);
    }

    [Fact]
    public async Task Restored_home_at_another_path_resolves_sessions_under_the_new_home()
    {
        var oldHome = Path.Combine(Path.GetTempPath(), $"netclaw-old-{Guid.NewGuid():N}");
        var oldPaths = new NetclawPaths(oldHome);
        oldPaths.EnsureDirectoriesExist();
        var newPaths = CreatePaths();
        try
        {
            await MigrateAsync(oldPaths, oldPaths.SqliteDbPath);
            var sessionId = new SessionId("signalr/restored-session");
            var original = new SqliteSessionStorageResolver(oldPaths, new FakeTimeProvider(), Deep).Resolve(sessionId);
            var envelopeName = Path.GetFileName(original.Binding!.EnvelopeRoot.Value);
            Directory.CreateDirectory(original.SessionDirectory.Value);
            File.WriteAllText(Path.Combine(original.SessionDirectory.Value, "notes.txt"), "kept");

            // Restore: the database and the sessions tree move together; the old path no longer exists.
            SqliteTestPools.Clear(oldPaths);
            File.Copy(oldPaths.SqliteDbPath, newPaths.SqliteDbPath);
            Directory.Delete(newPaths.SessionsDirectory);
            Directory.Move(oldPaths.SessionsDirectory, newPaths.SessionsDirectory);
            Directory.Delete(oldHome, recursive: true);

            var restored = new SqliteSessionStorageResolver(newPaths, new FakeTimeProvider(), Deep).Resolve(sessionId);

            Assert.Equal(Path.Combine(newPaths.SessionsDirectory, envelopeName), restored.Binding!.EnvelopeRoot.Value);
            Assert.Equal("kept", File.ReadAllText(Path.Combine(restored.SessionDirectory.Value, "notes.txt")));
            Assert.StartsWith(newPaths.SessionsDirectory, restored.LogPath.Value, StringComparison.Ordinal);
            Assert.Equal(1, CountBindings(newPaths));
        }
        finally
        {
            SqliteTestPools.Clear(oldPaths);
            if (Directory.Exists(oldHome))
                Directory.Delete(oldHome, recursive: true);
        }
    }

    [Theory]
    [InlineData("/home/alice/.netclaw/sessions/signalr_a-0123456789abcdef", "signalr_a-0123456789abcdef")]
    [InlineData("/srv/sessions/data/.netclaw/sessions/signalr_a-0123456789abcdef", "signalr_a-0123456789abcdef")]
    [InlineData(@"C:\Users\alice\.netclaw\sessions\signalr_a-0123456789abcdef", "signalr_a-0123456789abcdef")]
    [InlineData(@"C:\Users\alice\.netclaw\sessions/signalr_a-0123456789abcdef", "signalr_a-0123456789abcdef")]
    public void Stored_root_that_does_not_exist_is_rebased_onto_the_current_sessions_directory(
        string stored,
        string envelopeName)
    {
        var sessionsDirectory = Path.Combine(_basePath, "sessions");

        var resolved = SqliteSessionStorageResolver.ResolveStoredEnvelopeRoot(stored, sessionsDirectory);

        Assert.Equal(Path.Combine(sessionsDirectory, envelopeName), resolved);
    }

    [Fact]
    public void Stored_root_already_under_the_current_sessions_directory_is_unchanged()
    {
        var sessionsDirectory = Path.Combine(_basePath, "sessions");
        var stored = Path.Combine(sessionsDirectory, "signalr_a-0123456789abcdef");

        Assert.Equal(stored, SqliteSessionStorageResolver.ResolveStoredEnvelopeRoot(stored, sessionsDirectory));
    }

    [Fact]
    public void Stored_root_that_still_exists_elsewhere_is_still_rebased()
    {
        var elsewhere = Path.Combine(_basePath, "other-home", "sessions", "signalr_a-0123456789abcdef");
        Directory.CreateDirectory(elsewhere);
        var sessionsDirectory = Path.Combine(_basePath, "sessions");

        var resolved = SqliteSessionStorageResolver.ResolveStoredEnvelopeRoot(elsewhere, sessionsDirectory);

        Assert.Equal(Path.Combine(sessionsDirectory, "signalr_a-0123456789abcdef"), resolved);
    }

    [Theory]
    [InlineData("/home/alice/.netclaw/elsewhere/signalr_a-0123456789abcdef")]
    [InlineData("/home/alice/.netclaw/sessions")]
    [InlineData("/home/alice/.netclaw/sessions/")]
    [InlineData("/home/alice/.netclaw/sessions/..")]
    [InlineData("/home/alice/.netclaw/sessions//empty-segment")]
    [InlineData("/home/alice/.netclaw/sessions/a/b/c")]
    [InlineData("/home/alice/.netclaw/Sessions/signalr_a-0123456789abcdef")]
    [InlineData("signalr_a-0123456789abcdef")]
    public void Stored_root_without_a_single_segment_envelope_under_sessions_is_unchanged(string stored)
    {
        Assert.Equal(
            stored,
            SqliteSessionStorageResolver.ResolveStoredEnvelopeRoot(stored, Path.Combine(_basePath, "sessions")));
    }

    [Fact]
    public async Task Old_directory_that_exists_and_a_current_folder_that_exists_resolve_to_the_current_home()
    {
        var oldHome = Path.Combine(Path.GetTempPath(), $"netclaw-old-{Guid.NewGuid():N}");
        var oldPaths = new NetclawPaths(oldHome);
        oldPaths.EnsureDirectoriesExist();
        var currentPaths = CreatePaths();
        try
        {
            await MigrateAsync(oldPaths, oldPaths.SqliteDbPath);
            var sessionId = new SessionId("signalr/copied-home");
            var original = new SqliteSessionStorageResolver(oldPaths, new FakeTimeProvider(), Deep).Resolve(sessionId);
            Directory.CreateDirectory(original.SessionDirectory.Value);
            File.WriteAllText(Path.Combine(original.SessionDirectory.Value, "notes.txt"), "original");
            SqliteTestPools.Clear(oldPaths);
            File.Copy(oldPaths.SqliteDbPath, currentPaths.SqliteDbPath);
            var currentEnvelope = Path.Combine(currentPaths.SessionsDirectory, Path.GetFileName(original.Binding!.EnvelopeRoot.Value));
            Directory.CreateDirectory(Path.Combine(currentEnvelope, "workspace"));
            File.WriteAllText(Path.Combine(currentEnvelope, "workspace", "notes.txt"), "copy");

            var log = new CapturingLogger();
            var resolved = new SqliteSessionStorageResolver(currentPaths, new FakeTimeProvider(), Deep, log).Resolve(sessionId);

            Assert.Equal(currentEnvelope, resolved.Binding!.EnvelopeRoot.Value);
            Assert.Equal("copy", File.ReadAllText(Path.Combine(resolved.SessionDirectory.Value, "notes.txt")));
            var warning = Assert.Single(log.Warnings);
            Assert.Contains(original.Binding.EnvelopeRoot.Value, warning, StringComparison.Ordinal);
            Assert.Contains(currentEnvelope, warning, StringComparison.Ordinal);
            Assert.DoesNotContain("does not exist yet", warning, StringComparison.Ordinal);
        }
        finally
        {
            SqliteTestPools.Clear(oldPaths);
            if (Directory.Exists(oldHome))
                Directory.Delete(oldHome, recursive: true);
        }
    }

    [Fact]
    public async Task Old_directory_that_exists_and_a_missing_current_folder_resolve_to_a_fresh_current_folder()
    {
        var oldHome = Path.Combine(Path.GetTempPath(), $"netclaw-old-{Guid.NewGuid():N}");
        var oldPaths = new NetclawPaths(oldHome);
        oldPaths.EnsureDirectoriesExist();
        var currentPaths = CreatePaths();
        try
        {
            await MigrateAsync(oldPaths, oldPaths.SqliteDbPath);
            var sessionId = new SessionId("signalr/recreated-old-path");
            var original = new SqliteSessionStorageResolver(oldPaths, new FakeTimeProvider(), Deep).Resolve(sessionId);
            Directory.CreateDirectory(original.SessionDirectory.Value);
            File.WriteAllText(Path.Combine(original.SessionDirectory.Value, "notes.txt"), "stranded");
            SqliteTestPools.Clear(oldPaths);
            File.Copy(oldPaths.SqliteDbPath, currentPaths.SqliteDbPath);

            var log = new CapturingLogger();
            var resolver = new SqliteSessionStorageResolver(currentPaths, new FakeTimeProvider(), Deep, log);
            var resolved = resolver.Resolve(sessionId);
            resolver.Resolve(sessionId);

            var currentEnvelope = Path.Combine(currentPaths.SessionsDirectory, Path.GetFileName(original.Binding!.EnvelopeRoot.Value));
            Assert.Equal(currentEnvelope, resolved.Binding!.EnvelopeRoot.Value);
            Assert.False(File.Exists(Path.Combine(resolved.SessionDirectory.Value, "notes.txt")));
            var warning = Assert.Single(log.Warnings);
            Assert.Contains("does not exist yet", warning, StringComparison.Ordinal);
        }
        finally
        {
            SqliteTestPools.Clear(oldPaths);
            if (Directory.Exists(oldHome))
                Directory.Delete(oldHome, recursive: true);
        }
    }

    [Fact]
    public async Task Spill_after_a_home_move_uses_the_session_folder_of_the_current_home()
    {
        // The spill writer creates the session folder when it is missing. It must
        // create the folder that the resolver gives for the current home, which is
        // the folder that the shell launcher and tool_output_read also use.
        var oldHome = Path.Combine(Path.GetTempPath(), $"netclaw-old-{Guid.NewGuid():N}");
        var oldPaths = new NetclawPaths(oldHome);
        oldPaths.EnsureDirectoriesExist();
        var currentPaths = CreatePaths();
        try
        {
            await MigrateAsync(oldPaths, oldPaths.SqliteDbPath);
            var sessionId = new SessionId("signalr/moved-spill");
            var original = new SqliteSessionStorageResolver(oldPaths, new FakeTimeProvider(), Deep).Resolve(sessionId);
            SqliteTestPools.Clear(oldPaths);
            File.Copy(oldPaths.SqliteDbPath, currentPaths.SqliteDbPath);
            var resolved = new SqliteSessionStorageResolver(currentPaths, new FakeTimeProvider(), Deep).Resolve(sessionId);
            Assert.False(Directory.Exists(resolved.SessionDirectory.Value));
            var options = new TestToolExecutionContextOptions { Audience = TrustAudience.Personal };

            var result = await ToolOutputSpill.BoundAndSpillAsync(
                new string('H', 200) + new string('M', 200) + new string('T', 200),
                "call_moved",
                budget: 100,
                TestToolExecutionContext.CreateBoundWithStorage(sessionId.Value, resolved, options).Invocation,
                NullLogger.Instance,
                TestContext.Current.CancellationToken);
            var continuation = await new ToolOutputReadTool().ExecuteAsync(
                ToolInput.Create("CallId", "call_moved", "Start", 200, "Limit", 200),
                TestToolExecutionContext.CreateBoundWithStorage(sessionId.Value, resolved, options),
                TestContext.Current.CancellationToken);

            Assert.Contains("CallId='call_moved'", result, StringComparison.Ordinal);
            Assert.StartsWith(new string('M', 100), continuation, StringComparison.Ordinal);
            Assert.StartsWith(currentPaths.SessionsDirectory, resolved.SessionDirectory.Value, StringComparison.Ordinal);
            Assert.True(Directory.Exists(Path.Combine(resolved.SessionDirectory.Value, "tool-calls")));
            Assert.False(Directory.Exists(original.SessionDirectory.Value));
        }
        finally
        {
            SqliteTestPools.Clear(oldPaths);
            if (Directory.Exists(oldHome))
                Directory.Delete(oldHome, recursive: true);
        }
    }

    [Fact]
    public async Task Concurrent_first_resolution_of_a_moved_session_logs_one_warning()
    {
        var paths = CreatePaths();
        await MigrateAsync(paths, paths.SqliteDbPath);
        var sessionId = new SessionId("signalr/moved-race");
        var stored = Path.Combine(Path.GetTempPath(), "netclaw-gone", "sessions", "signalr_moved-race-0123456789abcdef");
        using (var connection = new SqliteConnection($"Data Source={paths.SqliteDbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO session_storage_bindings(session_id, layout_version, envelope_root, created_at) VALUES ($id, 2, $root, 0)";
            command.Parameters.AddWithValue("$id", sessionId.Value);
            command.Parameters.AddWithValue("$root", stored);
            command.ExecuteNonQuery();
        }

        var log = new CapturingLogger();
        var resolver = new SqliteSessionStorageResolver(paths, new FakeTimeProvider(), Deep, log);
        using var ready = new CountdownEvent(16);
        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, 16)
            .Select(_ => Task.Factory.StartNew(
                () => ResolveAfterSignal(resolver, sessionId, ready, start),
                TestContext.Current.CancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();
        ready.Wait(TestContext.Current.CancellationToken);
        start.Set();
        await Task.WhenAll(tasks);

        Assert.Single(log.Warnings);
    }

    [Fact]
    public async Task Unmoved_home_logs_no_warning()
    {
        var paths = CreatePaths();
        await MigrateAsync(paths, paths.SqliteDbPath);
        var log = new CapturingLogger();
        var resolver = new SqliteSessionStorageResolver(paths, new FakeTimeProvider(), Deep, log);
        var sessionId = new SessionId("signalr/unmoved");
        resolver.Resolve(sessionId);
        new SqliteSessionStorageResolver(paths, new FakeTimeProvider(), Deep, log).Resolve(sessionId);

        Assert.Empty(log.Warnings);
    }

    [Fact]
    public async Task Existing_session_keeps_legacy_paths_and_receives_no_binding()
    {
        var paths = CreatePaths();
        await MigrateAsync(paths, paths.SqliteDbPath);
        var sessionId = new SessionId("signalr/existing-session");
        var legacyDirectory = SessionDirectoryHelper.GetSessionDirectory(sessionId, paths.SessionsDirectory);
        Directory.CreateDirectory(legacyDirectory);
        File.WriteAllText(Path.Combine(legacyDirectory, "existing.txt"), "keep");

        var storage = new SqliteSessionStorageResolver(paths, new FakeTimeProvider(), Deep).Resolve(sessionId);

        Assert.Null(storage.Binding);
        Assert.Equal(legacyDirectory, storage.SessionDirectory.Value);
        Assert.True(File.Exists(Path.Combine(legacyDirectory, "existing.txt")));
        Assert.Equal(0, CountBindings(paths));
    }

    [Fact]
    public async Task Distinct_session_ids_with_the_same_display_form_get_distinct_envelopes()
    {
        var paths = CreatePaths();
        await MigrateAsync(paths, paths.SqliteDbPath);
        var resolver = new SqliteSessionStorageResolver(paths, new FakeTimeProvider(), Deep);

        var first = resolver.Resolve(new SessionId("channel/a_b"));
        var second = resolver.Resolve(new SessionId("channel/a/b"));

        Assert.NotEqual(first.Binding?.EnvelopeRoot, second.Binding?.EnvelopeRoot);
        Assert.Equal(2, CountBindings(paths));
    }

    [Fact]
    public async Task Journal_only_session_keeps_legacy_paths_and_receives_no_binding()
    {
        var paths = CreatePaths();
        var sessionId = new SessionId("signalr/journal-only");
        await MigrateAsync(paths, paths.SqliteDbPath);
        using (var connection = new SqliteConnection($"Data Source={paths.SqliteDbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO journal(deleted, persistence_id, sequence_number, created, message)
                VALUES (0, $persistenceId, 1, 0, X'00');
                """;
            command.Parameters.AddWithValue("$persistenceId", $"session-{sessionId.Value}");
            command.ExecuteNonQuery();
        }

        var storage = new SqliteSessionStorageResolver(paths, new FakeTimeProvider(), Deep).Resolve(sessionId);

        Assert.Null(storage.Binding);
        Assert.Equal(
            SessionDirectoryHelper.GetSessionDirectory(sessionId, paths.SessionsDirectory),
            storage.SessionDirectory.Value);
        Assert.Equal(0, CountBindings(paths));
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("journal_metadata")]
    public async Task Compacted_persistence_evidence_keeps_legacy_paths_and_receives_no_binding(
        string evidenceTable)
    {
        var paths = CreatePaths();
        var sessionId = new SessionId($"signalr/{evidenceTable}-only");
        await MigrateAsync(paths, paths.SqliteDbPath);
        using (var connection = new SqliteConnection($"Data Source={paths.SqliteDbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = evidenceTable switch
            {
                "snapshot" =>
                    """
                    INSERT INTO snapshot(persistence_id, sequence_number, created, snapshot)
                    VALUES ($persistenceId, 1, 0, X'00')
                    """,
                "journal_metadata" =>
                    """
                    INSERT INTO journal_metadata(persistence_id, sequence_number)
                    VALUES ($persistenceId, 1)
                    """,
                _ => throw new ArgumentOutOfRangeException(nameof(evidenceTable), evidenceTable, null)
            };
            command.Parameters.AddWithValue("$persistenceId", $"session-{sessionId.Value}");
            command.ExecuteNonQuery();
        }

        var storage = new SqliteSessionStorageResolver(paths, new FakeTimeProvider(), Deep).Resolve(sessionId);

        Assert.Null(storage.Binding);
        Assert.Equal(0, CountBindings(paths));
    }

    [Fact]
    public async Task Repeated_resolution_uses_the_cached_immutable_result()
    {
        var paths = CreatePaths();
        await MigrateAsync(paths, paths.SqliteDbPath);
        var resolver = new SqliteSessionStorageResolver(paths, new FakeTimeProvider(), Deep);
        var sessionId = new SessionId("signalr/cached-session");

        var first = resolver.Resolve(sessionId);
        var second = resolver.Resolve(sessionId);

        Assert.Same(first, second);
        Assert.Equal(1, CountBindings(paths));
    }

    [Fact]
    public async Task Catalog_only_session_keeps_legacy_paths_and_receives_no_binding()
    {
        var paths = CreatePaths();
        await MigrateAsync(paths, paths.SqliteDbPath);
        var sessionId = new SessionId("signalr/catalog-only");

        using (var connection = new SqliteConnection($"Data Source={paths.SqliteDbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO sessions(persistence_id, channel, created_at, last_activity, status, turn_count)
                VALUES ($persistenceId, 'signalr', 0, 0, 'inactive', 0)
                """;
            command.Parameters.AddWithValue("$persistenceId", $"session-{sessionId.Value}");
            command.ExecuteNonQuery();
        }

        var storage = new SqliteSessionStorageResolver(paths, new FakeTimeProvider(), Deep).Resolve(sessionId);

        Assert.Null(storage.Binding);
        Assert.Equal(0, CountBindings(paths));
    }

    private sealed class CapturingLogger : ILogger<SqliteSessionStorageResolver>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }

    /// <summary>
    /// Issue #2554 cross-boundary contract: the resolver (producer) emits the
    /// short managed temporary layout, and the path access decision (consumer)
    /// accepts the key folder as a root of that session only.
    /// </summary>
    [Fact]
    public async Task Resolver_emits_the_private_temporary_layout_that_the_path_policy_accepts()
    {
        if (OperatingSystem.IsWindows())
            return;

        var paths = CreatePaths();
        await MigrateAsync(paths, paths.SqliteDbPath);
        var temporaryBase = Path.Combine(_basePath, "netclaw-base");
        var resolver = new SqliteSessionStorageResolver(
            paths,
            new FakeTimeProvider(),
            new ManagedTemporaryLayout.PrivateBase(new ManagedTemporaryBase(temporaryBase)));

        var storage = resolver.Resolve(new SessionId("signalr/short-temp"));
        var other = resolver.Resolve(new SessionId("signalr/other-session"));

        var envelope = Assert.IsType<SessionStorageBinding>(storage.Binding).EnvelopeRoot.Value;
        var key = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(envelope)))[..16];
        Assert.Equal(Path.Combine(temporaryBase, key, "p"), storage.ManagedTemporary.Directory.Value);
        Assert.Equal(Path.Combine(temporaryBase, key), storage.ManagedTemporary.StorageRoot.Value);

        var policy = new PathAccessPolicy(new ToolConfig(), paths, new ToolPathPolicy([]));
        var context = TestToolExecutionContext.CreateBoundWithStorage(
            "signalr/short-temp",
            storage,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Team,
                Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(TrustAudience.Team)
            }).Invocation;
        Assert.IsType<PathAccessPolicy.PathAccessDecision.Allowed>(policy.Evaluate(
            Path.Combine(storage.ManagedTemporary.Directory.Value, "frame.png"),
            context,
            PathAccessPolicy.FileOperation.Write));
        Assert.IsType<PathAccessPolicy.PathAccessDecision.Denied>(policy.Evaluate(
            Path.Combine(other.ManagedTemporary.Directory.Value, "frame.png"),
            context,
            PathAccessPolicy.FileOperation.Read));
    }

    /// <summary>
    /// PR #2559 review item S3. The managed temporary layout is a security-relevant
    /// dependency, so no resolver constructor may supply a hidden default (the old deep
    /// layout in the session envelope). The test builds the resolver through each
    /// constructor with the arguments that the daemon has (the paths, the clock, the
    /// sessions folder, and the host layout). Each resolver must emit the short layout of
    /// the given base. A constructor that cannot take the layout fails the test.
    /// </summary>
    [Fact]
    public async Task Each_resolver_constructor_emits_the_short_layout_of_the_given_base()
    {
        if (OperatingSystem.IsWindows())
            return;

        var paths = CreatePaths();
        await MigrateAsync(paths, paths.SqliteDbPath);
        var temporaryBase = new ManagedTemporaryBase(Path.Combine(_basePath, "netclaw-base"));
        var layout = new ManagedTemporaryLayout.PrivateBase(temporaryBase);
        var constructors = typeof(SqliteSessionStorageResolver).GetConstructors(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotEmpty(constructors);

        var number = 0;
        foreach (var constructor in constructors)
        {
            var arguments = constructor.GetParameters()
                .Select(parameter => parameter.ParameterType switch
                {
                    var type when type == typeof(NetclawPaths) => (object?)paths,
                    var type when type == typeof(TimeProvider) => new FakeTimeProvider(),
                    var type when type == typeof(string) => paths.SessionsDirectory,
                    var type when type == typeof(ManagedTemporaryLayout) => layout,
                    var type when type == typeof(ILogger<SqliteSessionStorageResolver>) => NullLogger<SqliteSessionStorageResolver>.Instance,
                    var type => throw new InvalidOperationException($"The test has no value for a parameter of type {type}.")
                })
                .ToArray();
            var resolver = (SqliteSessionStorageResolver)constructor.Invoke(arguments);
            var signature = string.Join(", ", constructor.GetParameters().Select(parameter => parameter.ParameterType.Name));

            var storage = resolver.Resolve(new SessionId($"signalr/constructor-{number++}"));

            Assert.True(
                storage.ManagedTemporary.PrivateBase == temporaryBase,
                $"The constructor ({signature}) put the managed temporary folder at '{storage.ManagedTemporary.Directory.Value}', not below '{temporaryBase.Value}'.");
            Assert.StartsWith(temporaryBase.Value + Path.DirectorySeparatorChar, storage.ManagedTemporary.Directory.Value);
        }
    }

    private NetclawPaths CreatePaths()
    {
        var paths = new NetclawPaths(_basePath);
        paths.EnsureDirectoriesExist();
        return paths;
    }

    private static long CountBindings(NetclawPaths paths)
    {
        using var connection = new SqliteConnection($"Data Source={paths.SqliteDbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM session_storage_bindings";
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    private static SessionStoragePaths ResolveAfterSignal(
        SqliteSessionStorageResolver resolver,
        SessionId sessionId,
        CountdownEvent ready,
        ManualResetEventSlim start)
    {
        ready.Signal();
        start.Wait(TestContext.Current.CancellationToken);
        return resolver.Resolve(sessionId);
    }

    private static Task MigrateAsync(NetclawPaths paths, string sqlitePath)
        => new SchemaMigrator(paths, NullLogger<SchemaMigrator>.Instance)
            .MigrateAsync(sqlitePath, TestContext.Current.CancellationToken);
}

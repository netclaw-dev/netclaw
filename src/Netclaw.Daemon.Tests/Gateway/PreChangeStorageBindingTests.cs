// -----------------------------------------------------------------------
// <copyright file="PreChangeStorageBindingTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Daemon.Gateway;
using Netclaw.Daemon.Services;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Gateway;

public sealed class PreChangeStorageBindingTests
{
    [Fact]
    public async Task Captured_old_binding_and_marker_survive_a_home_move_without_a_new_binding()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "LegacyPersistence", "legacy-storage-binding-v0.json");
        var fixture = JsonSerializer.Deserialize<CapturedStorageFixture>(
            await File.ReadAllTextAsync(fixturePath, TestContext.Current.CancellationToken),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("The storage fixture is empty.");
        Assert.Equal("2e6bc4f014dc96b606566709df1bd11f90ecf34a", fixture.Baseline);
        Assert.Equal("signalr/pre-change-persistence-proof", fixture.Row.SessionId);
        Assert.Equal(2, fixture.Row.LayoutVersion);
        var segments = fixture.Row.EnvelopeRoot.Split('/', '\\');
        Assert.Equal("sessions", segments[^2]);
        var envelopeName = segments[^1];
        Assert.NotEmpty(envelopeName);
        Assert.DoesNotContain(envelopeName, new[] { ".", ".." });
        var markerBytes = Convert.FromBase64String(fixture.MarkerBase64);
        Assert.Equal(fixture.MarkerSha256, Convert.ToHexString(SHA256.HashData(markerBytes)));

        using var directory = new DisposableTempDir();
        var paths = new NetclawPaths(directory.Path);
        paths.EnsureDirectoriesExist();
        try
        {
            await new SchemaMigrator(paths, NullLogger<SchemaMigrator>.Instance)
                .MigrateAsync(paths.SqliteDbPath, TestContext.Current.CancellationToken);
            using (var connection = new SqliteConnection($"Data Source={paths.SqliteDbPath};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO session_storage_bindings(session_id, layout_version, envelope_root, created_at)
                    VALUES ($id, $version, $root, $created);
                    """;
                command.Parameters.AddWithValue("$id", fixture.Row.SessionId);
                command.Parameters.AddWithValue("$version", fixture.Row.LayoutVersion);
                command.Parameters.AddWithValue("$root", fixture.Row.EnvelopeRoot);
                command.Parameters.AddWithValue("$created", fixture.Row.CreatedAt);
                command.ExecuteNonQuery();
            }
            var expectedRoot = Path.Combine(paths.SessionsDirectory, envelopeName);
            var markerPath = Path.Combine(expectedRoot, fixture.MarkerRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.StartsWith(expectedRoot + Path.DirectorySeparatorChar, Path.GetFullPath(markerPath), StringComparison.Ordinal);
            Directory.CreateDirectory(Path.GetDirectoryName(markerPath)
                ?? throw new InvalidOperationException("The marker needs a parent directory."));
            await File.WriteAllBytesAsync(markerPath, markerBytes, TestContext.Current.CancellationToken);

            var clock = new FakeTimeProvider(new DateTimeOffset(2035, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var session = new SessionId(fixture.Row.SessionId);
            var storage = new SqliteSessionStorageResolver(paths, clock).Resolve(session);
            Assert.NotNull(storage.Binding);
            Assert.Equal(expectedRoot, storage.Binding.EnvelopeRoot.Value);
            Assert.NotEqual(fixture.Row.EnvelopeRoot, storage.Binding.EnvelopeRoot.Value);
            Assert.Equal(Path.Combine(storage.SessionDirectory.Value, "neutral-marker.txt"), markerPath);
            Assert.Equal(markerBytes, await File.ReadAllBytesAsync(markerPath, TestContext.Current.CancellationToken));
            Assert.Equal(fixture.Row, CapturedStorageRow.Read(paths));
            var cold = new SqliteSessionStorageResolver(paths, clock).Resolve(session);
            Assert.Equal(storage.Binding, cold.Binding);
            Assert.Equal(fixture.Row, CapturedStorageRow.Read(paths));
        }
        finally
        {
            SqliteTestPools.Clear(paths);
        }
    }
}

internal sealed record CapturedStorageFixture(
    string Baseline, CapturedStorageRow Row, string MarkerRelativePath, string MarkerBase64, string MarkerSha256);

internal sealed record CapturedStorageRow(string SessionId, int LayoutVersion, string EnvelopeRoot, long CreatedAt)
{
    public static CapturedStorageRow Read(NetclawPaths paths)
    {
        using var connection = new SqliteConnection($"Data Source={paths.SqliteDbPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT session_id, layout_version, envelope_root, created_at FROM session_storage_bindings";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        var row = new CapturedStorageRow(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt64(3));
        Assert.False(reader.Read());
        return row;
    }
}

// -----------------------------------------------------------------------
// <copyright file="BackgroundJobDefinitionStoreTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Jobs;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Jobs;

public sealed class BackgroundJobDefinitionStoreTests : IDisposable
{
    private readonly string _basePath = Path.Combine(Path.GetTempPath(), $"netclaw-job-store-tests-{Guid.NewGuid():N}");
    private readonly NetclawPaths _paths;

    public BackgroundJobDefinitionStoreTests()
    {
        _paths = new NetclawPaths(_basePath);
        _paths.EnsureDirectoriesExist();
    }

    /// <summary>
    /// Regression test for issue #994. A pre-#994 background job document missing
    /// the required <c>audience</c>/<c>boundary</c> keys carries no trust context
    /// and cannot be run safely. The store SHALL reject it loudly — exclude it
    /// from <c>Get</c>/<c>List</c> and log an error — never coercing a substitute
    /// audience.
    /// </summary>
    [Fact]
    public void Legacy_job_without_trust_fields_is_rejected()
    {
        // Authentic legacy shape: camelCase keys, enums as strings, no audience or boundary.
        var jobId = "legacy-job-001";
        var legacyJson = $$"""
            {
              "id": "{{jobId}}",
              "command": "make build",
              "sessionId": "C0TEST/1712000000.000001",
              "rationale": "Build the project artifacts.",
              "status": "Pending",
              "timeoutSeconds": 600,
              "startedAtMs": 0
            }
            """;

        var filePath = Path.Combine(_paths.JobsDirectory, $"{Uri.EscapeDataString(jobId)}.json");
        File.WriteAllText(filePath, legacyJson);

        var logger = new CapturingJobLogger<BackgroundJobDefinitionStore>();
        var store = new BackgroundJobDefinitionStore(_paths, logger);

        // Rejected — not coerced to a substitute audience.
        Assert.Null(store.Get(new BackgroundJobId(jobId)));
        Assert.Empty(store.List());

        // Loud — an error naming the document and the missing fields was logged.
        Assert.NotEmpty(logger.Errors);
        Assert.Contains(logger.Errors, e => e.Contains(jobId) && e.Contains("audience"));
    }

    /// <summary>
    /// Positive control: a current document with explicit Audience and Boundary round-trips
    /// correctly through a fresh store instance (Save then re-read).
    /// </summary>
    [Fact]
    public void Current_job_with_trust_fields_roundtrips_exact_values()
    {
        var store = new BackgroundJobDefinitionStore(_paths);
        var jobId = "roundtrip-job-001";

        store.Save(new BackgroundJobDefinition
        {
            Id = new BackgroundJobId(jobId),
            Command = "dotnet test",
            SessionId = new Netclaw.Actors.Protocol.SessionId("C0ABC/1712000000.000001"),
            Rationale = "Run the test suite.",
            Status = BackgroundJobStatus.Pending,
            TimeoutSeconds = 300,
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Team,
            OriginChannelType = Netclaw.Actors.Channels.ChannelType.Slack
        });

        // Re-open from a fresh store instance to exercise deserialization
        var freshStore = new BackgroundJobDefinitionStore(_paths);
        var loaded = freshStore.Get(new BackgroundJobId(jobId));

        Assert.NotNull(loaded);
        Assert.Equal(TrustAudience.Team, loaded!.Audience);
        Assert.Equal(TrustBoundary.Team, loaded.Boundary);
        Assert.Equal(jobId, loaded.Id.Value);
        Assert.Equal("dotnet test", loaded.Command);
        Assert.Equal("C0ABC/1712000000.000001", loaded.SessionId.Value);
    }

    /// <summary>
    /// Terminal-job cleanup: <see cref="BackgroundJobDefinitionStore.DeleteJobArtifacts"/>
    /// removes BOTH the definition file and the job's output-log directory, so the
    /// store cannot grow without bound after a job's retention window elapses.
    /// </summary>
    [Fact]
    public void DeleteJobArtifacts_removes_definition_and_output_directory()
    {
        var store = new BackgroundJobDefinitionStore(_paths);
        var jobId = new BackgroundJobId("cleanup-job-001");

        store.Save(new BackgroundJobDefinition
        {
            Id = jobId,
            Command = "dotnet test",
            SessionId = new Netclaw.Actors.Protocol.SessionId("C0ABC/1712000000.000001"),
            Rationale = "Run the test suite.",
            Status = BackgroundJobStatus.Completed,
            TimeoutSeconds = 300,
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Team,
            OriginChannelType = Netclaw.Actors.Channels.ChannelType.Slack
        });

        // Simulate a real job's output log on disk.
        var outputLogPath = store.GetOutputLogPath(jobId);
        File.WriteAllText(outputLogPath, "build output");

        Assert.NotNull(store.Get(jobId));
        Assert.True(File.Exists(outputLogPath));

        var removed = store.DeleteJobArtifacts(jobId);

        Assert.True(removed);
        Assert.Null(store.Get(jobId));
        Assert.False(File.Exists(outputLogPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(outputLogPath)));
    }

    /// <summary>
    /// Idempotent cleanup: deleting artifacts for an already-removed job reports
    /// false and does not throw.
    /// </summary>
    [Fact]
    public void DeleteJobArtifacts_missing_job_returns_false_without_throwing()
    {
        var store = new BackgroundJobDefinitionStore(_paths);

        var removed = store.DeleteJobArtifacts(new BackgroundJobId("never-existed"));

        Assert.False(removed);
    }

    [Fact]
    public void DeleteJobArtifacts_keeps_definition_when_output_cleanup_fails_then_retries()
    {
        var store = new BackgroundJobDefinitionStore(_paths);
        var jobId = new BackgroundJobId("cleanup-retry-001");
        store.Save(new BackgroundJobDefinition
        {
            Id = jobId,
            Command = "dotnet test",
            SessionId = new Netclaw.Actors.Protocol.SessionId("C0ABC/1712000000.000001"),
            Rationale = "Run the test suite.",
            Status = BackgroundJobStatus.Completed,
            TimeoutSeconds = 300,
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Team,
            OriginChannelType = Netclaw.Actors.Channels.ChannelType.Slack
        });

        var outputLogPath = store.GetOutputLogPathOnly(jobId);
        var outputDirectory = Path.GetDirectoryName(outputLogPath)!;
        File.WriteAllText(outputDirectory, "path collision");

        var error = Assert.Throws<IOException>(() => store.DeleteJobArtifacts(jobId));

        Assert.Contains("is not a directory", error.Message);
        Assert.NotNull(store.Get(jobId));
        Assert.True(File.Exists(outputDirectory));

        File.Delete(outputDirectory);
        File.WriteAllText(store.GetOutputLogPath(jobId), "build output");

        Assert.True(store.DeleteJobArtifacts(jobId));
        Assert.Null(store.Get(jobId));
        Assert.False(Directory.Exists(outputDirectory));
    }

    /// <summary>
    /// Traversal guard (adversarial review, HIGH): Uri.EscapeDataString does not
    /// escape dots, so a dot-only id must NOT resolve to the jobs directory's
    /// parent and delete it recursively. The store rejects such ids at
    /// deserialization, and DeleteJobArtifacts contains the delete to the jobs
    /// directory as belt-and-braces.
    /// </summary>
    [Fact]
    public void DeleteJobArtifacts_dot_only_id_cannot_escape_jobs_directory()
    {
        var store = new BackgroundJobDefinitionStore(_paths);
        var jobsDir = _paths.JobsDirectory;
        var parentDir = Path.GetDirectoryName(jobsDir.TrimEnd(Path.DirectorySeparatorChar))!;
        var sentinel = Path.Combine(parentDir, "sentinel-file.txt");
        File.WriteAllText(sentinel, "do not delete");

        // ".." would resolve to the parent of the jobs directory.
        var removed = store.DeleteJobArtifacts(new BackgroundJobId(".."));

        // The delete must be refused (nothing removed) — the parent survives.
        Assert.False(removed);
        Assert.True(File.Exists(sentinel));
        Assert.True(Directory.Exists(jobsDir));

        File.Delete(sentinel);
    }

    /// <summary>
    /// Traversal guard (adversarial review, HIGH): a persisted definition whose
    /// id is "." or ".." must be rejected at load — it never appears in
    /// List(), so the sweep can never act on it.
    /// </summary>
    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void Definition_with_dot_only_id_is_rejected_at_load(string unsafeId)
    {
        var logger = new CapturingJobLogger<BackgroundJobDefinitionStore>();
        var store = new BackgroundJobDefinitionStore(_paths, logger);

        // File name mirrors what GetPath would produce for this id:
        // Uri.EscapeDataString leaves dots unescaped, so the file is "..json".
        var filePath = Path.Combine(_paths.JobsDirectory, $"{Uri.EscapeDataString(unsafeId)}.json");
        File.WriteAllText(filePath, $$"""
            {
              "id": "{{unsafeId}}",
              "command": "echo pwn",
              "sessionId": "C0TEST/1712000000.000001",
              "rationale": "test",
              "status": "Completed",
              "timeoutSeconds": 600,
              "startedAtMs": 0,
              "completedAtMs": 0,
              "audience": "Personal",
              "boundary": "Personal"
            }
            """);

        Assert.Empty(store.List());
        Assert.Null(store.Get(new BackgroundJobId(unsafeId)));
        Assert.Contains(logger.Errors, e => e.Contains("unsafe id"));
    }

    [Fact]
    public void Definition_with_id_that_does_not_match_file_name_is_rejected_at_load()
    {
        var logger = new CapturingJobLogger<BackgroundJobDefinitionStore>();
        var store = new BackgroundJobDefinitionStore(_paths, logger);
        var victimId = new BackgroundJobId("victim-job");
        store.Save(new BackgroundJobDefinition
        {
            Id = victimId,
            Command = "dotnet test",
            SessionId = new Netclaw.Actors.Protocol.SessionId("C0ABC/1712000000.000001"),
            Rationale = "Run the test suite.",
            Status = BackgroundJobStatus.Completed,
            StartedAtMs = 1,
            CompletedAtMs = 2,
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Team,
            OriginChannelType = Netclaw.Actors.Channels.ChannelType.Slack
        });

        var victimPath = Path.Combine(_paths.JobsDirectory, $"{Uri.EscapeDataString(victimId.Value)}.json");
        var aliasPath = Path.Combine(_paths.JobsDirectory, "stale-alias.json");
        File.Copy(victimPath, aliasPath);

        var loaded = store.List();

        var definition = Assert.Single(loaded);
        Assert.Equal(victimId, definition.Id);
        Assert.Null(store.Get(new BackgroundJobId("stale-alias")));
        Assert.Contains(logger.Errors, error =>
            error.Contains("does not match its canonical file name", StringComparison.Ordinal));
    }

    /// <summary>
    /// Byte-equality gate for issue #994 Pass 7b. Wrapping <c>BackgroundJobDefinition.Id</c>
    /// in <see cref="BackgroundJobId"/> and <c>SessionId</c> in
    /// <see cref="Netclaw.Actors.Protocol.SessionId"/> MUST NOT change the on-disk JSON:
    /// both stay bare strings, never a nested <c>{ "value": ... }</c> object. The
    /// <c>JsonConverter</c>s exist precisely so an upgraded daemon reads job
    /// documents written by the old binary and vice versa.
    /// </summary>
    [Fact]
    public void BackgroundJobDefinition_id_and_sessionId_serialize_as_bare_json_strings()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        };

        var definition = new BackgroundJobDefinition
        {
            Id = new BackgroundJobId("job-byte-eq"),
            Command = "dotnet test",
            SessionId = new Netclaw.Actors.Protocol.SessionId("C0ABC/1712000000.000001"),
            Rationale = "Run the test suite.",
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Team
        };

        var json = JsonSerializer.Serialize(definition, options);

        using var doc = JsonDocument.Parse(json);
        var idElement = doc.RootElement.GetProperty("id");
        var sessionElement = doc.RootElement.GetProperty("sessionId");

        Assert.Equal(JsonValueKind.String, idElement.ValueKind);
        Assert.Equal(JsonValueKind.String, sessionElement.ValueKind);
        Assert.Equal("job-byte-eq", idElement.GetString());
        Assert.Equal("C0ABC/1712000000.000001", sessionElement.GetString());

        // Round-trip: the bare string deserializes back into the value object.
        var loaded = JsonSerializer.Deserialize<BackgroundJobDefinition>(json, options);
        Assert.NotNull(loaded);
        Assert.Equal(new BackgroundJobId("job-byte-eq"), loaded!.Id);
        Assert.Equal(new Netclaw.Actors.Protocol.SessionId("C0ABC/1712000000.000001"), loaded.SessionId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_basePath))
            Directory.Delete(_basePath, recursive: true);
    }
}

/// <summary>
/// Capturing <see cref="ILogger{T}"/> that records formatted messages by level.
/// Used to verify the store logs a loud error when it rejects a legacy document.
/// </summary>
internal sealed class CapturingJobLogger<T> : ILogger<T>
{
    public List<string> Warnings { get; } = [];
    public List<string> Errors { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        if (logLevel >= LogLevel.Error)
            Errors.Add(message);
        else if (logLevel == LogLevel.Warning)
            Warnings.Add(message);
    }
}

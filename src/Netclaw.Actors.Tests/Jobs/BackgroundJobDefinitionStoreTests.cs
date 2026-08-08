// -----------------------------------------------------------------------
// <copyright file="BackgroundJobDefinitionStoreTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Protocol;
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

    [Fact]
    public void New_job_definition_and_output_use_source_session_directory()
    {
        var store = new BackgroundJobDefinitionStore(_paths);
        var definition = CreateDefinition("session-job", "C0ABC/1712000000.000001");

        store.Save(definition);
        var outputPath = store.GetOutputLogPath(definition.Id, definition.SessionId);

        var sessionDirectory = SessionDirectoryHelper.GetSessionJobsDirectory(
            definition.SessionId,
            _paths.SessionsDirectory);
        Assert.True(File.Exists(Path.Combine(sessionDirectory, "session-job.json")));
        Assert.Equal(Path.Combine(sessionDirectory, "session-job", "output.log"), outputPath);
        Assert.True(Directory.Exists(Path.GetDirectoryName(outputPath)));
        Assert.False(File.Exists(Path.Combine(_paths.JobsDirectory, "session-job.json")));
    }

    [Fact]
    public void Existing_job_definition_and_output_stay_in_daemon_directory_after_update()
    {
        var definition = CreateDefinition("existing-job", "C0ABC/1712000000.000001");
        var daemonPath = Path.Combine(_paths.JobsDirectory, "existing-job.json");
        WriteDefinition(daemonPath, definition);
        var daemonOutputPath = Path.Combine(_paths.JobsDirectory, "existing-job", "output.log");
        Directory.CreateDirectory(Path.GetDirectoryName(daemonOutputPath)!);
        File.WriteAllText(daemonOutputPath, "existing output");

        var store = new BackgroundJobDefinitionStore(_paths);
        store.Save(definition with { Status = BackgroundJobStatus.Completed, ExitCode = 0 });

        var sessionDirectory = SessionDirectoryHelper.GetSessionJobsDirectory(
            definition.SessionId,
            _paths.SessionsDirectory);
        Assert.True(File.Exists(daemonPath));
        Assert.Equal(daemonOutputPath, store.GetOutputLogPathOnly(definition.Id, definition.SessionId));
        Assert.Equal("existing output", File.ReadAllText(daemonOutputPath));
        Assert.False(File.Exists(Path.Combine(sessionDirectory, "existing-job.json")));
        Assert.Equal(BackgroundJobStatus.Completed, new BackgroundJobDefinitionStore(_paths).Get(definition.Id)!.Status);
    }

    [Fact]
    public void Duplicate_id_across_daemon_and_session_directories_is_rejected()
    {
        var definition = CreateDefinition("duplicate-job", "C0ABC/1712000000.000001");
        var daemonPath = Path.Combine(_paths.JobsDirectory, "duplicate-job.json");
        WriteDefinition(daemonPath, definition);
        var sessionDirectory = SessionDirectoryHelper.GetSessionJobsDirectory(
            definition.SessionId,
            _paths.SessionsDirectory);
        Directory.CreateDirectory(sessionDirectory);
        WriteDefinition(Path.Combine(sessionDirectory, "duplicate-job.json"), definition);
        var logger = new CapturingJobLogger<BackgroundJobDefinitionStore>();

        var store = new BackgroundJobDefinitionStore(_paths, logger);

        Assert.Null(store.Get(definition.Id));
        Assert.Empty(store.List());
        Assert.Contains(logger.Errors, message =>
            message.Contains(daemonPath, StringComparison.Ordinal)
            && message.Contains(sessionDirectory, StringComparison.Ordinal));
    }

    [Fact]
    public void Session_directory_owner_mismatch_is_rejected()
    {
        var definition = CreateDefinition("owner-mismatch", "C0ABC/1712000000.000001");
        var wrongDirectory = SessionDirectoryHelper.GetSessionJobsDirectory(
            new SessionId("C0OTHER/1712000000.000002"),
            _paths.SessionsDirectory);
        Directory.CreateDirectory(wrongDirectory);
        var wrongPath = Path.Combine(wrongDirectory, "owner-mismatch.json");
        WriteDefinition(wrongPath, definition);
        var logger = new CapturingJobLogger<BackgroundJobDefinitionStore>();

        var store = new BackgroundJobDefinitionStore(_paths, logger);

        Assert.Null(store.Get(definition.Id));
        Assert.Empty(store.List());
        Assert.Contains(logger.Errors, message =>
            message.Contains(wrongPath, StringComparison.Ordinal)
            && message.Contains("owner", StringComparison.OrdinalIgnoreCase));
    }

    private static BackgroundJobDefinition CreateDefinition(string id, string sessionId) => new()
    {
        Id = new BackgroundJobId(id),
        Command = "dotnet test",
        SessionId = new SessionId(sessionId),
        Rationale = "Run the test suite.",
        Status = BackgroundJobStatus.Pending,
        TimeoutSeconds = 300,
        StartedAtMs = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds(),
        Audience = TrustAudience.Personal,
        Boundary = TrustBoundary.Personal
    };

    private static void WriteDefinition(string path, BackgroundJobDefinition definition)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(definition, options));
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

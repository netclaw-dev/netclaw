// -----------------------------------------------------------------------
// <copyright file="ReminderDefinitionStoreTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Reminders;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Reminders;

public sealed class ReminderDefinitionStoreTests : IDisposable
{
    private readonly string _basePath = Path.Combine(Path.GetTempPath(), $"netclaw-reminder-store-tests-{Guid.NewGuid():N}");
    private readonly NetclawPaths _paths;

    public ReminderDefinitionStoreTests()
    {
        _paths = new NetclawPaths(_basePath);
        _paths.EnsureDirectoriesExist();
    }

    [Fact]
    public void Constructor_prunes_invalid_json_and_records_dropped_definition()
    {
        var reminderId = "legacy-reminder";
        var filePath = Path.Combine(_paths.RemindersDirectory, $"{Uri.EscapeDataString(reminderId)}.json");
        File.WriteAllText(filePath, "{ this is invalid json }");

        var store = new ReminderDefinitionStore(_paths);

        Assert.False(File.Exists(filePath));

        var dropped = store.ConsumeDroppedInvalidDefinitions();
        var entry = Assert.Single(dropped);
        Assert.Equal(reminderId, entry.ReminderId);
        Assert.False(string.IsNullOrWhiteSpace(entry.Reason));
        Assert.Empty(store.ConsumeDroppedInvalidDefinitions());
    }

    [Fact]
    public void Constructor_keeps_valid_definitions_while_pruning_invalid_files()
    {
        var seededStore = new ReminderDefinitionStore(_paths);
        var now = TimeProvider.System.GetUtcNow();

        seededStore.Save(new ReminderDefinition
        {
            Id = new ReminderId("valid-reminder"),
            Title = "valid-reminder",
            Instructions = "check status",
            Delivery = new ReminderDelivery { Kind = DeliveryKind.None },
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.OneShot,
                FireAt = now.AddMinutes(30)
            },
            Audience = TrustAudience.Public,
            Boundary = TrustBoundary.Public,
            Enabled = true,
            CreatedBy = "test",
            CreatedAt = now,
            UpdatedAt = now
        });

        var invalidPath = Path.Combine(_paths.RemindersDirectory, $"{Uri.EscapeDataString("bad-reminder")}.json");
        File.WriteAllText(invalidPath, "not json");

        var reloadedStore = new ReminderDefinitionStore(_paths);
        var reminders = reloadedStore.List();

        Assert.Single(reminders);
        Assert.Equal("valid-reminder", reminders[0].Id.Value);
        Assert.False(File.Exists(invalidPath));
    }

    /// <summary>
    /// Regression test for issue #994. A pre-#994 reminder document missing the
    /// required <c>audience</c>/<c>boundary</c> keys carries no trust context
    /// and cannot be run safely. The store SHALL reject it loudly — exclude it
    /// from <c>Get</c>/<c>List</c> and log an error — and SHALL preserve the
    /// file (operator-authored data, not corrupt JSON), never coercing a
    /// substitute audience.
    /// </summary>
    [Fact]
    public void Legacy_reminder_without_trust_fields_is_rejected_and_preserved()
    {
        // Authentic legacy shape: camelCase keys, no audience or boundary, enums as strings.
        const long fireAtMs = 1_800_000_000_000L; // some arbitrary future timestamp
        var reminderId = "legacy-no-trust";
        var legacyJson = $$"""
            {
              "id": "{{reminderId}}",
              "title": "Legacy Check",
              "schedule": {
                "type": "OneShot",
                "fireAtMs": {{fireAtMs}}
              },
              "instructions": "Check the build status.",
              "delivery": {
                "kind": "None"
              },
              "deliveryRequired": true,
              "deliveryInstructions": "Post result to channel.",
              "enabled": true,
              "createdBy": "alice",
              "createdAtMs": 1700000000000,
              "updatedAtMs": 1700000000000
            }
            """;

        var filePath = Path.Combine(_paths.RemindersDirectory, $"{Uri.EscapeDataString(reminderId)}.json");
        File.WriteAllText(filePath, legacyJson);

        var logger = new CapturingLogger<ReminderDefinitionStore>();
        var store = new ReminderDefinitionStore(_paths, logger);

        // Rejected — not coerced to a substitute audience.
        Assert.Null(store.Get(new ReminderId(reminderId)));
        Assert.Empty(store.List());

        // Preserved — a legacy doc is operator data, not corrupt JSON; the
        // operator must be able to repair or remove it.
        Assert.True(File.Exists(filePath), "Legacy reminder file must NOT be deleted.");

        // Loud — an error naming the document and the missing fields was logged.
        Assert.NotEmpty(logger.Errors);
        Assert.Contains(logger.Errors, e => e.Contains(reminderId) && e.Contains("audience"));
    }

    /// <summary>
    /// Positive control: a current document with explicit Audience and Boundary round-trips
    /// correctly through a fresh store (Save then re-read).
    /// </summary>
    [Fact]
    public void Current_reminder_with_trust_fields_roundtrips_exact_values()
    {
        var store = new ReminderDefinitionStore(_paths);
        var now = TimeProvider.System.GetUtcNow();
        var id = "roundtrip-trust";

        store.Save(new ReminderDefinition
        {
            Id = new ReminderId(id),
            Title = "Round-trip check",
            Instructions = "Do the thing.",
            Delivery = new ReminderDelivery { Kind = DeliveryKind.None },
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.OneShot,
                FireAt = now.AddHours(1)
            },
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.Personal,
            Enabled = true,
            CreatedBy = "bob",
            CreatedAt = now,
            UpdatedAt = now
        });

        // Re-open from a fresh store instance to exercise deserialization
        var freshStore = new ReminderDefinitionStore(_paths);
        var loaded = freshStore.Get(new ReminderId(id));

        Assert.NotNull(loaded);
        Assert.Equal(TrustAudience.Personal, loaded!.Audience);
        Assert.Equal(TrustBoundary.Personal, loaded.Boundary);
        Assert.Equal(id, loaded.Id.Value);
        Assert.Equal("Round-trip check", loaded.Title);
        Assert.Equal(0, loaded.ConsecutiveFailures);
        Assert.Null(loaded.TerminalOutcome);
    }

    [Fact]
    public void Reminder_failure_state_roundtrips()
    {
        var store = new ReminderDefinitionStore(_paths);
        var now = TimeProvider.System.GetUtcNow();
        var definition = new ReminderDefinition
        {
            Id = new ReminderId("roundtrip-failure-state"),
            Title = "Failure state check",
            Instructions = "Do the thing.",
            Delivery = new ReminderDelivery { Kind = DeliveryKind.None },
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.OneShot,
                FireAt = now.AddHours(1)
            },
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.Personal,
            Enabled = false,
            ConsecutiveFailures = 5,
            TerminalOutcome = ReminderTerminalOutcome.Failed,
            CreatedAt = now,
            UpdatedAt = now
        };

        store.Save(definition);

        var loaded = new ReminderDefinitionStore(_paths).Get(definition.Id);

        Assert.NotNull(loaded);
        Assert.Equal(5, loaded.ConsecutiveFailures);
        Assert.Equal(ReminderTerminalOutcome.Failed, loaded.TerminalOutcome);
    }

    [Fact]
    public void Definition_without_failure_fields_loads_with_active_defaults()
    {
        var reminderId = "old-failure-shape";
        var filePath = Path.Combine(
            _paths.RemindersDirectory,
            $"{Uri.EscapeDataString(reminderId)}.json");
        const string json = """
            {
              "id": "old-failure-shape",
              "title": "Old shape",
              "schedule": { "type": "OneShot", "fireAtMs": 1800000000000 },
              "instructions": "Check status.",
              "delivery": { "kind": "None" },
              "enabled": true,
              "audience": "Personal",
              "boundary": "personal",
              "createdAtMs": 1700000000000,
              "updatedAtMs": 1700000000000
            }
            """;
        File.WriteAllText(filePath, json);

        var loaded = new ReminderDefinitionStore(_paths).Get(new ReminderId(reminderId));

        Assert.NotNull(loaded);
        Assert.Equal(0, loaded!.ConsecutiveFailures);
        Assert.Null(loaded.TerminalOutcome);
        Assert.True(loaded.Enabled);
    }

    /// <summary>
    /// Byte-equality gate for issue #994 Pass 7b. Wrapping <c>ReminderDefinition.Id</c>
    /// in <see cref="ReminderId"/> MUST NOT change the on-disk JSON: the <c>id</c>
    /// property stays a bare string, never a nested <c>{ "value": ... }</c> object,
    /// so an upgraded daemon reads reminder documents written by the old binary.
    /// </summary>
    [Fact]
    public void ReminderDefinition_id_serializes_as_bare_json_string()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        };
        var now = TimeProvider.System.GetUtcNow();

        var definition = new ReminderDefinition
        {
            Id = new ReminderId("reminder-byte-eq"),
            Title = "Byte-equality check",
            Instructions = "Do the thing.",
            Delivery = new ReminderDelivery { Kind = DeliveryKind.None },
            Schedule = new ReminderSchedule { Type = ReminderScheduleType.OneShot, FireAt = now.AddHours(1) },
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.Personal
        };

        var json = JsonSerializer.Serialize(definition, options);

        using var doc = JsonDocument.Parse(json);
        var idElement = doc.RootElement.GetProperty("id");
        Assert.Equal(JsonValueKind.String, idElement.ValueKind);
        Assert.Equal("reminder-byte-eq", idElement.GetString());

        var loaded = JsonSerializer.Deserialize<ReminderDefinition>(json, options);
        Assert.NotNull(loaded);
        Assert.Equal(new ReminderId("reminder-byte-eq"), loaded!.Id);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..\\..\\windows\\system32\\config")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\System32")]
    public void Exists_with_traversal_id_does_not_escape_reminders_directory(string maliciousId)
    {
        var store = new ReminderDefinitionStore(_paths);

        // Uri.EscapeDataString neutralizes path separators, so the canonical
        // path stays inside _basePath and Exists() simply returns false. The
        // explicit containment check in GetPath would throw if that invariant
        // ever regressed.
        Assert.False(store.Exists(new ReminderId(maliciousId)));
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
internal sealed class CapturingLogger<T> : ILogger<T>
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

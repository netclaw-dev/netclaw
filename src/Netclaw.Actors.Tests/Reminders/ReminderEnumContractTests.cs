// -----------------------------------------------------------------------
// <copyright file="ReminderEnumContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Reflection;
using System.Text.Json.Serialization;
using Netclaw.Actors.Reminders;
using Xunit;

namespace Netclaw.Actors.Tests.Reminders;

/// <summary>
/// The store, the daemon endpoints, and the CLI each serialize reminder definitions with their own
/// options, so the enum names on the wire come from the converter attribute on the property.
/// </summary>
public sealed class ReminderEnumContractTests
{
    [Fact]
    public void Every_enum_property_of_a_persisted_reminder_type_names_its_converter()
    {
        var persistedTypes = new[]
        {
            typeof(ReminderDefinition),
            typeof(ReminderSchedule),
            typeof(ReminderDelivery)
        };

        var missing = persistedTypes
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(property => (Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType).IsEnum)
            .Where(property => property.GetCustomAttribute<JsonConverterAttribute>() is null)
            .Select(property => $"{property.DeclaringType!.Name}.{property.Name}")
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void The_terminal_outcome_names_stay_and_the_ends_without_a_run_get_their_own()
    {
        var names = Enum.GetNames<ReminderTerminalOutcome>();

        // The existing names are on disk and on the wire. The tombstone adds two, for an end by cancel and an end by expiry.
        Assert.Contains("Completed", names);
        Assert.Contains("Failed", names);
        Assert.Contains("Cancelled", names);
        Assert.Contains("Expired", names);
    }
}

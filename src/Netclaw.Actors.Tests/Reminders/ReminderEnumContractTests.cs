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
}

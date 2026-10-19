// -----------------------------------------------------------------------
// <copyright file="ReminderEndedProtocol.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Reflection;
using Netclaw.Actors.Reminders;

namespace Netclaw.Tests.Utilities;

/// <summary>What a reminder response says about an ended one-shot (<c>ReminderEndedInfo</c>).</summary>
internal sealed record EndedView(
    string Outcome,
    DateTimeOffset EndedAt,
    DateTimeOffset PurgeAt,
    string? LastError,
    string? Instructions);

/// <summary>
/// Reads and builds the additive protocol members of change <c>oneshot-reminder-autodelete</c>
/// (<c>ReminderEndedInfo</c>, <c>GetReminderResponse.Ended</c>, <c>ReminderStatusResponse.Ended</c>,
/// <c>ReminderCancelledResponse.AlreadyEnded</c>, <c>ReminderCancelledResponse.Deleted</c>).
/// The members do not exist before the implementation. Reflection keeps the test projects
/// compiling, and each missing member fails the test with a message that names it.
/// The exact shapes are in design.md, decision D7.
/// </summary>
internal static class ReminderEndedProtocol
{
    private const string EndedInfoTypeName = "Netclaw.Actors.Reminders.ReminderEndedInfo";

    public static Type EndedInfoType => typeof(ReminderManagerActor).Assembly.GetType(EndedInfoTypeName)
        ?? throw new InvalidOperationException(
            $"{EndedInfoTypeName} does not exist yet (change oneshot-reminder-autodelete, task 1.3).");

    /// <summary>Reads <paramref name="member"/> (an ended-info property) of a response, or null when the property is null.</summary>
    public static EndedView? ReadEnded(object response, string member)
    {
        var value = ReadMember(response, member);
        if (value is null)
            return null;

        return new EndedView(
            Outcome: ReadMember(value, "Outcome")!.ToString()!,
            EndedAt: (DateTimeOffset)ReadMember(value, "EndedAt")!,
            PurgeAt: (DateTimeOffset)ReadMember(value, "PurgeAt")!,
            LastError: (string?)ReadMember(value, "LastError"),
            Instructions: (string?)ReadMember(value, "Instructions"));
    }

    /// <summary>Reads a public property by name. A missing property is a failure that names it.</summary>
    public static object? ReadMember(object target, string member)
    {
        var property = target.GetType().GetProperty(member, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"{target.GetType().Name} has no '{member}' member yet (change oneshot-reminder-autodelete, design D7).");
        return property.GetValue(target);
    }

    public static bool HasMember(object target, string member)
        => target.GetType().GetProperty(member, BindingFlags.Public | BindingFlags.Instance) is not null;

    /// <summary>Builds a <c>ReminderEndedInfo</c> by constructor parameter name.</summary>
    public static object NewEndedInfo(
        string outcome,
        DateTimeOffset endedAt,
        DateTimeOffset purgeAt,
        string? lastError = null,
        string? instructions = null)
        => Construct(EndedInfoType, new Dictionary<string, object?>
        {
            ["Outcome"] = Enum.Parse(typeof(ReminderTerminalOutcome), outcome),
            ["EndedAt"] = endedAt,
            ["PurgeAt"] = purgeAt,
            ["LastError"] = lastError,
            ["Instructions"] = instructions
        });

    /// <summary>
    /// Builds a record by constructor parameter name. A parameter that <paramref name="named"/> omits
    /// takes its default value. A name that the constructor lacks is a failure that names it.
    /// </summary>
    public static object Construct(Type type, IReadOnlyDictionary<string, object?> named)
    {
        var constructor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var parameters = constructor.GetParameters();
        foreach (var name in named.Keys)
        {
            if (parameters.All(p => !string.Equals(p.Name, name, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"{type.Name} has no constructor parameter '{name}' yet (change oneshot-reminder-autodelete, design D7).");
            }
        }

        var args = parameters
            .Select(p => named.TryGetValue(p.Name!, out var value)
                ? value
                : p.HasDefaultValue ? p.DefaultValue : DefaultOf(p.ParameterType))
            .ToArray();
        return constructor.Invoke(args);
    }

    private static object? DefaultOf(Type type) => type.IsValueType ? Activator.CreateInstance(type) : null;
}

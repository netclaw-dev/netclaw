// -----------------------------------------------------------------------
// <copyright file="WebhookRouteName.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;

namespace Netclaw.Configuration;

/// <summary>
/// A validated webhook route name. The name is the URL path segment of an
/// inbound webhook and the file name of its definition, so it must be safe for
/// both. This type is the one place that decides what a route name may be.
/// <para>
/// A value exists only through <see cref="TryCreate"/> or <see cref="Create"/>,
/// so a value that exists is always trimmed, lowercase, and kebab-case. Read
/// the name through <see cref="Value"/>. There is no implicit conversion: a
/// route name must never become a plain string by accident.
/// </para>
/// <para>
/// A <c>default</c> value carries no name. <see cref="Value"/> throws for it
/// rather than return a substitute.
/// </para>
/// </summary>
public readonly record struct WebhookRouteName
{
    private static readonly Regex RouteNamePattern = new(
        "^[a-z0-9]+(?:-[a-z0-9]+)*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string? _value;

    private WebhookRouteName(string value) => _value = value;

    /// <summary>The validated, normalized route name.</summary>
    public string Value => _value ?? throw new InvalidOperationException(
        "Webhook route name is not initialized. Build one with TryCreate or Create.");

    /// <summary>
    /// Normalizes and validates a candidate route name. Returns false and an
    /// operator-facing message when the candidate is not a route name.
    /// </summary>
    public static bool TryCreate(string? candidate, out WebhookRouteName routeName, out string? error)
    {
        routeName = default;

        var normalized = candidate?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            error = "Webhook route name is required.";
            return false;
        }

        if (!RouteNamePattern.IsMatch(normalized))
        {
            error =
                "Webhook route name must be lowercase kebab-case (letters, numbers, single dashes).";
            return false;
        }

        routeName = new WebhookRouteName(normalized);
        error = null;
        return true;
    }

    /// <summary>
    /// Normalizes and validates a candidate route name, or throws. Use this
    /// where an invalid name is a programming error, not operator input.
    /// </summary>
    public static WebhookRouteName Create(string candidate)
    {
        if (!TryCreate(candidate, out var routeName, out var error))
            throw new ArgumentException(error, nameof(candidate));

        return routeName;
    }

    public override string ToString() => _value ?? "(uninitialized)";
}

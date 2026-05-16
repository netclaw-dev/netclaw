// -----------------------------------------------------------------------
// <copyright file="ModelId.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Strongly-typed model identifier — the provider-facing model name used in
/// API calls and capability lookups (e.g. <c>claude-opus-4-7</c>). Wraps the
/// raw id string so a model id cannot be confused with a session id, provider
/// name, or any other string at a call boundary.
/// </summary>
public readonly record struct ModelId
{
    /// <summary>
    /// Constructs a model id from its raw string value. The value is trimmed;
    /// an empty or whitespace value is rejected.
    /// </summary>
    public ModelId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Model id must be a non-empty value.", nameof(value));

        Value = value.Trim();
    }

    /// <summary>The raw model identifier string.</summary>
    public string Value { get; }

    /// <summary>
    /// Explicit conversion from the raw model-id string. Routes through the
    /// validating constructor — there is deliberately no implicit conversion.
    /// </summary>
    public static explicit operator ModelId(string value) => new(value);

    public override string ToString() => Value;
}

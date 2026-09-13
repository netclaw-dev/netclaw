// -----------------------------------------------------------------------
// <copyright file="AuthorizationAttemptId.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

namespace Netclaw.Tools;

/// <summary>
/// Opaque diagnostic identity for one tool call's authorization lifecycle.
/// This value grants no authority and is never exposed to tool implementations.
/// </summary>
internal readonly record struct AuthorizationAttemptId
{
    private const string Prefix = "auth-";
    private const int EncodedLength = 32;

    private AuthorizationAttemptId(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static AuthorizationAttemptId New()
        => new($"{Prefix}{Guid.NewGuid():N}");

    public static bool TryParse(string? value, out AuthorizationAttemptId attemptId)
    {
        attemptId = default;
        if (value is null
            || !value.StartsWith(Prefix, StringComparison.Ordinal)
            || value.Length != Prefix.Length + EncodedLength
            || !Guid.TryParseExact(value.Substring(Prefix.Length), "N", out var parsed))
        {
            return false;
        }

        attemptId = new AuthorizationAttemptId($"{Prefix}{parsed:N}");
        return true;
    }

    public override string ToString() => Value;
}

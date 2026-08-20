// -----------------------------------------------------------------------
// <copyright file="UserQueryNormalizer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Channels;

/// <summary>
/// Normalizes a raw user-lookup query typed by an operator. Trims the
/// query and drops one leading '@' so "@alice" and "alice" resolve to
/// the same user.
/// </summary>
public static class UserQueryNormalizer
{
    public static string StripLeadingAt(string query)
    {
        var normalized = query.Trim();
        return normalized.StartsWith('@') ? normalized[1..].Trim() : normalized;
    }
}

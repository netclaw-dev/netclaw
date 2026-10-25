// -----------------------------------------------------------------------
// <copyright file="ThinkingDialectMap.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Configuration;

namespace Netclaw.Providers;

/// <summary>
/// The levels that a model offers, and the reason for each level that it does not offer.
/// <see cref="Unavailable"/> holds every <see cref="ThinkingLevel"/> that is not in <see cref="Offered"/>.
/// </summary>
public sealed record OfferedThinkingLevels(
    IReadOnlyList<ThinkingLevel> Offered,
    IReadOnlyDictionary<ThinkingLevel, string> Unavailable);

/// <summary>
/// The static map of each <see cref="ThinkingDialect"/>: the levels that it can send, and the request
/// fields that it writes for a level and for the reasoning suppression intent.
/// </summary>
public static class ThinkingDialectMap
{
    /// <summary>The levels that the dialect can send, in enum order.</summary>
    public static IReadOnlyList<ThinkingLevel> ExpressibleLevels(ThinkingDialect dialect) => [];

    /// <summary>
    /// The offered levels: the levels that the dialect can send and that the capability includes.
    /// </summary>
    public static OfferedThinkingLevels OfferedLevels(ThinkingDialect dialect, ThinkingSupport capability)
        => new([], new Dictionary<ThinkingLevel, string>());

    /// <summary>
    /// Writes the request fields for <paramref name="level"/> into <paramref name="options"/>. A null level
    /// writes nothing. Throws <see cref="NotSupportedException"/> when the dialect cannot send the level.
    /// </summary>
    public static void Apply(ThinkingDialect dialect, ChatOptions options, ThinkingLevel? level)
    {
    }

    /// <summary>
    /// Writes the request fields for the reasoning suppression intent of a call site into
    /// <paramref name="options"/>. It never throws.
    /// </summary>
    public static void ApplySuppression(ThinkingDialect dialect, ChatOptions options)
    {
    }
}

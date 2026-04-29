// -----------------------------------------------------------------------
// <copyright file="SessionCatalogEntryDto.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Cli.Daemon;

/// <summary>
/// Client-side DTO matching the daemon's <c>SessionCatalogEntry</c> shape
/// from <c>GET /api/sessions</c>. Exposes a computed <see cref="SessionId"/>
/// property that strips the <c>session-</c> prefix from <see cref="PersistenceId"/>
/// for use with <c>EnsureSession</c>.
/// </summary>
public sealed record SessionCatalogEntryDto
{
    public required string PersistenceId { get; init; }
    public required string Channel { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public required string Status { get; init; }
    public required int TurnCount { get; init; }
    public required long CreatedAt { get; init; }
    public required long LastActivity { get; init; }
    public string? LogPath { get; init; }
    public long? LastInputTokens { get; init; }

    /// <summary>
    /// The raw session ID for use with <c>EnsureSession</c>.
    /// Strips the <c>session-</c> prefix from <see cref="PersistenceId"/>.
    /// </summary>
    public string SessionId => PersistenceId.StartsWith("session-", StringComparison.Ordinal)
        ? PersistenceId["session-".Length..]
        : PersistenceId;
}

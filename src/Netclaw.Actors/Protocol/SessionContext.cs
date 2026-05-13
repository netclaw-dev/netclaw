// -----------------------------------------------------------------------
// <copyright file="SessionContext.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Installs additive session-scoped prompt context without replacing the base
/// system prompt assembled from identity files.
/// </summary>
public sealed record SetSessionPromptOverlay : IWithSessionId, INoSerializationVerificationNeeded
{
    public required SessionId SessionId { get; init; }

    public string PromptOverlay { get; init; } = string.Empty;
}

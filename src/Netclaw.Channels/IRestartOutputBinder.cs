// -----------------------------------------------------------------------
// <copyright file="IRestartOutputBinder.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;

namespace Netclaw.Channels;

/// <summary>
/// Restores a channel subscriber from journal-owned route facts.
/// The channel checks its current ACL before it confirms the subscriber.
/// </summary>
public interface IRestartOutputBinder
{
    Task<RestartOutputBindingResult> BindForRestartAsync(
        RestartResumeRouteResult route,
        CancellationToken cancellationToken);
}

public sealed record RestartOutputBindingResult(bool Ready, string? Reason = null);

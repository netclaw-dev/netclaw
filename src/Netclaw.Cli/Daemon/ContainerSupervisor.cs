// -----------------------------------------------------------------------
// <copyright file="ContainerSupervisor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Cli.Daemon;

/// <summary>
/// Reports whether an external process supervisor owns the daemon lifecycle.
/// </summary>
/// <remarks>
/// The official Docker image runs <c>entrypoint.sh</c> as PID 1 and supervises
/// <c>netclawd</c>, restarting it on exit. In that environment the CLI must
/// never spawn a detached daemon of its own — doing so creates a second
/// <c>netclawd</c> that races the supervised one for the singleton lock file
/// (#1279). The image declares this by setting
/// <c>NETCLAW_CONTAINER_SUPERVISOR</c>; the CLI keys its behavior off the
/// marker rather than off generic container detection, because the invariant
/// that matters is "an external supervisor owns start/stop," not "we happen to
/// be inside a container."
/// </remarks>
public interface IContainerSupervisor
{
    /// <summary>
    /// <c>true</c> when an external supervisor owns daemon start/stop and the
    /// CLI must defer the lifecycle to it instead of spawning <c>netclawd</c>.
    /// </summary>
    bool IsExternallySupervised { get; }
}

/// <inheritdoc cref="IContainerSupervisor"/>
/// <remarks>
/// The image-set marker is treated as authoritative on purpose. We deliberately do
/// NOT try to corroborate it (e.g. by inspecting PID 1): a false negative there would
/// flip the answer to "not supervised" and let the CLI spawn a detached daemon —
/// re-opening the exact split-brain (#1279) the marker exists to prevent. The image
/// sets the marker precisely because it knows it runs a supervisor, so the safe
/// default is to trust it; the lifecycle commands instead fail loudly (rather than
/// silently) if the daemon is not running, so a mis-set marker is visible.
/// </remarks>
public sealed class ContainerSupervisor : IContainerSupervisor
{
    public bool IsExternallySupervised =>
        Environment.GetEnvironmentVariable("NETCLAW_CONTAINER_SUPERVISOR") is { Length: > 0 };
}

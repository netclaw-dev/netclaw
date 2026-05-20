// -----------------------------------------------------------------------
// <copyright file="ServiceIdentity.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Per-instance service identity, projected from the OpenTelemetry resource at
/// startup and stamped onto operational webhook alert payloads — so an alert
/// carries the same identity as the telemetry this netclaw instance emits.
/// </summary>
/// <param name="Name">OpenTelemetry <c>service.name</c>.</param>
/// <param name="Namespace">OpenTelemetry <c>service.namespace</c>; <c>null</c> when the environment does not supply it.</param>
/// <param name="InstanceId">OpenTelemetry <c>service.instance.id</c>; <c>null</c> when the environment does not supply it.</param>
/// <param name="Version">OpenTelemetry <c>service.version</c> — the running netclaw build.</param>
public sealed record ServiceIdentity(string Name, string? Namespace, string? InstanceId, string Version);

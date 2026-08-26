// -----------------------------------------------------------------------
// <copyright file="IWireType.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Marker interface for types that cross a wire boundary (SignalR, HTTP, etc.).
/// Implementations must remain serialization-safe — no behavior, no circular refs.
/// </summary>
public interface IWireType;

// -----------------------------------------------------------------------
// <copyright file="IPersistableMessage.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Protocol;

/// <summary>
/// Marker interface for messages that need to be serialized (persisted or sent across process boundaries).
/// All messages implementing this interface must be registered in the protobuf serializer.
/// </summary>
public interface IPersistableMessage { }

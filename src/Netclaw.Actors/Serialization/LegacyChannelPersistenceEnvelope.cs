// -----------------------------------------------------------------------
// <copyright file="LegacyChannelPersistenceEnvelope.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Serialization;

/// <summary>
/// Decode-only carrier for historical transport persistence records. The
/// generic serializer creates this from an old manifest but has no encoding
/// path for it. A transport actor converts the payload after recovery.
/// </summary>
internal sealed record LegacyChannelPersistenceEnvelope(
    string Manifest,
    byte[] Payload) : INetclawSerializableMessage;

// -----------------------------------------------------------------------
// <copyright file="INetclawSerializableMessage.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Serialization;

/// <summary>
/// Marker for messages that cross a persistence or remoting boundary. Bound
/// to <see cref="NetclawProtobufSerializer"/> in
/// <c>WithNetclawSerialization</c>. Every implementing type MUST also have
/// an entry in <see cref="NetclawProtobufSerializer.Manifest(object)"/>'s
/// type-to-manifest table and a <c>ToProto</c>/<c>FromProto</c> mapping in
/// <see cref="NetclawProtoMapper"/>. Missing entries fail loudly at the
/// first serialize call — that is the regression signal this marker
/// exists to provide.
/// </summary>
/// <remarks>
/// Do not bind a second interface to <see cref="NetclawProtobufSerializer"/>.
/// Akka's <c>FindSerializerForType</c> short-circuits on first match, so
/// overlapping interface bindings produce iteration-order-dependent
/// behavior.
///
/// A type may legitimately implement both this marker and
/// <c>Akka.Actor.INoSerializationVerificationNeeded</c>. That means "bound
/// to proto for journal/wire serialization, but local-dispatch skips the
/// roundtrip" — usually because the type carries an ephemeral field the
/// proto mapping drops (e.g. <c>SendUserMessage.Source</c>).
/// </remarks>
public interface INetclawSerializableMessage
{
}

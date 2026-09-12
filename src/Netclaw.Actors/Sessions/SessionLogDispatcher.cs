// -----------------------------------------------------------------------
// <copyright file="SessionLogDispatcher.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text;
using Akka.Actor;
using Netclaw.Actors.Protocol;
using Netclaw.Tools;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Routes parent and child audit records to one writer for each resolved log path.
/// </summary>
public sealed class SessionLogDispatcher : ReceiveActor
{
    private readonly ISessionStorageResolver _storageResolver;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<SessionLogPath, IActorRef> _writers = [];
    private readonly Dictionary<IActorRef, SessionLogPath> _paths = [];

    /// <summary>Creates a dispatcher that resolves each session before it creates a writer.</summary>
    /// <param name="storageResolver">Resolves the immutable parent and child log paths.</param>
    /// <param name="timeProvider">Supplies timestamps to each log writer.</param>
    public SessionLogDispatcher(
        ISessionStorageResolver storageResolver,
        TimeProvider timeProvider)
    {
        _storageResolver = storageResolver;
        _timeProvider = timeProvider;

        Receive<SessionLogDiagnostic>(diagnostic => Route(diagnostic, ResolvePath(diagnostic)));
        Receive<IWithSessionId>(message => Route(message, _storageResolver.Resolve(message.SessionId).LogPath));
        Receive<Terminated>(terminated => RemoveWriter(terminated.ActorRef));
    }

    private SessionLogPath ResolvePath(SessionLogDiagnostic diagnostic)
    {
        var parent = _storageResolver.Resolve(diagnostic.SessionId);
        if (diagnostic.SubSessionId is not { } scopeId)
            return parent.LogPath;

        if (!scopeId.TryGetRunId(out var runId))
        {
            throw new ArgumentException(
                "A child session log needs a valid subagent scope identifier.",
                nameof(diagnostic));
        }

        return parent.ForChild(runId, scopeId).LogPath;
    }

    private void Route(object message, SessionLogPath logPath)
    {
        if (!_writers.TryGetValue(logPath, out var writer))
        {
            var sessionId = message is IWithSessionId sessionMessage
                ? sessionMessage.SessionId
                : throw new ArgumentException("A session log message needs a session identifier.", nameof(message));
            writer = Context.ActorOf(
                SessionLogActor.CreatePropsForPath(
                    sessionId,
                    logPath,
                    _timeProvider),
                WriterName(logPath));
            Context.Watch(writer);
            _writers.Add(logPath, writer);
            _paths.Add(writer, logPath);
        }

        writer.Forward(message);
    }

    private void RemoveWriter(IActorRef writer)
    {
        if (!_paths.Remove(writer, out var path))
            return;

        _writers.Remove(path);
    }

    private static string WriterName(SessionLogPath path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path.Value));
        return $"log-{Convert.ToHexStringLower(hash)}";
    }
}

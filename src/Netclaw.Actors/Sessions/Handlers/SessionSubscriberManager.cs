// -----------------------------------------------------------------------
// <copyright file="SessionSubscriberManager.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Sessions.Handlers;

internal readonly record struct SessionSubscriber(IActorRef Actor, OutputFilter Filter);

/// <summary>
/// Owns direct session subscribers and their output filters. Accessed only from
/// the session actor mailbox thread, except immutable snapshots handed to async
/// tool/sub-agent callbacks.
/// </summary>
internal sealed class SessionSubscriberManager
{
    private readonly Dictionary<IActorRef, OutputFilter> _subscribers = [];

    public int Count => _subscribers.Count;

    public bool IsReJoin(IActorRef subscriber, OutputFilter filter)
        => _subscribers.TryGetValue(subscriber, out var existingFilter)
           && existingFilter == filter;

    public void AddOrUpdate(IActorRef subscriber, OutputFilter filter)
        => _subscribers[subscriber] = filter;

    public bool Remove(IActorRef subscriber)
        => _subscribers.Remove(subscriber);

    public IReadOnlyList<SessionSubscriber> Snapshot()
        => [.. _subscribers.Select(pair => new SessionSubscriber(pair.Key, pair.Value))];

    public void Emit(SessionOutput output, OutputFilter requiredFlag = OutputFilter.None)
    {
        foreach (var (subscriber, filter) in _subscribers)
        {
            if (requiredFlag == OutputFilter.None || filter.HasFlag(requiredFlag))
                subscriber.Tell(output);
        }
    }

    public static void Emit(
        IReadOnlyList<SessionSubscriber> subscribers,
        SessionOutput output,
        OutputFilter requiredFlag = OutputFilter.None)
    {
        foreach (var subscriber in subscribers)
        {
            if (requiredFlag == OutputFilter.None || subscriber.Filter.HasFlag(requiredFlag))
                subscriber.Actor.Tell(output);
        }
    }
}

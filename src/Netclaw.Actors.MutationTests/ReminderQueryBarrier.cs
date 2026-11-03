// -----------------------------------------------------------------------
// <copyright file="ReminderQueryBarrier.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Reminders;
using static Netclaw.Actors.Reminders.ReminderProtocol;

namespace Netclaw.Actors.MutationTests;

internal sealed record BarrierReply(ReminderExecutionAccepted? Accepted, ReminderHealthResponse Health);

// The manager finishes one message before it reads the next, and Akka keeps the order of the
// replies to one sender. So the health reply comes after the manager handles the message, and
// after any acceptance reply for it. A missing acceptance reply fails the test at once.
internal sealed class QueryBarrier : ReceiveActor
{
    public QueryBarrier(IActorRef manager)
    {
        Receive<object>(message =>
        {
            var replyTo = Sender;
            ReminderExecutionAccepted? accepted = null;
            manager.Tell(message, Self);
            manager.Tell(GetReminderHealthQuery.Instance, Self);
            BecomeStacked(() =>
            {
                Receive<ReminderExecutionAccepted>(reply => accepted = reply);
                Receive<ReminderHealthResponse>(health =>
                {
                    replyTo.Tell(new BarrierReply(accepted, health));
                    UnbecomeStacked();
                });
            });
        });
    }
}

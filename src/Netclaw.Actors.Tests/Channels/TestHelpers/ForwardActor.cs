using Akka.Actor;

namespace Netclaw.Actors.Tests.Channels.TestHelpers;

internal sealed class ForwardActor : ReceiveActor
{
    public ForwardActor(IActorRef target)
    {
        ReceiveAny(msg => target.Tell(msg));
    }
}

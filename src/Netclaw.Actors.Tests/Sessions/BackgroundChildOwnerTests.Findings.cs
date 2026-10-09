// -----------------------------------------------------------------------
// <copyright file="BackgroundChildOwnerTests.Findings.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.SubAgents;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed partial class BackgroundChildOwnerTests
{
    [Fact]
    public async Task A_later_parent_input_cannot_replace_the_child_finding_authority()
    {
        const string findingText = "The neutral fixture contains a durable factual conclusion for the original operator.";
        _start!.EmitStructuredFindings = true;
        _child.PlannedResponses.Enqueue([new TextContent(findingText)]);
        var session = new SessionId("signalr/background-finding-authority");
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        manager.Tell(new JoinSession(subscriber) { SessionId = session, Filter = OutputFilter.Full }, subscriber.Ref);
        await subscriber.ExpectMsgAsync<SessionJoined>(Ceiling, cancellationToken: TestContext.Current.CancellationToken);
        MessageSource Source(string turn, TrustAudience audience, TrustBoundary boundary) => new()
        {
            ChannelType = ChannelType.SignalR, SenderId = new SenderId("operator-a"),
            TurnId = new TurnId(turn), MessageId = turn, Audience = audience, Boundary = boundary,
            Principal = PrincipalClassification.Operator,
            Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted)
        };
        await manager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = session, Content = "Delegate the factual conclusion.",
            Source = Source("original", TrustAudience.Personal, TrustBoundary.Personal)
        }, Ceiling, TestContext.Current.CancellationToken);
        await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<object>(message => message is SubAgentOutput { Phase: SubAgentPhase.Started },
            Ceiling, cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<object>(message => message is TurnCompleted, Ceiling,
            cancellationToken: TestContext.Current.CancellationToken);

        _main.PlannedResponses.Enqueue([new TextContent("The later parent request completed independently.")]);
        await manager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = session, Content = "Answer the later independent request.",
            Source = Source("later", TrustAudience.Team, TrustBoundary.Team)
        }, Ceiling, TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<object>(message => message is TurnCompleted, Ceiling,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(_childRelease.Task.IsCompleted);

        _childRelease.TrySetResult();
        var completed = Assert.IsType<SubAgentOutput>(await subscriber.FishForMessageAsync<object>(
            message => message is SubAgentOutput { Phase: SubAgentPhase.Completed }, Ceiling,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(completed.Success);
        Assert.Equal(1, completed.FindingsCount);
        var checkpoint = await _findingCheckpoint.Finding.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
        Assert.Equal("original", checkpoint.TurnId!.Value.Value);
        var payload = Assert.IsType<MemoryCheckpointPayload>(checkpoint.Payload);
        Assert.Equal(TrustAudience.Personal.ToWireValue(), payload.Audience);
        Assert.Equal(TrustBoundary.Personal.Value, payload.Boundary);
        Assert.Equal(findingText, payload.Content);
        Assert.True(payload.HasAcceptedSubAgentFinding);
        await subscriber.FishForMessageAsync<object>(message => message is TurnCompleted, Ceiling,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(_main.ReceivedMessages[^1].SelectMany(message => message.Contents.OfType<FunctionResultContent>()),
            result => result.Result?.ToString()?.Contains(findingText, StringComparison.Ordinal) == true);
    }
}

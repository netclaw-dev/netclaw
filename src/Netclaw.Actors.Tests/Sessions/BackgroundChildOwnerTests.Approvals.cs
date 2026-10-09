// -----------------------------------------------------------------------
// <copyright file="BackgroundChildOwnerTests.Approvals.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed partial class BackgroundChildOwnerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Child_approval_retains_the_original_requester_after_a_later_parent_turn(bool cancel)
    {
        var session = new SessionId("signalr/background-approval");
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        manager.Tell(new JoinSession(subscriber) { SessionId = session, Filter = OutputFilter.Full }, subscriber.Ref);
        await subscriber.ExpectMsgAsync<SessionJoined>(Ceiling, cancellationToken: TestContext.Current.CancellationToken);
        _child.ToolCallsOnFirstCall = [new FunctionCallContent("child-approval-1", "approval_probe",
            new Dictionary<string, object?> { ["_rationale"] = "Request the fixture operation." })];
        MessageSource Source(string turn, string sender) => new()
        {
            ChannelType = ChannelType.SignalR, SenderId = new SenderId(sender), TurnId = new TurnId(turn),
            MessageId = turn, Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal,
            Principal = PrincipalClassification.Operator,
            Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted)
        };
        await manager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = session, Content = "Start the approval fixture.", Source = Source("original", "operator-a")
        }, Ceiling, TestContext.Current.CancellationToken);
        await _child.FirstCallEntered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<object>(message => message is TurnCompleted, Ceiling,
            cancellationToken: TestContext.Current.CancellationToken);
        _childRelease.TrySetResult();
        var prompt = await subscriber.FishForMessageAsync<object>(message => message is ToolInteractionRequest, Ceiling,
            cancellationToken: TestContext.Current.CancellationToken);
        var request = Assert.IsType<ToolInteractionRequest>(prompt);
        Assert.Equal(new SenderId("operator-a"), request.RequesterSenderId);
        Assert.Equal(0, Volatile.Read(ref _approvedEffects));
        await manager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = session, Content = "Answer an independent question.", Source = Source("later", "operator-b")
        }, Ceiling, TestContext.Current.CancellationToken);
        await subscriber.FishForMessageAsync<object>(message => message is TurnCompleted, Ceiling,
            cancellationToken: TestContext.Current.CancellationToken);
        ToolInteractionResponse Answer(string sender) => new()
        {
            SessionId = session, CallId = request.CallId, SenderId = new SenderId(sender),
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce)
        };
        Assert.IsType<CommandNack>(await manager.Ask<ISessionResponse>(Answer("operator-b"), Ceiling,
            TestContext.Current.CancellationToken));
        Assert.Equal(0, Volatile.Read(ref _approvedEffects));
        if (cancel)
        {
            _main.PlannedResponses.Enqueue([new FunctionCallContent("cancel-approval", "control_probe",
                new Dictionary<string, object?> { ["_rationale"] = "Cancel the fixture.", ["run_id"] = _start!.Prepared!.RunId.Value })]);
            _main.PlannedResponses.Enqueue([new TextContent("The cancellation was admitted.")]);
            await manager.Ask<CommandAck>(new SendUserMessage
            {
                SessionId = session, Content = "Cancel the fixture.", Source = Source("cancel", "operator-a")
            }, Ceiling, TestContext.Current.CancellationToken);
            await AwaitAssertAsync(() => Assert.Contains(_main.ReceivedMessages.SelectMany(static messages => messages)
                .SelectMany(static message => message.Contents.OfType<FunctionResultContent>()),
                static result => result.CallId.StartsWith("child-result-", StringComparison.Ordinal)), Ceiling,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.IsType<CommandNack>(await manager.Ask<ISessionResponse>(Answer("operator-a"), Ceiling,
                TestContext.Current.CancellationToken));
            Assert.Equal(0, Volatile.Read(ref _approvedEffects));
        }
        else
        {
            Assert.IsType<CommandAck>(await manager.Ask<ISessionResponse>(Answer("operator-a"), Ceiling,
                TestContext.Current.CancellationToken));
            await AwaitAssertAsync(() => Assert.Equal(1, Volatile.Read(ref _approvedEffects)), Ceiling,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.IsType<CommandNack>(await manager.Ask<ISessionResponse>(Answer("operator-a"), Ceiling,
                TestContext.Current.CancellationToken));
            Assert.Equal(1, Volatile.Read(ref _approvedEffects));
        }
    }
}

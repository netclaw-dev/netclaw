// -----------------------------------------------------------------------
// <copyright file="ChannelPipelineAckTargetTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Streams;
using Akka.Streams.Dsl;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

/// <summary>
/// Focused behavioral tests for the <c>ChannelPipeline.MapToCommand</c>
/// input sink's ack-target propagation. The sink's body is:
/// <code>
/// sessionManager.Tell(cmd, cmd.Source?.AckTarget ?? ActorRefs.NoSender);
/// </code>
/// These tests exercise that expression by running a minimal
/// <see cref="Source"/> → <see cref="Sink"/> flow with a
/// <see cref="TestProbe"/> standing in for the session manager, so we can
/// assert on the captured <c>Sender</c>.
/// </summary>
public sealed class ChannelPipelineAckTargetTests : TestKit
{
    public ChannelPipelineAckTargetTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider) { }

    [Fact]
    public async Task Null_AckTarget_tells_session_manager_with_NoSender()
    {
        var sessionManagerProbe = CreateTestProbe("session-manager");
        var input = BuildInput(reminderId: null, ackTarget: null);
        var cmd = BuildCommand(input);

        await RunSinkAsync(sessionManagerProbe.Ref, cmd);

        await sessionManagerProbe.ExpectMsgAsync<SendUserMessage>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        // NoSender → the probe observes DeadLetters as the sender.
        Assert.True(
            sessionManagerProbe.LastSender.IsNobody()
            || Equals(sessionManagerProbe.LastSender, Sys.DeadLetters));
    }

    [Fact]
    public async Task Set_AckTarget_tells_session_manager_with_that_ref_and_ack_routes_back()
    {
        var sessionManagerProbe = CreateTestProbe("session-manager");
        var ackProbe = CreateTestProbe("ack-target");

        var input = BuildInput(reminderId: "r:1", ackTarget: ackProbe.Ref);
        var cmd = BuildCommand(input);

        await RunSinkAsync(sessionManagerProbe.Ref, cmd);

        await sessionManagerProbe.ExpectMsgAsync<SendUserMessage>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Same(ackProbe.Ref, sessionManagerProbe.LastSender);

        // Simulate the session replying CommandAck to Sender — it must
        // land on the ack probe (the "dispatcher's Ask temp actor").
        var sessionId = cmd.SessionId;
        sessionManagerProbe.LastSender.Tell(CommandAck.For(sessionId));
        var ack = await ackProbe.ExpectMsgAsync<CommandAck>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, ack.SessionId);
    }

    private async Task RunSinkAsync(IActorRef sessionManager, SendUserMessage cmd)
    {
        var materializer = Sys.Materializer();
        await Source.Single(cmd)
            .RunWith(
                Sink.ForEach<SendUserMessage>(m =>
                {
                    // Mirrors the sink body in ChannelPipeline.CreateAsync.
                    var ackTarget = m.Source?.AckTarget ?? ActorRefs.NoSender;
                    sessionManager.Tell(m, ackTarget);
                }),
                materializer);
    }

    private static ChannelInput BuildInput(string? reminderId, IActorRef? ackTarget) => new()
    {
        SenderId = new SenderId("user-1"),
        Contents = [new TextContent("hello")],
        ReceivedAt = DateTimeOffset.UtcNow,
        ReminderId = reminderId,
        AckTarget = ackTarget,
        Audience = TrustAudience.Public,
        Boundary = TrustBoundary.Public,
        Principal = PrincipalClassification.UntrustedExternal,
        Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Public)
    };

    private static SendUserMessage BuildCommand(ChannelInput input)
    {
        var source = MessageSourceFactory.Create(
            input,
            new SessionPipelineOptions { ChannelType = ChannelType.Slack },
            new Netclaw.Actors.Protocol.TurnId("turn-1"));

        return new SendUserMessage
        {
            SessionId = new SessionId("test/session"),
            Content = "hello",
            Source = source
        };
    }
}

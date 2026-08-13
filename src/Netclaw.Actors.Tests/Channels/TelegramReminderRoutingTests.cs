// -----------------------------------------------------------------------
// <copyright file="TelegramReminderRoutingTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels.Telegram;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Channels;

public sealed class TelegramReminderRoutingTests(ITestOutputHelper output) : TestKit(output: output)
{
    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    [Theory]
    [InlineData("8962863491/chat")]
    [InlineData("-5326927974/chat")]
    public async Task Gateway_routes_trusted_turn_to_the_originating_chat(string sessionId)
    {
        var sink = CreateTestProbe();
        var dependencies = new TelegramGatewayDependencies(
            null!,
            null,
            new TelegramChannelOptions(),
            null!,
            null!,
            new ToolAudienceProfiles(),
            null!,
            null!,
            ConversationPropsFactory: (_, _) => Props.Create(() => new ForwardActor(sink.Ref)));
        var gateway = Sys.ActorOf(TelegramGatewayActor.CreateProps(dependencies));
        var source = new MessageSource
        {
            ChannelType = ChannelType.Telegram,
            SenderId = new SenderId("reminder-system"),
            MessageId = "telegram-reminder:1",
            TurnId = new TurnId("telegram-reminder:1"),
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.TrustedInstance,
            Principal = PrincipalClassification.VerifiedAutomation,
            Provenance = new SourceProvenance(TransportAuthenticity.LocalProcess, PayloadTaint.Trusted),
            ReceivedAt = new DateTimeOffset(2026, 8, 13, 21, 30, 0, TimeSpan.Zero)
        };

        gateway.Tell(new DeliverTrustedSessionTurn(
            new SessionId(sessionId),
            "Run the reminder",
            source));

        var routed = await sink.ExpectMsgAsync<DeliverTrustedSessionTurn>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, routed.SessionId.Value);
    }
}

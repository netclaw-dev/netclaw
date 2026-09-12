// -----------------------------------------------------------------------
// <copyright file="GatewayRoutingContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Xunit;

namespace Netclaw.Actors.Tests.Channels.Contracts;

public abstract class GatewayRoutingContractTests : TestKit
{
    protected GatewayRoutingContractTests(ITestOutputHelper output) : base(output: output) { }

    protected abstract IActorRef CreateGateway(ChannelOptionsBuilder options);

    protected abstract object CreateAllowedMessage(
        string channelId, string threadId, string userId, string text, string eventId);

    protected abstract object CreateDeniedMessage(
        string channelId, string userId, string eventId);

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    [Fact]
    public async Task ACL_denied_message_not_routed()
    {
        var options = new ChannelOptionsBuilder
        {
            AllowedChannelIds = ["ch-allowed"],
            AllowedUserIds = ["user-allowed"]
        };

        var gateway = CreateGateway(options);
        gateway.Tell(CreateDeniedMessage("ch-allowed", "user-denied", "evt-1"));
        await ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Allowed_message_routes_to_session()
    {
        var options = new ChannelOptionsBuilder
        {
            AllowedChannelIds = ["ch-1"]
        };

        var gateway = CreateGateway(options);
        gateway.Tell(CreateAllowedMessage("ch-1", "thread-1", "user-1", "hello", "evt-1"));
        var received = await ExpectMsgAsync<object>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(received);
    }

    [Fact]
    public async Task Duplicate_event_id_dropped()
    {
        var options = new ChannelOptionsBuilder
        {
            AllowedChannelIds = ["ch-1"]
        };

        var gateway = CreateGateway(options);
        var msg = CreateAllowedMessage("ch-1", "thread-1", "user-1", "hello", "evt-dup");

        gateway.Tell(msg);
        await ExpectMsgAsync<object>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);

        gateway.Tell(msg);
        await ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
    }
}

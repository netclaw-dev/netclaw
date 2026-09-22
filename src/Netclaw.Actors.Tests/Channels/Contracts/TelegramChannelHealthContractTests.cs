// -----------------------------------------------------------------------
// <copyright file="TelegramChannelHealthContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Channels;
using Netclaw.Channels.Telegram;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Channels.Contracts;

/// <summary>
/// Telegram proves the base health contract only. The transport state is one
/// connected flag driven by the poll loop, so there is no connected-but-not-ready
/// state and no snapshot health detail — like Slack's socket-mode transport,
/// unlike Discord's and Mattermost's snapshot transports.
/// </summary>
public sealed class TelegramChannelHealthContractTests(ITestOutputHelper output)
    : ChannelHealthContractTests(output)
{
    private TelegramChannel? _channel;

    protected override IChannel CreateChannel(bool enabled)
    {
        var options = new TelegramChannelOptions
        {
            Enabled = enabled,
            BotToken = new SensitiveString("12345:test-token")
        };
        var transport = new TelegramTransport(
            options,
            NullLogger<TelegramTransport>.Instance,
            (_, _) => new TelegramTransportTests.FakeTelegramBotApiClient());

        _channel = new TelegramChannel(
            pipeline: null!,
            ingressGate: null!,
            actorSystem: Sys,
            actorRegistry: ActorRegistry.For(Sys),
            options: options,
            transport: transport,
            logger: NullLogger<TelegramChannel>.Instance,
            contentScanner: null!,
            toolConfig: new ToolConfig(),
            modelCapabilities: new ModelCapabilities(),
            storageResolver: null!,
            channelRegistry: null!,
            timeProvider: TimeProvider.System);

        return _channel;
    }

    protected override async Task SetTransportStateAsync(bool connected, bool ready, string? healthDetail)
    {
        // Guard against future base-contract tests assuming a partial-ready
        // state Telegram cannot represent — fail loud instead of silently
        // collapsing it to connected/disconnected.
        if (connected != ready || healthDetail is not null)
            throw new NotSupportedException(
                "Telegram's polling transport has no connected-but-not-ready state or snapshot detail.");

        // The only way Telegram reaches the connected state is through its own
        // connect path; a freshly constructed channel is already disconnected.
        if (connected)
            await _channel!.StartAsync(CancellationToken.None);
    }

    protected override async Task AfterAllAsync()
    {
        if (_channel is not null)
            await _channel.StopAsync(CancellationToken.None);

        await base.AfterAllAsync();
    }
}

// -----------------------------------------------------------------------
// <copyright file="TelegramChannelShutdownContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Channels;
using Netclaw.Channels.Telegram;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Channels.Contracts;

/// <summary>
/// Telegram holds the shutdown contract by construction: the transport
/// "disconnect" is a local unsubscribe plus a CancellationTokenSource cancel —
/// it never calls the bot API, so a failing disconnect cannot even be
/// constructed. The fixture still wires a live transport (the closest analog
/// of the SIGTERM state) and a dead actor system, and the contract pins that
/// <c>StopAsync</c> completes without propagating anything.
/// </summary>
public sealed class TelegramChannelShutdownContractTests : ChannelShutdownContractTests
{
    protected override IChannel CreateStoppableChannel()
    {
        var options = new TelegramChannelOptions
        {
            Enabled = true,
            BotToken = new SensitiveString("12345:test-token")
        };
        var transport = new TelegramTransport(
            options,
            NullLogger<TelegramTransport>.Instance,
            (_, _) => new TelegramTransportTests.FakeTelegramBotApiClient());

        // GetMe completes synchronously on the fake client, so the start is a
        // plain blocking call here. The channel's own StartAsync never runs —
        // the gateway is never created, matching the contract's stop-path scope.
        transport.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        return new TelegramChannel(
            pipeline: null!,
            ingressGate: null!,
            actorSystem: null!,
            actorRegistry: null!,
            options: options,
            transport: transport,
            logger: NullLogger<TelegramChannel>.Instance,
            contentScanner: null!,
            toolConfig: new ToolConfig(),
            modelCapabilities: new ModelCapabilities(),
            storageResolver: null!,
            channelRegistry: null!,
            timeProvider: TimeProvider.System);
    }
}

// -----------------------------------------------------------------------
// <copyright file="DiscordNetGatewayClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Pattern;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace Netclaw.Channels.Discord.Transport;

internal sealed class DiscordNetGatewayClient : IDiscordGatewayClient, IDiscordGatewayEventSink, IDisposable
{
    private static readonly TimeSpan ConnectAskTimeout = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan SnapshotAskTimeout = TimeSpan.FromSeconds(5);

    private readonly ActorSystem _actorSystem;
    private readonly IActorRef _lifecycleActor;
    private readonly ILogger<DiscordNetGatewayClient> _logger;

    public event Func<DiscordGatewayMessage, Task>? MessageReceived;
    public event Func<DiscordGatewayInteraction, Task>? InteractionReceived;
    public event Func<string, Task>? CleanReconnectRequired;
    public event Func<DiscordGatewaySnapshot, Task>? ConnectionRestored;

    internal enum DiscordChannelKind
    {
        GuildChannel,
        Thread,
        DirectMessage,
    }

    public DiscordNetGatewayClient(
        ActorSystem actorSystem,
        DiscordSocketClient client,
        TimeProvider timeProvider,
        ILogger<DiscordNetGatewayClient> logger)
    {
        _actorSystem = actorSystem;
        _logger = logger;
        _lifecycleActor = actorSystem.ActorOf(
            DiscordNetGatewayLifecycleActor.CreateProps(
                new DiscordSocketGatewayTransport(client),
                timeProvider,
                this,
                logger),
            "discord-net-gateway-lifecycle");
    }

    public Task<DiscordGatewaySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
        _lifecycleActor.Ask<DiscordGatewaySnapshot>(
            DiscordNetGatewayLifecycleActor.GetSnapshot.Instance,
            SnapshotAskTimeout,
            cancellationToken: cancellationToken);

    public Task<DiscordGatewaySnapshot> ConnectAsync(string botToken, CancellationToken cancellationToken = default) =>
        _lifecycleActor.Ask<DiscordGatewaySnapshot>(
            new DiscordNetGatewayLifecycleActor.Connect(botToken),
            ConnectAskTimeout,
            cancellationToken: cancellationToken);

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        // The CLR shutdown hook can terminate the actor system before host
        // shutdown reaches the channel. An Ask to a dead actor dead-letters and
        // stalls for the full ConnectAskTimeout; with the system gone there is
        // nothing left to disconnect (#2035).
        if (_actorSystem.WhenTerminated.IsCompleted)
            return;

        try
        {
            await _lifecycleActor.Ask<DiscordGatewaySnapshot>(
                DiscordNetGatewayLifecycleActor.Disconnect.Instance,
                ConnectAskTimeout,
                cancellationToken: cancellationToken);
        }
        catch (AskTimeoutException ex) when (_actorSystem.WhenTerminated.IsCompleted)
        {
            // The system terminated while the disconnect was in flight; the
            // drain result no longer matters.
            _logger.LogDebug(ex, "Gateway disconnect interrupted by actor system termination during shutdown; drain result discarded.");
        }
    }

    public void Dispose() => _actorSystem.Stop(_lifecycleActor);

    internal static (string ChannelId, string ReplyChannelId, string ThreadOrMessageId) ResolveChannelContext(
        ulong channelId, ulong messageId,
        DiscordChannelKind kind,
        ulong? parentChannelId) =>
        DiscordNetGatewayLifecycleActor.ResolveChannelContext(
            channelId,
            messageId,
            kind switch
            {
                DiscordChannelKind.Thread => DiscordNetGatewayLifecycleActor.DiscordChannelKind.Thread,
                DiscordChannelKind.DirectMessage => DiscordNetGatewayLifecycleActor.DiscordChannelKind.DirectMessage,
                _ => DiscordNetGatewayLifecycleActor.DiscordChannelKind.GuildChannel,
            },
            parentChannelId);

    Task IDiscordGatewayEventSink.PublishMessageAsync(DiscordGatewayMessage message) =>
        MessageReceived?.Invoke(message) ?? Task.CompletedTask;

    Task IDiscordGatewayEventSink.PublishInteractionAsync(DiscordGatewayInteraction interaction) =>
        InteractionReceived?.Invoke(interaction) ?? Task.CompletedTask;

    Task IDiscordGatewayEventSink.PublishCleanReconnectRequiredAsync(string reason) =>
        CleanReconnectRequired?.Invoke(reason) ?? Task.CompletedTask;

    Task IDiscordGatewayEventSink.PublishConnectionRestoredAsync(DiscordGatewaySnapshot snapshot) =>
        ConnectionRestored?.Invoke(snapshot) ?? Task.CompletedTask;
}

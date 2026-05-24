// -----------------------------------------------------------------------
// <copyright file="MattermostNetGatewayClientTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Mattermost.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Channels.Mattermost.Transport;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class MattermostNetGatewayClientTests
{
    [Fact]
    public async Task Disconnect_when_socket_is_already_closed_detaches_sdk_handlers_before_reconnect()
    {
        var sdk = new FakeMattermostSdkClient();
        var client = new MattermostNetGatewayClient(
            sdk,
            new FakeTimeProvider(DateTimeOffset.Parse("2026-05-24T18:45:00Z")),
            NullLogger<MattermostNetGatewayClient>.Instance);

        await client.ConnectAsync("https://mattermost.example.com", "token", TestContext.Current.CancellationToken);

        sdk.IsConnected = false;
        await client.DisconnectAsync(TestContext.Current.CancellationToken);
        await client.ConnectAsync("https://mattermost.example.com", "token", TestContext.Current.CancellationToken);

        Assert.Equal(1, sdk.ActiveMessageHandlerCount);
        Assert.Equal(1, sdk.ActiveConnectedHandlerCount);
        Assert.Equal(1, sdk.ActiveDisconnectedHandlerCount);
        Assert.Equal(1, sdk.ActiveLogHandlerCount);
        Assert.Equal(2, sdk.MessageHandlerAttachCount);
        Assert.Equal(1, sdk.MessageHandlerDetachCount);
    }

    private sealed class FakeMattermostSdkClient : IMattermostSdkClient
    {
        private EventHandler<MessageEventArgs>? _messageReceived;
        private EventHandler<ConnectionEventArgs>? _connected;
        private EventHandler<DisconnectionEventArgs>? _disconnected;
        private EventHandler<LogEventArgs>? _logMessage;

        public bool IsConnected { get; set; }
        public bool IgnoreOwnMessages { get; set; }

        public int MessageHandlerAttachCount { get; private set; }
        public int MessageHandlerDetachCount { get; private set; }

        public int ActiveMessageHandlerCount => _messageReceived?.GetInvocationList().Length ?? 0;
        public int ActiveConnectedHandlerCount => _connected?.GetInvocationList().Length ?? 0;
        public int ActiveDisconnectedHandlerCount => _disconnected?.GetInvocationList().Length ?? 0;
        public int ActiveLogHandlerCount => _logMessage?.GetInvocationList().Length ?? 0;

        public event EventHandler<MessageEventArgs>? OnMessageReceived
        {
            add
            {
                _messageReceived += value;
                MessageHandlerAttachCount++;
            }
            remove
            {
                _messageReceived -= value;
                MessageHandlerDetachCount++;
            }
        }

        public event EventHandler<ConnectionEventArgs>? OnConnected
        {
            add => _connected += value;
            remove => _connected -= value;
        }

        public event EventHandler<DisconnectionEventArgs>? OnDisconnected
        {
            add => _disconnected += value;
            remove => _disconnected -= value;
        }

        public event EventHandler<LogEventArgs>? OnLogMessage
        {
            add => _logMessage += value;
            remove => _logMessage -= value;
        }

        public Task<MattermostGatewayBotIdentity> GetMeAsync()
        {
            IsConnected = true;
            return Task.FromResult(new MattermostGatewayBotIdentity("bot-user", "netclaw-bot"));
        }

        public Task StartReceivingAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task StopReceivingAsync()
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public Task<MattermostGatewayFileDetails> GetFileDetailsAsync(string fileId)
            => Task.FromResult(new MattermostGatewayFileDetails(fileId, "application/octet-stream", 0));
    }
}

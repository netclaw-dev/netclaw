// -----------------------------------------------------------------------
// <copyright file="TelegramChannel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Pattern;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Channels;
using Netclaw.Configuration;
using Netclaw.Security;

namespace Netclaw.Channels.Telegram;

public sealed class TelegramChannel : IChannel
{
    private readonly ISessionPipeline _pipeline;
    private readonly SessionIngressGate _ingressGate;
    private readonly ActorSystem _actorSystem;
    private readonly IActorRegistry _actorRegistry;
    private readonly TelegramChannelOptions _options;
    private readonly TelegramTransport _transport;
    private readonly ILogger<TelegramChannel> _logger;
    private readonly IContentScanner _contentScanner;
    private readonly ToolAudienceProfiles _audienceProfiles;
    private readonly ModelCapabilities _modelCapabilities;
    private readonly NetclawPaths _paths;
    private readonly IChannelRegistry _channelRegistry;
    private IActorRef? _gateway;
    private volatile bool _connected;
    private volatile string? _failureDetail;

    public TelegramChannel(
        ISessionPipeline pipeline,
        SessionIngressGate ingressGate,
        ActorSystem actorSystem,
        IActorRegistry actorRegistry,
        TelegramChannelOptions options,
        TelegramTransport transport,
        ILogger<TelegramChannel> logger,
        IContentScanner contentScanner,
        ToolConfig toolConfig,
        ModelCapabilities modelCapabilities,
        NetclawPaths paths,
        IChannelRegistry channelRegistry)
    {
        _pipeline = pipeline;
        _ingressGate = ingressGate;
        _actorSystem = actorSystem;
        _actorRegistry = actorRegistry;
        _options = options;
        _transport = transport;
        _logger = logger;
        _contentScanner = contentScanner;
        _audienceProfiles = toolConfig.AudienceProfiles;
        _modelCapabilities = modelCapabilities;
        _paths = paths;
        _channelRegistry = channelRegistry;
    }

    public ChannelType ChannelType => ChannelType.Telegram;

    public string DisplayName => "Telegram";

    internal IActorRef? Gateway => _gateway;

    public ValueTask<ChannelHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return ValueTask.FromResult(new ChannelHealth(ChannelHealthStatus.Degraded, "Telegram channel disabled."));

        return ValueTask.FromResult(_connected
            ? new ChannelHealth(ChannelHealthStatus.Healthy)
            : new ChannelHealth(ChannelHealthStatus.Disconnected, _failureDetail ?? "Telegram bot disconnected."));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Telegram channel disabled by configuration.");
            return;
        }

        try
        {
            _gateway = _actorSystem.ActorOf(
                TelegramGatewayActor.CreateProps(new TelegramGatewayDependencies(
                    _pipeline,
                    _ingressGate,
                    _options,
                    _transport,
                    _contentScanner,
                    _audienceProfiles,
                    _modelCapabilities,
                    _paths,
                    _channelRegistry)),
                "telegram-gateway");

            _actorRegistry.Register<TelegramGatewayActorKey>(_gateway);
            _transport.MessageReceived += HandleMessageAsync;
            _transport.CallbackReceived += HandleCallbackAsync;
            _transport.PollingFailed += HandlePollingFailureAsync;
            _transport.PollingRecovered += HandlePollingRecoveryAsync;
            await _transport.StartAsync(cancellationToken);
            _connected = true;
            _failureDetail = null;
            _logger.LogInformation("Telegram channel connected.");
            Console.WriteLine("Telegram channel connected. The bot is ready for messages.");
        }
        catch (Exception ex)
        {
            _failureDetail = ex.Message;
            _logger.LogError(ex, "Telegram channel could not connect; the rest of NetClaw will continue.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _connected = false;
        _transport.MessageReceived -= HandleMessageAsync;
        _transport.CallbackReceived -= HandleCallbackAsync;
        _transport.PollingFailed -= HandlePollingFailureAsync;
        _transport.PollingRecovered -= HandlePollingRecoveryAsync;
        await _transport.StopAsync();

        if (_gateway is not null)
        {
            try
            {
                await _gateway.GracefulStop(TimeSpan.FromSeconds(5));
            }
            catch
            {
                _actorSystem.Stop(_gateway);
            }

            _gateway = null;
        }
    }

    private Task HandleMessageAsync(TelegramInboundMessage message)
    {
        _gateway?.Tell(message);
        return Task.CompletedTask;
    }

    private Task HandleCallbackAsync(TelegramCallbackQuery callback)
    {
        _gateway?.Tell(callback);
        return Task.CompletedTask;
    }

    private Task HandlePollingFailureAsync(string detail)
    {
        _connected = false;
        _failureDetail = detail;
        return Task.CompletedTask;
    }

    private Task HandlePollingRecoveryAsync()
    {
        _connected = true;
        _failureDetail = null;
        return Task.CompletedTask;
    }
}

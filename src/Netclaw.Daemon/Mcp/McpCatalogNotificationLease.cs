// -----------------------------------------------------------------------
// <copyright file="McpCatalogNotificationLease.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Netclaw.Tools;

namespace Netclaw.Daemon.Mcp;

internal sealed class McpCatalogNotificationLease : IAsyncDisposable
{
    internal const string SubscriptionId = "netclaw-catalog";
    internal static readonly TimeSpan AcknowledgementTimeout = TimeSpan.FromSeconds(15);

    private readonly McpServerName _serverName;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<McpClientManager> _logger;
    private readonly Func<McpCatalogNotificationLease, CancellationToken, Task> _refresh;
    private readonly Channel<byte> _signals;
    private readonly Channel<byte> _refreshCompletions;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly CancellationTokenSource _listenerCancellation;
    private readonly TaskCompletionSource _activation =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<SubscriptionsListenNotifications?> _acknowledgement =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _listenerStopped =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IReadOnlyList<KeyValuePair<string, Func<JsonRpcNotification, CancellationToken, ValueTask>>> _handlers;
    private readonly Task _worker;
    private Task? _listener;
    private int _mode;
    private int _toolsEnabled;
    private int _promptsEnabled;
    private int _disposed;

    public McpCatalogNotificationLease(
        McpServerName serverName,
        TimeProvider timeProvider,
        ILogger<McpClientManager> logger,
        Func<McpCatalogNotificationLease, CancellationToken, Task> refresh)
    {
        _serverName = serverName;
        _timeProvider = timeProvider;
        _logger = logger;
        _refresh = refresh;
        _signals = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
        _listenerCancellation = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
        _refreshCompletions = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = true,
        });
        _handlers =
        [
            new(NotificationMethods.SubscriptionsAcknowledgedNotification, HandleAcknowledgementAsync),
            new(NotificationMethods.ToolListChangedNotification, HandleToolChangedAsync),
            new(NotificationMethods.PromptListChangedNotification, HandlePromptChangedAsync),
        ];
        _worker = RunWorkerAsync();
    }

    public IEnumerable<KeyValuePair<string, Func<JsonRpcNotification, CancellationToken, ValueTask>>> Handlers
        => _handlers;

    public McpServerName ServerName => _serverName;

    internal bool ToolsEnabled => Volatile.Read(ref _toolsEnabled) != 0;

    internal bool PromptsEnabled => Volatile.Read(ref _promptsEnabled) != 0;

    internal McpCatalogNotificationMode Mode => (McpCatalogNotificationMode)Volatile.Read(ref _mode);

    internal ValueTask<byte> WaitForRefreshCompletionAsync(CancellationToken cancellationToken)
        => _refreshCompletions.Reader.ReadAsync(cancellationToken);

    internal Task WaitForListenerStopAsync(CancellationToken cancellationToken)
        => _listenerStopped.Task.WaitAsync(cancellationToken);

    public async Task EstablishAsync(
        McpClient client,
        IMcpClientRuntime runtime,
        CancellationToken cancellationToken)
    {
        var profile = runtime.GetCatalogNotificationProfile(client);
        if (!string.Equals(profile.ProtocolVersion, "2026-07-28", StringComparison.Ordinal))
        {
            EstablishLegacy(profile);
            return;
        }

        Volatile.Write(ref _mode, (int)McpCatalogNotificationMode.ModernPending);
        var requested = new SubscriptionsListenNotifications
        {
            ToolsListChanged = true,
            PromptsListChanged = true,
        };
        _listener = runtime.ListenForCatalogChangesAsync(
            client,
            new RequestId(SubscriptionId),
            requested,
            _listenerCancellation.Token);

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeout = Task.Delay(AcknowledgementTimeout, _timeProvider, timeoutCancellation.Token);
        var completed = await Task.WhenAny(_acknowledgement.Task, _listener, timeout);

        if (completed == _acknowledgement.Task)
        {
            timeoutCancellation.Cancel();
            var accepted = await _acknowledgement.Task;
            if (accepted is null)
            {
                DisableModernListener();
                return;
            }

            Volatile.Write(ref _toolsEnabled, accepted.ToolsListChanged is true ? 1 : 0);
            Volatile.Write(ref _promptsEnabled, accepted.PromptsListChanged is true ? 1 : 0);
            Volatile.Write(ref _mode, (int)McpCatalogNotificationMode.Modern);
            _logger.LogInformation(
                "MCP server '{Name}' accepted modern catalog notifications (tools: {Tools}, prompts: {Prompts})",
                _serverName.Value,
                ToolsEnabled,
                PromptsEnabled);
            _ = ObserveListenerAsync(_listener);
            return;
        }

        if (completed == timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DisableModernListener();
            _logger.LogWarning(
                "MCP server '{Name}' did not acknowledge catalog notifications within {TimeoutSeconds} seconds; catalog polling remains active",
                _serverName.Value,
                AcknowledgementTimeout.TotalSeconds);
            return;
        }

        timeoutCancellation.Cancel();
        try
        {
            await _listener;
            _logger.LogWarning(
                "MCP server '{Name}' ended the catalog notification request before acknowledgement; catalog polling remains active",
                _serverName.Value);
        }
        catch (OperationCanceledException) when (_listenerCancellation.IsCancellationRequested)
        {
            LogCleanupCancellation("request setup");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "MCP server '{Name}' rejected the catalog notification request ({FailureType}); catalog polling remains active",
                _serverName.Value,
                ex.GetType().Name);
        }

        DisableModernListener();
    }

    public void Activate()
    {
        if (Volatile.Read(ref _disposed) == 0)
            _activation.TrySetResult();
    }

    public void Deactivate()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Volatile.Write(ref _mode, (int)McpCatalogNotificationMode.Disabled);
        Volatile.Write(ref _toolsEnabled, 0);
        Volatile.Write(ref _promptsEnabled, 0);
        _signals.Writer.TryComplete();
        _refreshCompletions.Writer.TryComplete();
        _cancellation.Cancel();
        _activation.TrySetCanceled(_cancellation.Token);
    }

    public async ValueTask DisposeAsync()
    {
        Deactivate();

        try
        {
            await _worker;
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            LogCleanupCancellation("worker");
        }

        if (_listener is not null)
        {
            try
            {
                await _listener;
            }
            catch (OperationCanceledException) when (_listenerCancellation.IsCancellationRequested)
            {
                LogCleanupCancellation("listener");
            }
            catch (Exception ex)
            {
                // EstablishAsync or ObserveListenerAsync reports the safe failure category.
                _logger.LogDebug(
                    "MCP server '{Name}' catalog notification listener cleanup observed {FailureType}",
                    _serverName.Value,
                    ex.GetType().Name);
            }
        }

        _listenerCancellation.Dispose();
        _cancellation.Dispose();
    }

    private void EstablishLegacy(McpCatalogNotificationProfile profile)
    {
        Volatile.Write(ref _toolsEnabled, profile.ToolsListChanged ? 1 : 0);
        Volatile.Write(ref _promptsEnabled, profile.PromptsListChanged ? 1 : 0);
        Volatile.Write(ref _mode, (int)McpCatalogNotificationMode.Legacy);

        if (ToolsEnabled || PromptsEnabled)
        {
            _logger.LogInformation(
                "MCP server '{Name}' uses legacy catalog notifications (tools: {Tools}, prompts: {Prompts})",
                _serverName.Value,
                ToolsEnabled,
                PromptsEnabled);
            return;
        }

        _logger.LogInformation(
            "MCP server '{Name}' does not declare catalog notification support; catalog polling remains active",
            _serverName.Value);
    }

    private ValueTask HandleAcknowledgementAsync(
        JsonRpcNotification notification,
        CancellationToken cancellationToken)
    {
        if (Mode is not McpCatalogNotificationMode.ModernPending
            || !HasMatchingSubscriptionId(notification.Params))
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            var acknowledgement = notification.Params?.Deserialize<SubscriptionsAcknowledgedNotificationParams>(
                McpJsonUtilities.DefaultOptions);
            if (acknowledgement?.Notifications is not { } accepted)
            {
                RejectAcknowledgement();
                return ValueTask.CompletedTask;
            }

            _acknowledgement.TrySetResult(accepted);
        }
        catch (JsonException)
        {
            RejectAcknowledgement();
        }

        return ValueTask.CompletedTask;
    }

    private void RejectAcknowledgement()
    {
        if (!_acknowledgement.TrySetResult(null))
            return;

        _logger.LogWarning(
            "MCP server '{Name}' sent an invalid catalog notification acknowledgement; catalog polling remains active",
            _serverName.Value);
    }

    private ValueTask HandleToolChangedAsync(
        JsonRpcNotification notification,
        CancellationToken cancellationToken)
    {
        if (ToolsEnabled && AcceptsNotification(notification))
            _signals.Writer.TryWrite(0);
        return ValueTask.CompletedTask;
    }

    private ValueTask HandlePromptChangedAsync(
        JsonRpcNotification notification,
        CancellationToken cancellationToken)
    {
        if (PromptsEnabled && AcceptsNotification(notification))
            _signals.Writer.TryWrite(0);
        return ValueTask.CompletedTask;
    }

    private bool AcceptsNotification(JsonRpcNotification notification)
        => Mode switch
        {
            McpCatalogNotificationMode.Legacy => true,
            McpCatalogNotificationMode.Modern => HasMatchingSubscriptionId(notification.Params),
            _ => false,
        };

    private static bool HasMatchingSubscriptionId(JsonNode? parameters)
    {
        if (parameters is not JsonObject values
            || values["_meta"] is not JsonObject metadata
            || metadata[MetaKeys.SubscriptionId] is not JsonValue subscriptionId)
        {
            return false;
        }

        return subscriptionId.TryGetValue<string>(out var value)
               && string.Equals(value, SubscriptionId, StringComparison.Ordinal);
    }

    private async Task RunWorkerAsync()
    {
        await _activation.Task.WaitAsync(_cancellation.Token);
        await foreach (var _ in _signals.Reader.ReadAllAsync(_cancellation.Token))
        {
            await _refresh(this, _cancellation.Token);
            _refreshCompletions.Writer.TryWrite(0);
        }
    }

    private async Task ObserveListenerAsync(Task listener)
    {
        try
        {
            await listener;
            if (!_listenerCancellation.IsCancellationRequested)
            {
                DisableModernListener();
                _logger.LogWarning(
                    "MCP server '{Name}' ended the catalog notification request; catalog polling remains active",
                    _serverName.Value);
            }
        }
        catch (OperationCanceledException) when (_listenerCancellation.IsCancellationRequested)
        {
            LogCleanupCancellation("listener observation");
        }
        catch (Exception ex)
        {
            DisableModernListener();
            _logger.LogWarning(
                "MCP server '{Name}' lost the catalog notification request ({FailureType}); catalog polling remains active",
                _serverName.Value,
                ex.GetType().Name);
        }
    }

    private void DisableModernListener()
    {
        Volatile.Write(ref _mode, (int)McpCatalogNotificationMode.Disabled);
        Volatile.Write(ref _toolsEnabled, 0);
        Volatile.Write(ref _promptsEnabled, 0);
        _listenerCancellation.Cancel();
        _listenerStopped.TrySetResult();
    }

    private void LogCleanupCancellation(string operation)
        => _logger.LogDebug(
            "MCP server '{Name}' catalog notification {Operation} stopped during lease cleanup",
            _serverName.Value,
            operation);
}

internal sealed record McpCatalogNotificationProfile(
    string? ProtocolVersion,
    bool ToolsListChanged,
    bool PromptsListChanged);

internal enum McpCatalogNotificationMode
{
    Disabled,
    ModernPending,
    Modern,
    Legacy,
}

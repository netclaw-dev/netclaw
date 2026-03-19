using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Wraps an <see cref="IChatClientProvider"/> and decorates each client with
/// logging and retry. For the <see cref="ModelRole.Main"/> role, additionally
/// wraps in <see cref="FailoverChatClient"/> if a distinct fallback is configured,
/// or <see cref="AlertingChatClientDecorator"/> for single-provider setups.
/// </summary>
public sealed class ResilientChatClientProviderDecorator : IChatClientProvider
{
    private readonly IChatClient _main;
    private readonly IChatClient _compaction;

    public ResilientChatClientProviderDecorator(
        IChatClientProvider inner,
        RetryPolicy retryPolicy,
        ModelSelection models,
        ILoggerFactory loggerFactory,
        IOperationalNotificationSink notificationSink,
        TimeProvider? timeProvider = null)
    {
        var tp = timeProvider ?? TimeProvider.System;
        var retryLogger = loggerFactory.CreateLogger<RetryingChatClient>();
        var loggingLogger = loggerFactory.CreateLogger<LoggingChatClient>();
        var failoverLogger = loggerFactory.CreateLogger<FailoverChatClient>();

        // Decorate main: Logging → Retry → raw
        var rawMain = inner.GetClient(ModelRole.Main);
        var decoratedMain = Decorate(rawMain, retryPolicy, retryLogger, loggingLogger, tp);

        // If fallback is a distinct provider, wrap in FailoverChatClient
        if (models.Fallback is not null)
        {
            var rawFallback = inner.GetClient(ModelRole.Fallback);
            // Only wrap in failover if fallback is actually a different client
            if (!ReferenceEquals(rawFallback, rawMain))
            {
                var decoratedFallback = Decorate(rawFallback, retryPolicy, retryLogger, loggingLogger, tp);
                _main = new FailoverChatClient(
                    decoratedMain, decoratedFallback, failoverLogger, notificationSink, tp);
            }
            else
            {
                _main = new AlertingChatClientDecorator(decoratedMain, notificationSink, tp);
            }
        }
        else
        {
            _main = new AlertingChatClientDecorator(decoratedMain, notificationSink, tp);
        }

        // Decorate compaction
        var rawCompaction = inner.GetClient(ModelRole.Compaction);
        _compaction = ReferenceEquals(rawCompaction, rawMain)
            ? _main // reuse the decorated main if compaction falls back to it
            : Decorate(rawCompaction, retryPolicy, retryLogger, loggingLogger, tp);
    }

    public IChatClient GetClient(ModelRole role) => role switch
    {
        ModelRole.Compaction => _compaction,
        _ => _main
    };

    private static IChatClient Decorate(
        IChatClient raw,
        RetryPolicy policy,
        ILogger retryLogger,
        ILogger loggingLogger,
        TimeProvider tp)
    {
        // Inner → Retry → Logging (logging is outermost so it captures retry time)
        var retrying = new RetryingChatClient(raw, policy, retryLogger, tp);
        return new LoggingChatClient(retrying, loggingLogger, tp);
    }
}

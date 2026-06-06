// -----------------------------------------------------------------------
// <copyright file="PipelineChatClientFactory.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Builds the fully-composed middleware pipeline for a single <c>(provider, model)</c>:
/// <c>Logging → Retry → VendorOptions → raw provider client</c>. The leaf
/// (raw provider client plus any vendor-options wrap) comes from
/// <see cref="ProviderPluginFactory"/>; this layer adds the cross-cutting Retry and
/// Logging decorators via <see cref="ChatClientBuilder"/>.
///
/// One pipeline is built per configured model; routing (selecting which pipeline to
/// invoke per call) is a separate concern owned by the router.
/// </summary>
public sealed class PipelineChatClientFactory
{
    private readonly ProviderPluginFactory _factory;
    private readonly RetryPolicy _retryPolicy;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TimeProvider _timeProvider;

    public PipelineChatClientFactory(
        ProviderPluginFactory factory,
        RetryPolicy retryPolicy,
        ILoggerFactory loggerFactory,
        TimeProvider? timeProvider = null)
    {
        _factory = factory;
        _retryPolicy = retryPolicy;
        _loggerFactory = loggerFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public IChatClient Create(ModelReference model) =>
        Compose(_factory.Create(model), _retryPolicy, _loggerFactory, _timeProvider);

    /// <summary>
    /// Composes the cross-cutting middleware around a leaf client. Internal so the
    /// middleware <b>order</b> can be asserted in isolation: <see cref="ChatClientBuilder"/>
    /// applies the <i>first-registered</i> factory outermost, so <see cref="LoggingChatClient"/>
    /// — which must capture the total elapsed time including retries — is registered
    /// before <see cref="RetryingChatClient"/>.
    /// </summary>
    internal static IChatClient Compose(
        IChatClient leaf,
        RetryPolicy retryPolicy,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider)
    {
        var loggingLogger = loggerFactory.CreateLogger<LoggingChatClient>();
        var retryLogger = loggerFactory.CreateLogger<RetryingChatClient>();

        return new ChatClientBuilder(leaf)
            .Use(inner => new LoggingChatClient(inner, loggingLogger, timeProvider))
            .Use(inner => new RetryingChatClient(inner, retryPolicy, retryLogger, timeProvider))
            .Build();
    }
}

// -----------------------------------------------------------------------
// <copyright file="IProviderOAuthCallbackListener.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Providers.OAuth;
using Microsoft.Extensions.Logging;

namespace Netclaw.Daemon.Providers;

internal interface IProviderOAuthCallbackListener
{
    void StartListening(string redirectUri, string state);
}

internal sealed class ProviderOAuthCallbackListener : IProviderOAuthCallbackListener
{
    private readonly OAuthPkceService _pkceService;
    private readonly ILogger<ProviderOAuthCallbackListener> _logger;

    public ProviderOAuthCallbackListener(
        OAuthPkceService pkceService,
        ILogger<ProviderOAuthCallbackListener> logger)
    {
        _pkceService = pkceService;
        _logger = logger;
    }

    public void StartListening(string redirectUri, string state)
    {
        var task = _pkceService.ListenForCallbackAsync(redirectUri, state);
        _ = task.ContinueWith(t =>
            {
                var exception = t.Exception?.GetBaseException();
                _logger.LogWarning(
                    exception,
                    "Provider OAuth callback listener failed for state {State}",
                    state);
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}

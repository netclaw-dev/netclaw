using Netclaw.Providers.OAuth;

namespace Netclaw.Daemon.Providers;

internal interface IProviderOAuthCallbackListener
{
    void StartListening(string redirectUri, string state);
}

internal sealed class ProviderOAuthCallbackListener : IProviderOAuthCallbackListener
{
    private readonly OAuthPkceService _pkceService;

    public ProviderOAuthCallbackListener(OAuthPkceService pkceService)
    {
        _pkceService = pkceService;
    }

    public void StartListening(string redirectUri, string state)
    {
        _ = _pkceService.ListenForCallbackAsync(redirectUri, state);
    }
}

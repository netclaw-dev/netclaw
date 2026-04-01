using Microsoft.AspNetCore.SignalR.Client;

namespace Netclaw.Cli.Daemon;

/// <summary>
/// Extension methods for <see cref="HubConnectionBuilder"/> to support
/// optional bearer token attachment.
/// </summary>
public static class HubConnectionBuilderExtensions
{
    /// <summary>
    /// Configures the hub URL and optionally attaches a bearer token provider.
    /// <para>
    /// When <paramref name="tokenFactory"/> is <c>null</c> (loopback connections),
    /// this behaves identically to <c>.WithUrl(hubUrl)</c> — no token is sent.
    /// </para>
    /// <para>
    /// When a factory is provided (remote device connections), the factory is
    /// set as <c>AccessTokenProvider</c> on the underlying HTTP connection options
    /// and called before each connection attempt.
    /// </para>
    /// </summary>
    /// <param name="builder">The hub connection builder.</param>
    /// <param name="hubUrl">The hub endpoint URL.</param>
    /// <param name="tokenFactory">
    /// Optional factory that returns a bearer token string, or <c>null</c> to
    /// send no token (loopback / unauthenticated scenarios).
    /// </param>
    public static IHubConnectionBuilder ConfigureAccessToken(
        this HubConnectionBuilder builder,
        string hubUrl,
        Func<Task<string?>>? tokenFactory)
    {
        return tokenFactory is null
            ? builder.WithUrl(hubUrl)
            : builder.WithUrl(hubUrl, options => { options.AccessTokenProvider = tokenFactory; });
    }
}

// -----------------------------------------------------------------------
// <copyright file="NetclawHeadersHandler.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration.Http;

/// <summary>
/// <see cref="DelegatingHandler"/> that adds the shared Netclaw User-Agent
/// and a component identifier header to every outgoing request. Existing
/// User-Agent values on the request are preserved — callers that intentionally
/// spoof a UA (e.g. web fetch or DDG scraping) bypass the header by not
/// registering this handler in the first place.
/// </summary>
public sealed class NetclawHeadersHandler : DelegatingHandler
{
    private readonly string _component;

    public NetclawHeadersHandler(string component)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        _component = component;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Contains() looks at the raw header bag, so it catches both the typed
        // UserAgent collection and untyped values pushed via
        // TryAddWithoutValidation. UserAgent.Count == 0 would miss the latter
        // and re-stamp a duplicate header on the wire.
        if (!request.Headers.Contains("User-Agent"))
            request.Headers.TryAddWithoutValidation("User-Agent", NetclawUserAgent.Value);

        if (!request.Headers.Contains(NetclawUserAgent.ComponentHeader))
            request.Headers.TryAddWithoutValidation(NetclawUserAgent.ComponentHeader, _component);

        return base.SendAsync(request, cancellationToken);
    }
}

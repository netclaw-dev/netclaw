// -----------------------------------------------------------------------
// <copyright file="SessionAffinityHandler.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Providers;

/// <summary>
/// <see cref="DelegatingHandler"/> that reads the ambient session ID from
/// <see cref="SessionAffinityContext"/> and promotes it to an
/// <c>X-Session-Id</c> HTTP header. When the ambient value is <c>null</c>
/// (sidecar calls, startup probes), no header is added and the load
/// balancer falls back to its default policy (typically round-robin).
/// </summary>
public sealed class SessionAffinityHandler : DelegatingHandler
{
    public const string HeaderName = "X-Session-Id";

    public SessionAffinityHandler() : base(new HttpClientHandler()) { }

    public SessionAffinityHandler(HttpMessageHandler innerHandler) : base(innerHandler) { }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var sessionId = SessionAffinityContext.SessionId;
        if (!string.IsNullOrEmpty(sessionId))
            request.Headers.TryAddWithoutValidation(HeaderName, sessionId);

        return base.SendAsync(request, cancellationToken);
    }
}

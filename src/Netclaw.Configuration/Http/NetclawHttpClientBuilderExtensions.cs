// -----------------------------------------------------------------------
// <copyright file="NetclawHttpClientBuilderExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Netclaw.Configuration.Http;

/// <summary>
/// Extensions for wiring the shared Netclaw User-Agent and component header
/// onto named or typed <see cref="HttpClient"/> registrations.
/// </summary>
public static class NetclawHttpClientBuilderExtensions
{
    /// <summary>
    /// Attaches <see cref="NetclawHeadersHandler"/> to this <see cref="HttpClient"/>
    /// registration so every outbound request carries the canonical Netclaw
    /// User-Agent and an <c>X-Netclaw-Component</c> header.
    /// </summary>
    /// <param name="builder">The <see cref="IHttpClientBuilder"/> being configured.</param>
    /// <param name="component">
    /// Short identifier for the calling subsystem (e.g. "mcp", "webhook",
    /// "update-check"). Should be lowercase kebab/snake-case so server
    /// operators can match on it cleanly.
    /// </param>
    public static IHttpClientBuilder AddNetclawHeaders(this IHttpClientBuilder builder, string component)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(component);

        builder.Services.TryAddTransient<NetclawHeadersHandler>();
        return builder.AddHttpMessageHandler(_ => new NetclawHeadersHandler(component));
    }
}

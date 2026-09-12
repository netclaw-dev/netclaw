// -----------------------------------------------------------------------
// <copyright file="WebFetchConfig.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Configuration for the web_fetch tool's HTTP/HTTPS policy.
/// </summary>
public sealed class WebFetchConfig
{
    /// <summary>
    /// When true, web_fetch rejects plain HTTP URLs unless the host is in <see cref="HttpAllowList"/>.
    /// </summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>
    /// Hosts allowed to use plain HTTP when <see cref="RequireHttps"/> is true.
    /// Default includes loopback addresses (localhost, 127.0.0.1, ::1).
    /// </summary>
    public List<string> HttpAllowList { get; set; } = ["localhost", "127.0.0.1", "::1", "[::1]"];
}

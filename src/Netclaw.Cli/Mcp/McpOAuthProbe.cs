// -----------------------------------------------------------------------
// <copyright file="McpOAuthProbe.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;

namespace Netclaw.Cli.Mcp;

/// <summary>
/// Result of a best-effort OAuth capability probe against an MCP server endpoint.
/// <see cref="OAuthRequired"/> is true when the server advertises RFC 9728
/// protected-resource metadata naming at least one authorization server.
/// <see cref="DynamicRegistrationAvailable"/> is true when that authorization
/// server publishes a registration endpoint (RFC 7591), which lets
/// <c>netclaw mcp auth</c> register a client automatically instead of requiring
/// a pre-registered <c>--client-id</c>.
/// </summary>
internal sealed record McpOAuthProbeResult(bool OAuthRequired, bool DynamicRegistrationAvailable);

/// <summary>
/// Discovers whether an HTTP/SSE MCP endpoint requires OAuth authorization, and
/// whether its authorization server supports dynamic client registration.
/// Best-effort: any probe failure yields <c>null</c> so callers can degrade to
/// their pre-probe behavior instead of failing the enclosing command.
/// </summary>
internal static class McpOAuthProbe
{
    private const string ProtectedResourceSegment = "/.well-known/oauth-protected-resource";
    private const string AuthorizationServerSegment = "/.well-known/oauth-authorization-server";

    /// <summary>
    /// Probes <paramref name="endpointUrl"/> for OAuth requirements. Returns
    /// <c>null</c> when the endpoint publishes no usable protected-resource
    /// metadata (or when any request fails), meaning OAuth could not be
    /// positively detected.
    /// </summary>
    public static async Task<McpOAuthProbeResult?> DetectAsync(
        string endpointUrl,
        HttpClient client,
        CancellationToken ct)
    {
        var issuer = await DiscoverIssuerAsync(endpointUrl, client, ct);
        if (issuer is null)
            return null;

        var dynamicRegistration = await DiscoverDynamicRegistrationAsync(issuer, client, ct);
        return new McpOAuthProbeResult(OAuthRequired: true, DynamicRegistrationAvailable: dynamicRegistration);
    }

    /// <summary>
    /// RFC 9728: the protected-resource metadata document lives at
    /// <c>/.well-known/oauth-protected-resource</c> on the resource's origin,
    /// with the resource path appended as a suffix. Try the path-suffixed form
    /// first, then fall back to the origin-level document.
    /// </summary>
    private static async Task<string?> DiscoverIssuerAsync(
        string endpointUrl,
        HttpClient client,
        CancellationToken ct)
    {
        var resource = new Uri(endpointUrl);
        var origin = resource.GetLeftPart(UriPartial.Authority);
        var path = resource.AbsolutePath.TrimEnd('/');

        var candidates = string.IsNullOrEmpty(path) || path == "/"
            ? new[] { $"{origin}{ProtectedResourceSegment}" }
            : [$"{origin}{ProtectedResourceSegment}{path}", $"{origin}{ProtectedResourceSegment}"];

        foreach (var candidate in candidates)
        {
            using var document = await TryGetJsonAsync(client, candidate, ct);
            if (document is null)
                continue;

            if (document.RootElement.TryGetProperty("authorization_servers", out var servers)
                && servers.ValueKind == JsonValueKind.Array
                && servers.GetArrayLength() > 0)
            {
                foreach (var server in servers.EnumerateArray())
                {
                    if (server.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(server.GetString()))
                        return server.GetString();
                }
            }
        }

        return null;
    }

    /// <summary>
    /// RFC 8414: the authorization-server metadata document lives at
    /// <c>/.well-known/oauth-authorization-server</c> on the issuer's origin.
    /// A non-empty <c>registration_endpoint</c> means dynamic client
    /// registration (RFC 7591) is available.
    /// </summary>
    private static async Task<bool> DiscoverDynamicRegistrationAsync(
        string issuer,
        HttpClient client,
        CancellationToken ct)
    {
        var authServer = new Uri(issuer);
        var origin = authServer.GetLeftPart(UriPartial.Authority);
        var path = authServer.AbsolutePath.TrimEnd('/');

        var candidates = string.IsNullOrEmpty(path) || path == "/"
            ? new[] { $"{origin}{AuthorizationServerSegment}" }
            : [$"{origin}{AuthorizationServerSegment}{path}", $"{origin}{AuthorizationServerSegment}"];

        foreach (var candidate in candidates)
        {
            using var document = await TryGetJsonAsync(client, candidate, ct);
            if (document is null)
                continue;

            if (document.RootElement.TryGetProperty("registration_endpoint", out var registration)
                && registration.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(registration.GetString()))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<JsonDocument?> TryGetJsonAsync(
        HttpClient client,
        string url,
        CancellationToken ct)
    {
        try
        {
            using var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var body = await response.Content.ReadAsStringAsync(ct);
            return JsonDocument.Parse(body);
        }
        catch (Exception ex) when (ex is HttpRequestException
            or TaskCanceledException
            or OperationCanceledException
            or JsonException
            or NotSupportedException)
        {
            // Best-effort probe: an unreachable or malformed endpoint means
            // "could not detect OAuth", never a failure of the caller.
            return null;
        }
    }
}

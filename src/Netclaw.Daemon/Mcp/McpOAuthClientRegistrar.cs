// -----------------------------------------------------------------------
// <copyright file="McpOAuthClientRegistrar.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Netclaw.Tools;

namespace Netclaw.Daemon.Mcp;

/// <summary>
/// Raised when an MCP server requires OAuth but Netclaw cannot obtain a client
/// identity for it. The message is operator-facing and names the remedy.
/// </summary>
internal sealed class McpOAuthRegistrationException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

/// <summary>
/// RFC 7591 dynamic client registration, owned by Netclaw rather than the MCP SDK.
///
/// The SDK hard-codes <c>token_endpoint_auth_method: "client_secret_post"</c> in its
/// registration request and never consults the authorization server's advertised
/// <c>token_endpoint_auth_methods_supported</c> (csharp-sdk#1611; unfixed in 1.4.1,
/// every 2.0 prerelease, and main — PR #1615 only covers the token request). Servers
/// that accept public clients only, such as TextForge, reject that body with
/// <c>400 invalid_client_metadata</c>, which makes SDK-driven registration impossible
/// against them. Netclaw registers instead and seeds
/// <see cref="ModelContextProtocol.Authentication.ClientOAuthOptions.ClientId"/>, which
/// short-circuits the SDK's registration path entirely.
/// </summary>
internal sealed class McpOAuthClientRegistrar(
    HttpClient httpClient,
    ILogger<McpOAuthClientRegistrar> logger)
{
    /// <summary>
    /// What the SDK falls back to when an authorization server advertises no
    /// <c>token_endpoint_auth_methods_supported</c>. Netclaw matches it so the
    /// registered method and the method used at the token endpoint cannot diverge.
    /// </summary>
    private const string SdkDefaultAuthMethod = "client_secret_post";

    /// <summary>
    /// Home page presented to operators on the authorization server's consent screen.
    /// RFC 7591 §2 <c>client_uri</c>.
    /// </summary>
    private const string ClientUri = "https://netclaw.dev";

    /// <summary>
    /// Netclaw logo presented on the authorization server's consent screen.
    /// RFC 7591 §2 <c>logo_uri</c>. Served as a raw asset from the public brand
    /// repo (square PNG icon) so any authorization server can fetch it without
    /// a Netclaw deployment running, and PNG keeps SVG-picky servers happy.
    /// </summary>
    private const string LogoUri = "https://raw.githubusercontent.com/netclaw-dev/netclaw-brand/dev/logo/netclaw-icon-purple.png";

    /// <summary>
    /// Registers a client for <paramref name="endpoint"/> and returns its identity.
    /// Returns <c>null</c> when the server advertises no OAuth protected-resource
    /// metadata, which is how an unauthenticated MCP server is recognized.
    /// </summary>
    public async Task<McpOAuthClientIdentity?> TryRegisterAsync(
        McpServerName serverName,
        string endpoint,
        Uri redirectUri,
        CancellationToken cancellationToken)
    {
        var resource = new Uri(endpoint);
        var authorizationServer = await TryDiscoverAuthorizationServerAsync(resource, cancellationToken);
        if (authorizationServer is null)
            return null;

        var (issuer, registrationEndpoint, supportedAuthMethods) = authorizationServer.Value;

        if (string.IsNullOrWhiteSpace(registrationEndpoint))
        {
            throw new McpOAuthRegistrationException(
                $"MCP server '{serverName.Value}' requires OAuth, but its authorization server " +
                $"({issuer}) does not support dynamic client registration. Register a client " +
                $"manually and set OAuthClientId for '{serverName.Value}' in the MCP server config.");
        }

        // Register with exactly the method ClientOAuthProvider will later select for the
        // token request (TokenEndpointAuthMethodsSupported.FirstOrDefault()). SDK 1.4.1
        // exposes no way to override that selection, so matching it here is what keeps
        // registration and token authentication in agreement.
        var authMethod = supportedAuthMethods.FirstOrDefault() ?? SdkDefaultAuthMethod;

        var request = new Dictionary<string, object>
        {
            ["client_name"] = "netclaw",
            ["client_uri"] = ClientUri,
            ["logo_uri"] = LogoUri,
            ["redirect_uris"] = new[] { redirectUri.ToString() },
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            ["response_types"] = new[] { "code" },
            ["token_endpoint_auth_method"] = authMethod,
        };

        using var response = await httpClient.PostAsJsonAsync(registrationEndpoint, request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Only the endpoint, the status, and the RFC 7591 error fields are reported.
            // The raw body is deliberately not carried anywhere: this exception is logged,
            // and daemon logs are OTLP-exported when telemetry is enabled, so an arbitrary
            // provider blob would leave the machine. DescribeOAuthError allowlists the two
            // standard fields, which is the part an operator can act on.
            throw new McpOAuthRegistrationException(
                $"MCP server '{serverName.Value}' rejected dynamic client registration at " +
                $"{registrationEndpoint}: HTTP {(int)response.StatusCode} {response.StatusCode}" +
                $"{DescribeOAuthError(body)}. Register a client manually and set OAuthClientId " +
                $"for '{serverName.Value}' in the MCP server config.");
        }

        string? clientId;
        string? clientSecret;
        try
        {
            using var document = JsonDocument.Parse(body);
            clientId = document.RootElement.TryGetProperty("client_id", out var idProperty)
                ? idProperty.GetString()
                : null;
            clientSecret = document.RootElement.TryGetProperty("client_secret", out var secretProperty)
                ? secretProperty.GetString()
                : null;
        }
        catch (JsonException ex)
        {
            throw new McpOAuthRegistrationException(
                $"MCP server '{serverName.Value}' returned an unreadable client registration response.", ex);
        }

        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new McpOAuthRegistrationException(
                $"MCP server '{serverName.Value}' returned a client registration response without a client_id.");
        }

        logger.LogInformation(
            "Registered OAuth client for MCP server '{Name}' with {AuthMethod} at {Issuer}",
            serverName.Value,
            authMethod,
            issuer);

        return new McpOAuthClientIdentity(clientId, clientSecret, DynamicClientRegistration: true);
    }

    private async Task<(string Issuer, string? RegistrationEndpoint, IReadOnlyList<string> AuthMethods)?>
        TryDiscoverAuthorizationServerAsync(Uri resource, CancellationToken cancellationToken)
    {
        var origin = resource.GetLeftPart(UriPartial.Authority);
        var path = resource.AbsolutePath.TrimEnd('/');

        // RFC 9728 inserts the well-known segment after the host and keeps the resource
        // path as a suffix. Fall back to the bare well-known path for servers that only
        // publish the origin-level document.
        var candidates = string.IsNullOrEmpty(path) || path == "/"
            ? new[] { $"{origin}/.well-known/oauth-protected-resource" }
            : [$"{origin}/.well-known/oauth-protected-resource{path}", $"{origin}/.well-known/oauth-protected-resource"];

        foreach (var candidate in candidates)
        {
            var issuer = await TryReadAuthorizationServerAsync(candidate, cancellationToken);
            if (issuer is null)
                continue;

            var metadataUrl = $"{issuer.TrimEnd('/')}/.well-known/oauth-authorization-server";
            JsonElement metadata;
            try
            {
                metadata = await httpClient.GetFromJsonAsync<JsonElement>(metadataUrl, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
            {
                throw new McpOAuthRegistrationException(
                    $"Authorization server '{issuer}' did not return usable metadata at {metadataUrl}.", ex);
            }

            var registrationEndpoint = metadata.TryGetProperty("registration_endpoint", out var registration)
                ? registration.GetString()
                : null;
            var authMethods = metadata.TryGetProperty("token_endpoint_auth_methods_supported", out var methods)
                                  && methods.ValueKind == JsonValueKind.Array
                ? methods.EnumerateArray().Select(m => m.GetString()).OfType<string>().ToArray()
                : [];

            return (issuer, registrationEndpoint, authMethods);
        }

        return null;
    }

    private async Task<string?> TryReadAuthorizationServerAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var document = await httpClient.GetFromJsonAsync<JsonElement>(url, cancellationToken);
            if (!document.TryGetProperty("authorization_servers", out var servers)
                || servers.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var server in servers.EnumerateArray())
            {
                var value = server.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            // Absent or unparseable protected-resource metadata is how an MCP server
            // without OAuth presents itself; it is not an error.
            logger.LogDebug(ex, "No OAuth protected-resource metadata at {Url}", url);
            return null;
        }
    }

    private static string DescribeOAuthError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return string.Empty;

        try
        {
            using var document = JsonDocument.Parse(body);
            var error = document.RootElement.TryGetProperty("error", out var errorProperty)
                ? errorProperty.GetString()
                : null;
            var description = document.RootElement.TryGetProperty("error_description", out var descriptionProperty)
                ? descriptionProperty.GetString()
                : null;
            return (error, description) switch
            {
                (null, null) => string.Empty,
                (not null, null) => $" ({error})",
                (null, not null) => $" ({description})",
                _ => $" ({error}: {description})",
            };
        }
        catch (JsonException)
        {
            // A non-JSON error body carries no field worth quoting back, and the raw
            // response may contain provider detail that should stay in the daemon log.
            return string.Empty;
        }
    }
}

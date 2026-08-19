// -----------------------------------------------------------------------
// <copyright file="WebhookRouteDaemonClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text.Json;
using Netclaw.Cli.Daemon;

namespace Netclaw.Cli.Webhooks;

/// <summary>Outcome of one webhook route call against the daemon.</summary>
/// <param name="Success">True when the daemon accepted the call.</param>
/// <param name="NotFound">
/// True only for a delete of a route the daemon does not hold. The availability
/// probe never sets it: a 404 there means the daemon predates the resource, which
/// is a hard failure with its own message.
/// </param>
/// <param name="Error">The message to report. It is null when the call succeeded.</param>
internal readonly record struct WebhookRouteApiResult(bool Success, bool NotFound, string? Error);

/// <summary>
/// The one write path for webhook routes. Every CLI surface that mutates a route
/// calls the daemon through this client, so the command and the config TUI cannot
/// drift into two different rules.
/// <para>
/// The rule (design D4): the daemon owns route mutations. There is no local write
/// path and no fallback. A daemon that does not answer, a daemon that does not
/// serve the resource, and a daemon that refuses the call all fail the command.
/// The supported daemon-absent path is a route file authored on disk and loaded
/// at daemon startup, not a CLI write.
/// </para>
/// </summary>
internal sealed class WebhookRouteDaemonClient
{
    /// <summary>The daemon did not answer, so no route mutation can happen.</summary>
    internal const string DaemonUnreachableMessage =
        "The daemon is not reachable. Start the daemon to manage webhook routes.";

    /// <summary>The daemon answered but predates the webhook route resource.</summary>
    internal const string DaemonMissingResourceMessage =
        "This daemon does not serve the webhook route API. Upgrade the daemon.";

    private readonly DaemonApi? _daemonApi;
    private WebhookRouteApiResult? _availability;

    /// <summary>
    /// Creates the client. <paramref name="daemonApi"/> is null when the caller
    /// holds no daemon client at all. That is the same operator-visible state as
    /// an unreachable daemon — this process cannot reach one — so the probe fails
    /// with the same message. It never selects a local write.
    /// </summary>
    public WebhookRouteDaemonClient(DaemonApi? daemonApi)
    {
        _daemonApi = daemonApi;
    }

    /// <summary>
    /// Reports whether the daemon can serve a route mutation. The probe runs once
    /// per client instance, so one CLI invocation asks one time.
    /// </summary>
    public async Task<WebhookRouteApiResult> EnsureAvailableAsync(CancellationToken ct)
    {
        if (_availability is { } cached)
            return cached;

        var availability = await ProbeAsync(ct);
        _availability = availability;
        return availability;
    }

    /// <summary>
    /// Sends one field-level route patch to the daemon. Call it only after
    /// <see cref="EnsureAvailableAsync"/> reported success.
    /// </summary>
    public async Task<WebhookRouteApiResult> UpsertAsync(
        string routeName,
        WebhookRoutePatch patch,
        CancellationToken ct)
    {
        var api = RequireDaemonApi();
        try
        {
            using var response = await api.UpsertWebhookRouteAsync(routeName, patch, ct);
            if (response.IsSuccessStatusCode)
                return new WebhookRouteApiResult(Success: true, NotFound: false, Error: null);

            return new WebhookRouteApiResult(
                Success: false,
                NotFound: false,
                Error: await DescribeFailureAsync(response, ct));
        }
        catch (Exception ex) when (IsDaemonUnreachable(ex, ct))
        {
            // The daemon died between the probe and this write. Fail with a
            // readable error that names the uncertainty: the daemon may have
            // applied the change before the connection broke.
            return new WebhookRouteApiResult(
                Success: false,
                NotFound: false,
                Error: MidFlightFailureMessage(ex));
        }
    }

    /// <summary>
    /// Deletes one route through the daemon. The probe already ran when this
    /// runs, so a 404 here means the route is missing, never an old daemon.
    /// </summary>
    public async Task<WebhookRouteApiResult> DeleteAsync(string routeName, CancellationToken ct)
    {
        var api = RequireDaemonApi();
        try
        {
            using var response = await api.DeleteWebhookRouteAsync(routeName, ct);
            if (response.IsSuccessStatusCode)
                return new WebhookRouteApiResult(Success: true, NotFound: false, Error: null);

            if (response.StatusCode is HttpStatusCode.NotFound)
                return new WebhookRouteApiResult(Success: false, NotFound: true, Error: null);

            return new WebhookRouteApiResult(
                Success: false,
                NotFound: false,
                Error: await DescribeFailureAsync(response, ct));
        }
        catch (Exception ex) when (IsDaemonUnreachable(ex, ct))
        {
            // Same rule as UpsertAsync: a mid-flight transport failure fails the
            // command with a readable error.
            return new WebhookRouteApiResult(
                Success: false,
                NotFound: false,
                Error: MidFlightFailureMessage(ex));
        }
    }

    private static string MidFlightFailureMessage(Exception ex)
        => "the daemon became unreachable while the write was in flight"
           + $" ({ex.Message}). The daemon may or may not have applied the"
           + " change. Verify with 'netclaw webhooks show' and retry.";

    private DaemonApi RequireDaemonApi()
        => _daemonApi ?? throw new InvalidOperationException(
            "The webhook route client has no daemon client. Probe availability before you call the daemon.");

    private async Task<WebhookRouteApiResult> ProbeAsync(CancellationToken ct)
    {
        if (_daemonApi is null)
            return Unavailable(DaemonUnreachableMessage);

        try
        {
            using var response = await _daemonApi.ListWebhookRoutesAsync(ct);

            // An old daemon has no route resource, so the path resolves to nothing.
            if (response.StatusCode is HttpStatusCode.NotFound)
                return Unavailable(DaemonMissingResourceMessage);

            if (response.IsSuccessStatusCode)
                return new WebhookRouteApiResult(Success: true, NotFound: false, Error: null);

            // The daemon answered and refused. It is the enforcement point, so its
            // refusal stops the command and keeps its own message.
            return Unavailable(await DescribeFailureAsync(response, ct));
        }
        catch (Exception ex) when (IsDaemonUnreachable(ex, ct))
        {
            return Unavailable(DaemonUnreachableMessage);
        }
    }

    private static WebhookRouteApiResult Unavailable(string error)
        => new(Success: false, NotFound: false, Error: error);

    /// <summary>
    /// Reports whether the failure means "no daemon answered". The client puts
    /// its request timeout on a linked token, so a timeout arrives as a
    /// cancellation that the caller's own token did not request.
    /// </summary>
    private static bool IsDaemonUnreachable(Exception ex, CancellationToken ct)
        => ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested;

    /// <summary>
    /// Reads the daemon's own message out of a failure response. The route
    /// handlers answer either <c>{"error": ...}</c> or a problem document with
    /// <c>detail</c>; an unreadable body degrades to the status code, which is
    /// still the daemon's answer.
    /// </summary>
    private static async Task<string> DescribeFailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var fallback = $"daemon returned {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd();

        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return fallback;
        }

        if (string.IsNullOrWhiteSpace(body))
            return fallback;

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind is JsonValueKind.Object)
            {
                foreach (var property in new[] { "error", "detail", "title" })
                {
                    if (document.RootElement.TryGetProperty(property, out var value)
                        && value.ValueKind is JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(value.GetString()))
                    {
                        return value.GetString()!;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Not a JSON body — an HTML error page from a proxy, for example.
            // Report the status line rather than the page text.
            return fallback;
        }

        return fallback;
    }
}

/// <summary>
/// Field-level patch for <c>PUT /api/webhooks/{name}</c>. It mirrors the
/// daemon's request body: every property is optional and a null property leaves
/// the stored value unchanged, which is what an omitted CLI flag already means.
/// The property names are the wire contract — rename one only with the daemon's
/// <c>UpsertWebhookRouteRequest</c>.
/// </summary>
internal sealed record WebhookRoutePatch
{
    public string? Prompt { get; init; }
    public string? Secret { get; init; }
    public string? VerificationKind { get; init; }
    public string? Audience { get; init; }
    public IReadOnlyList<string>? Events { get; init; }
    public string? NotifyInstructions { get; init; }
    public bool? DeliveryRequired { get; init; }
    public string? NotificationChannelId { get; init; }
    public int? MaxBodyBytes { get; init; }
    public int? RateLimitPerMinute { get; init; }
    public bool? Enabled { get; init; }
    public string? SignatureHeaderName { get; init; }
    public string? SignaturePrefix { get; init; }
    public string? SecretHeaderName { get; init; }
    public string? EventHeaderName { get; init; }
    public string? DeliveryIdHeaderName { get; init; }
    public string? TimestampField { get; init; }
    public string? SignatureField { get; init; }
    public string? SignedPayloadSeparator { get; init; }
    public int? ToleranceSeconds { get; init; }
}

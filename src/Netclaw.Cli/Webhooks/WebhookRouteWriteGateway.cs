// -----------------------------------------------------------------------
// <copyright file="WebhookRouteWriteGateway.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text.Json;
using Netclaw.Cli.Daemon;

namespace Netclaw.Cli.Webhooks;

/// <summary>Which writer puts a webhook route on disk.</summary>
internal enum WebhookRouteWriteMode
{
    /// <summary>The daemon writes the route through its route actor.</summary>
    Daemon,

    /// <summary>This process writes the route file itself.</summary>
    DirectFile
}

/// <summary>
/// Outcome of the write-path probe. A non-null <see cref="Error"/> is a hard
/// failure: the caller reports it and stops. It never selects a write mode,
/// because a fall back on a daemon error would bypass the daemon's enforcement
/// point.
/// </summary>
internal readonly record struct WebhookRouteModeResolution(WebhookRouteWriteMode Mode, string? Error)
{
    public bool Failed => Error is not null;
}

/// <summary>Outcome of one webhook route call against the daemon.</summary>
internal readonly record struct WebhookRouteApiResult(bool Success, bool NotFound, string? Error);

/// <summary>
/// The one write-path seam for webhook routes. Every CLI surface that mutates a
/// route resolves its mode here, so the command and the config TUI cannot drift
/// into two different rules.
/// <para>
/// The rule (design D4): the daemon owns route mutations when it is reachable
/// and serves the resource. Only two answers select direct-file mode — the
/// daemon does not answer at all, or an old daemon answers 404 for the resource.
/// Both print one notice on stderr, so the operator always knows which writer
/// ran. Every other failure fails the caller with the daemon's message.
/// </para>
/// </summary>
internal sealed class WebhookRouteWriteGateway
{
    /// <summary>
    /// The direct-file notice. One text covers both direct-file causes: the
    /// operator needs to know which writer ran, not which probe answer picked it.
    /// </summary>
    internal const string DirectFileNotice =
        "notice: direct-file mode. The daemon webhook route API is unavailable, so this command writes the route file directly.";

    private readonly DaemonApi? _daemonApi;
    private readonly TextWriter _noticeWriter;
    private WebhookRouteModeResolution? _resolved;

    /// <summary>
    /// Creates the gateway. <paramref name="daemonApi"/> is null when the caller
    /// has no daemon client at all — an offline invocation, which is the same
    /// disclosed direct-file state as an unreachable daemon, not a silent bypass.
    /// </summary>
    public WebhookRouteWriteGateway(DaemonApi? daemonApi, TextWriter noticeWriter)
    {
        _daemonApi = daemonApi;
        _noticeWriter = noticeWriter;
    }

    /// <summary>
    /// Resolves the write path. The probe runs once per gateway instance, so one
    /// CLI invocation picks one writer and prints at most one notice.
    /// </summary>
    public async Task<WebhookRouteModeResolution> ResolveModeAsync(CancellationToken ct)
    {
        if (_resolved is { } cached)
            return cached;

        var resolution = await ProbeAsync(ct);
        _resolved = resolution;

        if (resolution is { Mode: WebhookRouteWriteMode.DirectFile, Failed: false })
            _noticeWriter.WriteLine(DirectFileNotice);

        return resolution;
    }

    /// <summary>
    /// Sends one field-level route patch to the daemon. Call it only after
    /// <see cref="ResolveModeAsync"/> answered <see cref="WebhookRouteWriteMode.Daemon"/>.
    /// </summary>
    public async Task<WebhookRouteApiResult> UpsertAsync(
        string routeName,
        WebhookRoutePatch patch,
        CancellationToken ct)
    {
        var api = RequireDaemonApi();
        using var response = await api.UpsertWebhookRouteAsync(routeName, patch, ct);
        if (response.IsSuccessStatusCode)
            return new WebhookRouteApiResult(Success: true, NotFound: false, Error: null);

        return new WebhookRouteApiResult(
            Success: false,
            NotFound: false,
            Error: await DescribeFailureAsync(response, ct));
    }

    /// <summary>
    /// Deletes one route through the daemon. The mode is already resolved when
    /// this runs, so a 404 here means the route is missing, never an old daemon.
    /// </summary>
    public async Task<WebhookRouteApiResult> DeleteAsync(string routeName, CancellationToken ct)
    {
        var api = RequireDaemonApi();
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

    private DaemonApi RequireDaemonApi()
        => _daemonApi ?? throw new InvalidOperationException(
            "The webhook route gateway has no daemon client. Resolve the write mode before you call the daemon.");

    private async Task<WebhookRouteModeResolution> ProbeAsync(CancellationToken ct)
    {
        if (_daemonApi is null)
            return new WebhookRouteModeResolution(WebhookRouteWriteMode.DirectFile, Error: null);

        try
        {
            using var response = await _daemonApi.ListWebhookRoutesAsync(ct);

            // An old daemon has no route resource, so the path resolves to nothing.
            if (response.StatusCode is HttpStatusCode.NotFound)
                return new WebhookRouteModeResolution(WebhookRouteWriteMode.DirectFile, Error: null);

            if (response.IsSuccessStatusCode)
                return new WebhookRouteModeResolution(WebhookRouteWriteMode.Daemon, Error: null);

            // The daemon answered and refused. It is the enforcement point, so
            // its refusal stops the command instead of selecting the file path.
            return new WebhookRouteModeResolution(
                WebhookRouteWriteMode.Daemon,
                await DescribeFailureAsync(response, ct));
        }
        catch (Exception ex) when (IsDaemonUnreachable(ex, ct))
        {
            return new WebhookRouteModeResolution(WebhookRouteWriteMode.DirectFile, Error: null);
        }
    }

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
    /// still the daemon's answer and never a fall back to the file path.
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

// -----------------------------------------------------------------------
// <copyright file="DaemonApi.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Netclaw.Cli.Config;
using Netclaw.Cli.Json;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;

namespace Netclaw.Cli.Daemon;

/// <summary>
/// Single shared abstraction for all daemon REST HTTP communication.
/// Owns endpoint resolution, client creation, timeout, and deserialization.
/// Registered as a singleton in DI — every CLI command and TUI ViewModel
/// uses this instead of creating its own HttpClient + endpoint logic.
/// </summary>
public sealed class DaemonApi
{
    internal const string LocalControlHttpClientName = "Netclaw.LocalControl";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(30);

    private readonly IHttpClientFactory _factory;
    private readonly string _endpoint;
    private readonly NetclawPaths _paths;
    private readonly LocalControlPairingProofProtector _proofProtector;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Default daemon endpoint when no override is configured.
    /// </summary>
    public const string DefaultEndpoint = "http://127.0.0.1:5199";

    public DaemonApi(
        IHttpClientFactory factory,
        IConfiguration configuration,
        NetclawPaths paths,
        LocalControlPairingProofProtector proofProtector,
        TimeProvider timeProvider)
    {
        _factory = factory;
        _paths = paths;
        _proofProtector = proofProtector;
        _timeProvider = timeProvider;
        _endpoint = ResolveEndpoint(paths);
    }

    /// <summary>
    /// Resolves the daemon endpoint from environment, local client state, or default.
    /// Usable without DI for callers that don't have the CLI service provider.
    /// </summary>
    public static string ResolveEndpoint(NetclawPaths? paths = null)
    {
        paths ??= new NetclawPaths();

        return (Environment.GetEnvironmentVariable("NETCLAW_DAEMON_ENDPOINT")
            ?? ClientConfigFile.ReadEndpoint(paths)
            ?? ResolveDaemonConfigEndpoint(paths, lenient: true)
            ?? DefaultEndpoint).TrimEnd('/');
    }

    private static string ResolveDaemonConfigEndpoint(NetclawPaths paths, bool lenient)
    {
        var daemonConfig = DaemonClientFactory.LoadDaemonConfig(paths, lenient);
        return DaemonControlPlaneEndpointResolver.ResolveFallbackEndpoint(daemonConfig);
    }

    // local-control-strict-config:begin
    internal static string ResolveLocalControlEndpoint(NetclawPaths paths)
        => ResolveDaemonConfigEndpoint(paths, lenient: false);
    // local-control-strict-config:end

    internal static SocketsHttpHandler CreateLocalControlHttpHandler()
        => new()
        {
            AllowAutoRedirect = false,
            UseProxy = false,
        };

    internal static HttpClient CreateLocalControlHttpClient()
        => new(CreateLocalControlHttpHandler()) { Timeout = DefaultTimeout };

    /// <summary>
    /// The resolved daemon base endpoint (e.g. <c>http://127.0.0.1:5199</c>).
    /// Useful for display messages and SignalR hub URL construction.
    /// </summary>
    public string Endpoint => _endpoint;

    internal string LocalControlEndpoint => ResolveLocalControlEndpoint(_paths);

    internal Task<LocalControlResult<PairingCodeResultDto>> RequestPairingCodeAsync(CancellationToken ct = default)
    {
        var proof = _proofProtector.CreateProof(LocalControlOperation.GeneratePairingCode, _timeProvider.GetUtcNow());
        return PostLocalControlAsync<LocalControlPairingCodeRequest, PairingCodeResultDto>(
            "pairing-code", new LocalControlPairingCodeRequest(proof), ct);
    }

    /// <summary>
    /// Reads the tokens of a completed provider OAuth flow. The proof binds to
    /// <paramref name="state"/>. It goes only to the direct local-control endpoint,
    /// never through a proxy, a redirect, or the paired-client endpoint.
    /// </summary>
    internal Task<LocalControlResult<ProviderOAuthResultDto>> ReadProviderOAuthResultAsync(
        string state,
        CancellationToken ct = default)
    {
        var proof = _proofProtector.CreateProof(
            LocalControlOperation.ReadProviderOAuthResult(state),
            _timeProvider.GetUtcNow());
        return PostLocalControlAsync<LocalControlProviderOAuthResultRequest, ProviderOAuthResultDto>(
            "provider-oauth-result", new LocalControlProviderOAuthResultRequest(state, proof), ct);
    }

    private async Task<LocalControlResult<TResult>> PostLocalControlAsync<TRequest, TResult>(
        string operationPath,
        TRequest request,
        CancellationToken ct)
        where TResult : class
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = _factory.CreateClient(LocalControlHttpClientName);
        using var response = await client.PostAsJsonAsync(
            $"{LocalControlEndpoint}/api/local-control/v1/{operationPath}",
            request,
            JsonDefaults.Api,
            cts.Token);

        var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        if (response.IsSuccessStatusCode)
        {
            var result = await JsonSerializer.DeserializeAsync<TResult>(stream, JsonDefaults.Api, cts.Token);
            return new LocalControlResult<TResult>(response.StatusCode, result, null);
        }

        LocalControlErrorResponse? error = null;
        try
        {
            error = await JsonSerializer.DeserializeAsync<LocalControlErrorResponse>(stream, JsonDefaults.Api, cts.Token);
        }
        catch (JsonException)
        {
            error = new LocalControlErrorResponse(
                response.ReasonPhrase ?? "The daemon returned an invalid error response.");
        }

        return new LocalControlResult<TResult>(response.StatusCode, null, error?.Error);
    }

    // ── Status ────────────────────────────────────────────────────────

    public async Task<DaemonRuntimeStatus.Response?> GetStatusAsync(CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = CreateHttpClient();
        using var response = await client.GetAsync($"{_endpoint}/api/health/status", cts.Token);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        return await JsonSerializer.DeserializeAsync<DaemonRuntimeStatus.Response>(stream, JsonDefaults.Api, cts.Token);
    }

    // ── Sessions ──────────────────────────────────────────────────────

    public async Task<List<SessionCatalogEntryDto>> ListSessionsAsync(
        int? limit = null,
        int? offset = null,
        CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = CreateHttpClient();
        var url = $"{_endpoint}/api/sessions";
        if (limit.HasValue || offset.HasValue)
        {
            var separator = "?";
            if (limit.HasValue)
            {
                url += $"{separator}limit={limit.Value}";
                separator = "&";
            }

            if (offset.HasValue)
                url += $"{separator}offset={offset.Value}";
        }

        using var response = await client.GetAsync(url, cts.Token);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        return await JsonSerializer.DeserializeAsync<List<SessionCatalogEntryDto>>(stream, JsonDefaults.Api, cts.Token) ?? [];
    }

    // ── Stats ─────────────────────────────────────────────────────────

    public async Task<DaemonStats.Response?> GetStatsAsync(int? days = null, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var url = $"{_endpoint}/api/stats";
        if (days.HasValue)
            url += $"?days={days.Value}";
        var client = CreateHttpClient();
        using var response = await client.GetAsync(url, cts.Token);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        return await JsonSerializer.DeserializeAsync<DaemonStats.Response>(stream, JsonDefaults.Api, cts.Token);
    }

    public async Task<SkillUsageStats.Response?> GetSkillUsageStatsAsync(int? days = null, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var url = $"{_endpoint}/api/stats/skills";
        if (days.HasValue)
            url += $"?days={days.Value}";
        var client = CreateHttpClient();
        using var response = await client.GetAsync(url, cts.Token);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        return await JsonSerializer.DeserializeAsync<SkillUsageStats.Response>(stream, JsonDefaults.Api, cts.Token);
    }

    // ── Skills ────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches the daemon's live skill inventory — file skills plus dynamic MCP
    /// prompt skills a disk scan cannot see. Throws <see cref="HttpRequestException"/>
    /// (or a timeout) when the daemon is unreachable; callers report the daemon as
    /// unavailable rather than degrading to a disk scan.
    /// </summary>
    public async Task<SkillInventory.Response?> GetSkillsAsync(CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = CreateHttpClient();
        using var response = await client.GetAsync($"{_endpoint}/api/skills", cts.Token);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        return await JsonSerializer.DeserializeAsync<SkillInventory.Response>(stream, JsonDefaults.Api, cts.Token);
    }

    /// <summary>
    /// Fetches one skill's detail from the daemon's live registry. Returns null when
    /// the daemon does not know the name. Throws <see cref="HttpRequestException"/>
    /// (or a timeout) when the daemon is unreachable; callers report that rather than
    /// degrading to a disk scan.
    /// </summary>
    public async Task<SkillInventory.Detail?> GetSkillAsync(string name, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = CreateHttpClient();
        using var response = await client.GetAsync(
            $"{_endpoint}/api/skills/{Uri.EscapeDataString(name)}", cts.Token);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // Two different failures share 404. A daemon that predates this route
            // returns an empty 404; the route itself answers an unknown name with a
            // problem body. Collapsing both into null would report a missing route as
            // "skill not found" — the exact false negative this endpoint exists to fix.
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            if (string.IsNullOrWhiteSpace(body))
                throw new HttpRequestException(
                    "the daemon does not serve /api/skills/{name} yet",
                    inner: null,
                    response.StatusCode);

            return null;
        }

        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        return await JsonSerializer.DeserializeAsync<SkillInventory.Detail>(stream, JsonDefaults.Api, cts.Token);
    }

    /// <summary>
    /// Runs the daemon-owned skill sync pass. This request has no client timeout.
    /// A source pass can exceed normal status request limits. The caller controls
    /// only its wait through <paramref name="ct"/>.
    /// </summary>
    public async Task<SkillSyncResult.Response?> SyncSkillsAsync(CancellationToken ct = default)
    {
        var client = CreateHttpClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}/api/skills/sync");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<SkillSyncResult.Response>(stream, JsonDefaults.Api, ct);
    }

    // ── Reminders ─────────────────────────────────────────────────────

    public async Task<HttpResponseMessage> ListRemindersAsync(CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = CreateHttpClient();
        return await client.GetAsync($"{_endpoint}/api/reminders", cts.Token);
    }

    public async Task<HttpResponseMessage> CreateReminderAsync(object request, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = CreateHttpClient();
        return await client.PostAsJsonAsync($"{_endpoint}/api/reminders", request, cts.Token);
    }

    public async Task<HttpResponseMessage> DeleteReminderAsync(string id, bool permanent = false, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = CreateHttpClient();
        var query = permanent ? "?permanent=true" : "";
        return await client.DeleteAsync($"{_endpoint}/api/reminders/{id}{query}", cts.Token);
    }

    public async Task<HttpResponseMessage> GetReminderAsync(string id, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = CreateHttpClient();
        return await client.GetAsync($"{_endpoint}/api/reminders/{id}", cts.Token);
    }

    public async Task<HttpResponseMessage> GetReminderHistoryAsync(string id, int last = 20, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = CreateHttpClient();
        return await client.GetAsync($"{_endpoint}/api/reminders/{id}/history?last={last}", cts.Token);
    }

    public async Task<HttpResponseMessage> GetReminderStatusAsync(string id, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = CreateHttpClient();
        return await client.GetAsync($"{_endpoint}/api/reminders/{id}/status", cts.Token);
    }

    public async Task<HttpResponseMessage> EnableReminderAsync(string id, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = CreateHttpClient();
        return await client.PostAsync($"{_endpoint}/api/reminders/{id}/enable", content: null, cts.Token);
    }

    public async Task<HttpResponseMessage> DisableReminderAsync(string id, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = CreateHttpClient();
        return await client.PostAsync($"{_endpoint}/api/reminders/{id}/disable", content: null, cts.Token);
    }

    public async Task<HttpResponseMessage> ValidateReminderAsync(object request, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = CreateHttpClient();
        return await client.PostAsJsonAsync($"{_endpoint}/api/reminders/validate", request, cts.Token);
    }

    public async Task<HttpResponseMessage> ImportReminderAsync(object request, JsonSerializerOptions? options = null, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = CreateHttpClient();
        return options is not null
            ? await client.PostAsJsonAsync($"{_endpoint}/api/reminders/import", request, options, cts.Token)
            : await client.PostAsJsonAsync($"{_endpoint}/api/reminders/import", request, cts.Token);
    }

    // ── Webhook routes ────────────────────────────────────────────────

    /// <summary>
    /// Lists the daemon's webhook routes. The CLI also uses this call as its
    /// write availability probe: a transport failure means the daemon is down,
    /// and a 404 means the daemon predates the resource. Every answer other than
    /// success fails the mutation; there is no local write path.
    /// </summary>
    public async Task<HttpResponseMessage> ListWebhookRoutesAsync(CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = CreateHttpClient();
        return await client.GetAsync($"{_endpoint}/api/webhooks", cts.Token);
    }

    /// <summary>
    /// Creates or updates one webhook route. <paramref name="request"/> is a
    /// field-level patch: an omitted (null) property leaves the stored value
    /// unchanged, so two patches of different fields compose in the daemon.
    /// </summary>
    public async Task<HttpResponseMessage> UpsertWebhookRouteAsync(
        string name,
        object request,
        CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = CreateHttpClient();
        return await client.PutAsJsonAsync($"{_endpoint}/api/webhooks/{Uri.EscapeDataString(name)}", request, cts.Token);
    }

    public async Task<HttpResponseMessage> DeleteWebhookRouteAsync(string name, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = CreateHttpClient();
        return await client.DeleteAsync($"{_endpoint}/api/webhooks/{Uri.EscapeDataString(name)}", cts.Token);
    }

    // ── MCP OAuth ─────────────────────────────────────────────────────

    public async Task<HttpResponseMessage> StartMcpOAuthAsync(string name, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(LongTimeout, ct);
        var client = CreateHttpClient();
        return await client.PostAsync($"{_endpoint}/api/mcp/oauth/start/{Uri.EscapeDataString(name)}", null, cts.Token);
    }

    public async Task<JsonElement> GetMcpOAuthStatusAsync(string name, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = CreateHttpClient();
        return await client.GetFromJsonAsync<JsonElement>(
            $"{_endpoint}/api/mcp/oauth/status/{Uri.EscapeDataString(name)}", cts.Token);
    }

    public async Task<JsonElement> GetMcpOAuthStatusByStateAsync(string state, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = CreateHttpClient();
        return await client.GetFromJsonAsync<JsonElement>(
            $"{_endpoint}/api/mcp/oauth/status-by-state/{Uri.EscapeDataString(state)}", cts.Token);
    }

    public async Task<HttpResponseMessage> McpOAuthCallbackAsync(
        string code,
        string state,
        string? iss,
        CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(LongTimeout, ct);
        var client = CreateHttpClient();
        // The MCP SDK validates iss per RFC 9207 and rejects the response when an advertising
        // server sent one and it does not arrive, so the paste path must carry it too.
        var query = $"code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}";
        if (!string.IsNullOrEmpty(iss))
            query += $"&iss={Uri.EscapeDataString(iss)}";
        return await client.GetAsync($"{_endpoint}/api/mcp/oauth/callback?{query}", cts.Token);
    }

    public async Task<JsonElement> GetMcpServerStatusesAsync(CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = CreateHttpClient();
        using var response = await client.GetAsync($"{_endpoint}/api/mcp/statuses", cts.Token);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        return await JsonSerializer.DeserializeAsync<JsonElement>(stream, JsonDefaults.Api, cts.Token);
    }

    public async Task<List<string>> GetMcpToolNamesAsync(string serverName, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = CreateHttpClient();
        return await client.GetFromJsonAsync<List<string>>(
            $"{_endpoint}/api/mcp/tools/{Uri.EscapeDataString(serverName)}", cts.Token) ?? [];
    }

    // ── Provider OAuth ─────────────────────────────────────────────────

    public async Task<HttpResponseMessage> StartProviderOAuthAsync(string providerType, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(LongTimeout, ct);
        var client = CreateHttpClient();
        return await client.PostAsync(
            $"{_endpoint}/api/provider/oauth/start?provider={Uri.EscapeDataString(providerType)}", null, cts.Token);
    }

    public async Task<JsonElement> GetProviderOAuthStatusAsync(string state, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = CreateHttpClient();
        return await client.GetFromJsonAsync<JsonElement>(
            $"{_endpoint}/api/provider/oauth/status/{Uri.EscapeDataString(state)}", cts.Token);
    }

    public async Task<HttpResponseMessage> ProviderOAuthCallbackAsync(string code, string state, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(LongTimeout, ct);
        var client = CreateHttpClient();
        return await client.GetAsync(
            $"{_endpoint}/api/provider/oauth/callback?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}", cts.Token);
    }

    // ── Device pairing ────────────────────────────────────────────────

    public async Task<List<PairedDeviceInfoDto>> ListPairedDevicesAsync(CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = CreateHttpClient();
        using var response = await client.GetAsync($"{_endpoint}/api/pair/devices", cts.Token);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        return await JsonSerializer.DeserializeAsync<List<PairedDeviceInfoDto>>(stream, JsonDefaults.Api, cts.Token) ?? [];
    }

    /// <summary>
    /// Revokes a paired device by name.
    /// Returns <c>true</c> if removed, <c>false</c> if not found.
    /// </summary>
    public async Task<bool> RevokePairedDeviceAsync(string name, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = CreateHttpClient();
        using var response = await client.DeleteAsync(
            $"{_endpoint}/api/pair/devices/{Uri.EscapeDataString(name)}", cts.Token);
        return response.StatusCode is HttpStatusCode.NoContent;
    }

    // ── Health (for init wizard polling) ──────────────────────────────

    /// <summary>
    /// Outcome of probing <c>/api/health/ready</c>: whether the daemon answered healthy,
    /// and the monotonic restart <see cref="Generation"/> it reported (null when the
    /// daemon predates the header or the probe failed).
    /// </summary>
    public readonly record struct DaemonReadiness(bool Healthy, int? Generation);

    /// <summary>
    /// Probes the daemon's anonymous readiness endpoint, returning both health and the
    /// reported restart generation (<c>X-Netclaw-Generation</c>).
    /// </summary>
    /// <remarks>
    /// The endpoint is re-resolved on every probe rather than reusing the value captured
    /// at construction (#1304): the init wizard writes config and waits for an in-process
    /// restart, and if that change altered <c>Daemon.Port</c> the daemon comes back on the
    /// new port while a frozen endpoint would keep polling the dead one. Re-resolution
    /// reads the just-written <c>Daemon</c> section (and still honors an explicit
    /// <c>NETCLAW_DAEMON_ENDPOINT</c> / paired client endpoint when one is set).
    /// </remarks>
    public Task<DaemonReadiness> ProbeReadinessAsync(CancellationToken ct = default)
        => ProbeReadinessAsync(ResolveEndpoint(_paths), ct);

    /// <summary>
    /// Probes this home's daemon with an anonymous GET of <c>/api/health/ready</c>.
    /// Local configuration supplies the address. Remote client endpoints do not affect the probe.
    /// A failed address resolution returns no endpoint and creates no transport.
    /// Other failures report not ready. Caller cancellation propagates.
    /// </summary>
    internal static Task<(bool Ready, string? Endpoint)> ProbeLocalReadinessAsync(
        NetclawPaths paths, CancellationToken ct = default)
        => ProbeLocalReadinessAsync(paths, CreateLocalControlHttpClient, ct);

    internal static async Task<(bool Ready, string? Endpoint)> ProbeLocalReadinessAsync(
        NetclawPaths paths, Func<HttpClient> createLocalControlHttpClient, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(createLocalControlHttpClient);
        ct.ThrowIfCancellationRequested();
        string? endpoint = null;
        try
        {
            endpoint = ResolveLocalControlEndpoint(paths);
            using var http = createLocalControlHttpClient();
            using var response = await http.GetAsync($"{endpoint}/api/health/ready", ct);
            return (response.IsSuccessStatusCode, endpoint);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();
            return (false, endpoint);
        }
    }

    private async Task<DaemonReadiness> ProbeReadinessAsync(string endpoint, CancellationToken ct)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = CreateHttpClient();
        using var response = await client.GetAsync($"{endpoint}/api/health/ready", cts.Token);
        if (!response.IsSuccessStatusCode)
            return new DaemonReadiness(false, null);

        int? generation = null;
        if (response.Headers.TryGetValues("X-Netclaw-Generation", out var values)
            && int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            generation = parsed;

        return new DaemonReadiness(true, generation);
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static CancellationTokenSource CreateTimeoutCts(TimeSpan timeout, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return cts;
    }

    private HttpClient CreateHttpClient()
    {
        var client = _factory.CreateClient();
        // Config TUI can switch exposure mode and write the bootstrap token while this singleton is alive.
        var deviceToken = DaemonClientFactory.ResolveDeviceToken(
            _endpoint,
            _paths,
            DaemonClientFactory.ResolveExposureMode(_paths));
        if (!string.IsNullOrWhiteSpace(deviceToken))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);

        return client;
    }
}

internal sealed record LocalControlPairingCodeRequest(string Proof);

internal sealed record LocalControlProviderOAuthResultRequest(string State, string Proof);

internal sealed record ProviderOAuthResultDto(
    string AccessToken,
    string? RefreshToken,
    string? AccountId,
    DateTimeOffset? ExpiresAt);

internal sealed record LocalControlErrorResponse(string Error);

internal sealed record LocalControlResult<T>(
    HttpStatusCode StatusCode,
    T? Result,
    string? Error)
    where T : class;

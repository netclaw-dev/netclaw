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

namespace Netclaw.Cli.Daemon;

/// <summary>
/// Single shared abstraction for all daemon REST HTTP communication.
/// Owns endpoint resolution, client creation, timeout, and deserialization.
/// Registered as a singleton in DI — every CLI command and TUI ViewModel
/// uses this instead of creating its own HttpClient + endpoint logic.
/// </summary>
public sealed class DaemonApi
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(30);

    private readonly IHttpClientFactory _factory;
    private readonly string _endpoint;
    private readonly NetclawPaths _paths;

    /// <summary>
    /// Default daemon endpoint when no override is configured.
    /// </summary>
    public const string DefaultEndpoint = "http://127.0.0.1:5199";

    public DaemonApi(IHttpClientFactory factory, IConfiguration configuration, NetclawPaths paths)
    {
        _factory = factory;
        _paths = paths;
        _endpoint = ResolveEndpoint(paths);
    }

    internal DaemonApi(IHttpClientFactory factory, IConfiguration configuration)
        : this(factory, configuration, new NetclawPaths())
    {
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
            ?? ResolveDaemonConfigEndpoint(paths)
            ?? DefaultEndpoint).TrimEnd('/');
    }

    private static string? ResolveDaemonConfigEndpoint(NetclawPaths paths)
    {
        var daemonConfig = DaemonClientFactory.LoadDaemonConfig(paths);
        return DaemonControlPlaneEndpointResolver.ResolveFallbackEndpoint(daemonConfig);
    }

    /// <summary>
    /// The resolved daemon base endpoint (e.g. <c>http://127.0.0.1:5199</c>).
    /// Useful for display messages and SignalR hub URL construction.
    /// </summary>
    public string Endpoint => _endpoint;

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

    public async Task<HttpResponseMessage> RunReminderAsync(string id, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = CreateHttpClient();
        return await client.PostAsync($"{_endpoint}/api/reminders/{Uri.EscapeDataString(id)}/run", content: null, cts.Token);
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

    public async Task<HttpResponseMessage> McpOAuthCallbackAsync(string code, string state, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(LongTimeout, ct);
        var client = CreateHttpClient();
        return await client.GetAsync(
            $"{_endpoint}/api/mcp/oauth/callback?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}", cts.Token);
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
    public async Task<DaemonReadiness> ProbeReadinessAsync(CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = CreateHttpClient();
        var endpoint = ResolveEndpoint(_paths);
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

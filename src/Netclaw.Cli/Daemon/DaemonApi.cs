using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
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
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(30);

    private readonly IHttpClientFactory _factory;
    private readonly string _endpoint;

    public DaemonApi(IHttpClientFactory factory, IConfiguration configuration)
    {
        _factory = factory;
        _endpoint = (configuration["Daemon:Endpoint"]
            ?? Environment.GetEnvironmentVariable("NETCLAW_DAEMON_ENDPOINT")
            ?? "http://127.0.0.1:5199").TrimEnd('/');
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
        var client = _factory.CreateClient();
        using var response = await client.GetAsync($"{_endpoint}/api/health/status", cts.Token);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        return await JsonSerializer.DeserializeAsync<DaemonRuntimeStatus.Response>(stream, WebJsonOptions, cts.Token);
    }

    // ── Sessions ──────────────────────────────────────────────────────

    public async Task<List<SessionCatalogEntryDto>> ListSessionsAsync(CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = _factory.CreateClient();
        using var response = await client.GetAsync($"{_endpoint}/api/sessions", cts.Token);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        return await JsonSerializer.DeserializeAsync<List<SessionCatalogEntryDto>>(stream, WebJsonOptions, cts.Token) ?? [];
    }

    // ── Stats ─────────────────────────────────────────────────────────

    public async Task<DaemonStats.Response?> GetStatsAsync(int? days = null, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var url = $"{_endpoint}/api/stats";
        if (days.HasValue)
            url += $"?days={days.Value}";
        var client = _factory.CreateClient();
        using var response = await client.GetAsync(url, cts.Token);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        return await JsonSerializer.DeserializeAsync<DaemonStats.Response>(stream, WebJsonOptions, cts.Token);
    }

    // ── Reminders ─────────────────────────────────────────────────────

    public async Task<HttpResponseMessage> ListRemindersAsync(CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = _factory.CreateClient();
        return await client.GetAsync($"{_endpoint}/api/reminders", cts.Token);
    }

    public async Task<HttpResponseMessage> CreateReminderAsync(object request, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = _factory.CreateClient();
        return await client.PostAsJsonAsync($"{_endpoint}/api/reminders", request, cts.Token);
    }

    public async Task<HttpResponseMessage> DeleteReminderAsync(string id, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = _factory.CreateClient();
        return await client.DeleteAsync($"{_endpoint}/api/reminders/{id}", cts.Token);
    }

    public async Task<HttpResponseMessage> GetReminderAsync(string id, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = _factory.CreateClient();
        return await client.GetAsync($"{_endpoint}/api/reminders/{id}", cts.Token);
    }

    public async Task<HttpResponseMessage> GetReminderHistoryAsync(string id, int last = 20, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = _factory.CreateClient();
        return await client.GetAsync($"{_endpoint}/api/reminders/{id}/history?last={last}", cts.Token);
    }

    public async Task<HttpResponseMessage> EnableReminderAsync(string id, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = _factory.CreateClient();
        return await client.PostAsync($"{_endpoint}/api/reminders/{id}/enable", content: null, cts.Token);
    }

    public async Task<HttpResponseMessage> DisableReminderAsync(string id, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = _factory.CreateClient();
        return await client.PostAsync($"{_endpoint}/api/reminders/{id}/disable", content: null, cts.Token);
    }

    public async Task<HttpResponseMessage> ValidateReminderAsync(object request, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = _factory.CreateClient();
        return await client.PostAsJsonAsync($"{_endpoint}/api/reminders/validate", request, cts.Token);
    }

    public async Task<HttpResponseMessage> ImportReminderAsync(object request, JsonSerializerOptions? options = null, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = _factory.CreateClient();
        return options is not null
            ? await client.PostAsJsonAsync($"{_endpoint}/api/reminders/import", request, options, cts.Token)
            : await client.PostAsJsonAsync($"{_endpoint}/api/reminders/import", request, cts.Token);
    }

    // ── MCP OAuth ─────────────────────────────────────────────────────

    public async Task<HttpResponseMessage> StartMcpOAuthAsync(string name, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(LongTimeout, ct);
        var client = _factory.CreateClient();
        return await client.PostAsync($"{_endpoint}/api/mcp/oauth/start/{Uri.EscapeDataString(name)}", null, cts.Token);
    }

    public async Task<JsonElement> GetMcpOAuthStatusAsync(string name, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = _factory.CreateClient();
        return await client.GetFromJsonAsync<JsonElement>(
            $"{_endpoint}/api/mcp/oauth/status/{Uri.EscapeDataString(name)}", cts.Token);
    }

    public async Task<JsonElement> GetMcpOAuthStatusByStateAsync(string state, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = _factory.CreateClient();
        return await client.GetFromJsonAsync<JsonElement>(
            $"{_endpoint}/api/mcp/oauth/status-by-state/{Uri.EscapeDataString(state)}", cts.Token);
    }

    public async Task<HttpResponseMessage> McpOAuthCallbackAsync(string code, string state, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(LongTimeout, ct);
        var client = _factory.CreateClient();
        return await client.GetAsync(
            $"{_endpoint}/api/mcp/oauth/callback?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}", cts.Token);
    }

    public async Task<JsonElement?> GetMcpServerStatusesAsync(CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = _factory.CreateClient();
        try
        {
            return await client.GetFromJsonAsync<JsonElement>(
                $"{_endpoint}/api/mcp/statuses", cts.Token);
        }
        catch
        {
            return null;
        }
    }

    // ── Provider OAuth ─────────────────────────────────────────────────

    public async Task<HttpResponseMessage> StartProviderOAuthAsync(string providerType, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(LongTimeout, ct);
        var client = _factory.CreateClient();
        return await client.PostAsync(
            $"{_endpoint}/api/provider/oauth/start?provider={Uri.EscapeDataString(providerType)}", null, cts.Token);
    }

    public async Task<JsonElement> GetProviderOAuthStatusAsync(string state, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = _factory.CreateClient();
        return await client.GetFromJsonAsync<JsonElement>(
            $"{_endpoint}/api/provider/oauth/status/{Uri.EscapeDataString(state)}", cts.Token);
    }

    public async Task<HttpResponseMessage> ProviderOAuthCallbackAsync(string code, string state, CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(LongTimeout, ct);
        var client = _factory.CreateClient();
        return await client.GetAsync(
            $"{_endpoint}/api/provider/oauth/callback?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}", cts.Token);
    }

    // ── Health (for init wizard polling) ──────────────────────────────

    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        using var cts = CreateTimeoutCts(DefaultTimeout, ct);
        var client = _factory.CreateClient();
        var response = await client.GetAsync($"{_endpoint}/api/health/ready", cts.Token);
        return response.IsSuccessStatusCode;
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static CancellationTokenSource CreateTimeoutCts(TimeSpan timeout, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return cts;
    }
}

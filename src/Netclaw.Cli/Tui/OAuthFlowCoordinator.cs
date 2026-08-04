// -----------------------------------------------------------------------
// <copyright file="OAuthFlowCoordinator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Text.Json;
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Netclaw.Providers;
using Netclaw.Providers.GitHubCopilot;
using Netclaw.Providers.OAuth;
using Netclaw.Tools;
using R3;

namespace Netclaw.Cli.Tui;

/// <summary>
/// Encapsulates OAuth flow state and orchestration logic shared between
/// <see cref="InitWizardViewModel"/> and <see cref="ProviderManagerViewModel"/>.
/// Both ViewModels own an instance and delegate OAuth operations to it
/// instead of duplicating the browser PKCE flow, device flow, and
/// paste-redirect fallback methods.
/// </summary>
public sealed class OAuthFlowCoordinator : IDisposable
{
    private readonly ProviderDescriptorRegistry _registry;
    private readonly DeviceFlowServiceFactory? _deviceFlowFactory;
    private readonly DaemonApi? _daemonApi;
    private readonly Action _requestRedraw;
    private CancellationTokenSource? _cts;

    // Set during flow start to route SubmitRedirectUrlAsync to the correct callback
    private Func<string, string, string?, Task<HttpResponseMessage>>? _activeCallbackFunc;

    // ── Observable state ──────────────────────────────────────────────

    public ReactiveProperty<DeviceFlowState> FlowState { get; } = new(DeviceFlowState.NotStarted);
    public string? UserCode { get; set; }
    public string? VerificationUri { get; set; }

    /// <summary>
    /// RFC 8628 §3.3.1 verification URI with the user code pre-filled.
    /// When the provider returns this (GitHub does), it is the URL we display
    /// because a single Cmd/Ctrl-click completes auth without retyping the code.
    /// Null when the provider only returned <see cref="VerificationUri"/>.
    /// </summary>
    public string? VerificationUriComplete { get; set; }
    public string? ErrorMessage { get; set; }
    public bool BrowserOpenFailed { get; set; }
    internal OAuthDeviceFlowResult? Result { get; set; }

    /// <summary>
    /// Completes when the current flow finishes (success or failure). Used for testing.
    /// </summary>
    internal Task? Completion { get; private set; }

    public OAuthFlowCoordinator(
        ProviderDescriptorRegistry registry,
        DeviceFlowServiceFactory? deviceFlowFactory,
        DaemonApi? daemonApi = null,
        Action? requestRedraw = null)
    {
        _registry = registry;
        _deviceFlowFactory = deviceFlowFactory;
        _daemonApi = daemonApi;
        _requestRedraw = requestRedraw ?? (() => { });
    }

    /// <summary>
    /// Start browser-based Authorization Code + PKCE flow for a provider.
    /// Returns a <see cref="CancellationToken"/> that fires when the flow ends —
    /// callers use it to drive UI timers (spinner, elapsed seconds).
    /// </summary>
    public CancellationToken StartBrowserFlow(
        string providerType, Action<OAuthDeviceFlowResult>? onSuccess = null)
    {
        Cancel();
        _cts = new CancellationTokenSource();
        _activeCallbackFunc = _daemonApi is not null
            ? (code, state, _) => _daemonApi.ProviderOAuthCallbackAsync(code, state)
            : null;
        Completion = RunBrowserFlowAsync(providerType, onSuccess, _cts.Token);
        return _cts.Token;
    }

    /// <summary>
    /// Start browser-based Authorization Code + PKCE flow for an MCP server.
    /// Returns a <see cref="CancellationToken"/> that fires when the flow ends.
    /// </summary>
    public CancellationToken StartMcpBrowserFlow(
        McpServerName serverName, Action? onSuccess = null)
    {
        Cancel();
        _cts = new CancellationTokenSource();
        _activeCallbackFunc = _daemonApi is not null
            ? (code, state, iss) => _daemonApi.McpOAuthCallbackAsync(code, state, iss)
            : null;
        Completion = RunMcpBrowserFlowAsync(serverName, onSuccess, _cts.Token);
        return _cts.Token;
    }

    /// <summary>
    /// Start OAuth device authorization flow (RFC 8628).
    /// Returns a <see cref="CancellationToken"/> that fires when the flow ends.
    /// </summary>
    public CancellationToken StartDeviceFlow(
        string providerType,
        Action<OAuthDeviceFlowResult>? onSuccess = null,
        ProviderEntry? entry = null)
    {
        Cancel();
        _cts = new CancellationTokenSource();
        Completion = RunDeviceFlowAsync(providerType, onSuccess, entry, _cts.Token);
        return _cts.Token;
    }

    /// <summary>
    /// Handle a pasted redirect URL for the browser OAuth fallback path.
    /// Parses the URL, sends the extracted code+state to the daemon callback endpoint.
    /// </summary>
    public async Task SubmitRedirectUrlAsync(string? pastedUrl)
    {
        if (!OAuthRedirectParser.TryParse(pastedUrl, out var code, out var state, out var iss, out var error))
        {
            ErrorMessage = error;
            _requestRedraw();
            return;
        }

        if (_activeCallbackFunc is null)
        {
            ErrorMessage = "Daemon API not available.";
            _requestRedraw();
            return;
        }

        try
        {
            var response = await _activeCallbackFunc(code, state, iss);

            if (response.IsSuccessStatusCode)
            {
                // Don't set FlowState = Succeeded here. The poll loop in
                // RunBrowserFlowAsync will see "Completed" on the next poll
                // (within ~2s) and properly set Result + FlowState + invoke
                // the onSuccess callback with the token. Setting Succeeded
                // here triggers UI subscriptions before the token is available.
                _requestRedraw();
            }
            else
            {
                ErrorMessage = "Token exchange failed. The authorization code may be expired.";
                _requestRedraw();
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to exchange code: {ex.Message}";
            _requestRedraw();
        }
    }

    public void Cancel()
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// Reset all state to initial values. Cancels any in-progress flow.
    /// </summary>
    public void Reset()
    {
        Cancel();
        FlowState.Value = DeviceFlowState.NotStarted;
        UserCode = null;
        VerificationUri = null;
        VerificationUriComplete = null;
        ErrorMessage = null;
        BrowserOpenFailed = false;
        Result = null;
    }

    public void Dispose()
    {
        Cancel();
        FlowState.Dispose();
    }

    // ── Browser Authorization Code + PKCE flow ───────────────────────

    private Task RunBrowserFlowAsync(
        string providerType, Action<OAuthDeviceFlowResult>? onSuccess, CancellationToken ct)
    {
        return RunBrowserFlowCoreAsync(
            startFlow: async token => await _daemonApi!.StartProviderOAuthAsync(providerType, token),
            pollStatus: (state, token) => _daemonApi!.GetProviderOAuthStatusAsync(state, token),
            parseResult: statusResponse =>
            {
                var accessToken = statusResponse.TryGetProperty("accessToken", out var atProp)
                    ? atProp.GetString() : null;
                var refreshToken = statusResponse.TryGetProperty("refreshToken", out var rtProp)
                    ? rtProp.GetString() : null;
                var accountId = statusResponse.TryGetProperty("accountId", out var accountIdProp)
                    ? accountIdProp.GetString() : null;
                var expiresAt = statusResponse.TryGetProperty("expiresAt", out var expProp)
                    ? expProp.GetString() : null;

                if (!string.IsNullOrEmpty(accessToken))
                {
                    return new OAuthDeviceFlowResult(
                        new SensitiveString(accessToken),
                        refreshToken is not null ? new SensitiveString(refreshToken) : null,
                        expiresAt is not null ? DateTimeOffset.Parse(expiresAt) : null,
                        accountId is not null ? new SensitiveString(accountId) : null);
                }

                return null;
            },
            onSuccess: result =>
            {
                if (result is null)
                    throw new InvalidOperationException("Provider OAuth completed without token payload.");

                onSuccess?.Invoke(result);
            },
            ct);
    }

    private Task RunMcpBrowserFlowAsync(
        McpServerName serverName, Action? onSuccess, CancellationToken ct)
    {
        return RunBrowserFlowCoreAsync(
            startFlow: async token => await _daemonApi!.StartMcpOAuthAsync(serverName.Value, token),
            pollStatus: (state, token) => _daemonApi!.GetMcpOAuthStatusByStateAsync(state, token),
            parseResult: _ => null, // MCP tokens are persisted daemon-side
            onSuccess: _ => onSuccess?.Invoke(),
            ct);
    }

    /// <summary>
    /// Shared browser PKCE flow: start → open browser → poll for completion.
    /// Parameterized by delegates so provider and MCP flows share the same logic.
    /// </summary>
    private async Task RunBrowserFlowCoreAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> startFlow,
        Func<string, CancellationToken, Task<JsonElement>> pollStatus,
        Func<JsonElement, OAuthDeviceFlowResult?> parseResult,
        Action<OAuthDeviceFlowResult?>? onSuccess,
        CancellationToken ct)
    {
        if (_daemonApi is null)
        {
            ErrorMessage = "Daemon API not available.";
            FlowState.Value = DeviceFlowState.Error;
            _requestRedraw();
            return;
        }

        try
        {
            // Step 1: Ask daemon to start the OAuth flow
            var startResponse = await startFlow(ct);

            if (!startResponse.IsSuccessStatusCode)
            {
                var errorBody = await startResponse.Content.ReadAsStringAsync(ct);
                ErrorMessage = $"Failed to start OAuth flow: {errorBody}";
                FlowState.Value = DeviceFlowState.Error;
                _requestRedraw();
                return;
            }

            var startResult = await startResponse.Content.ReadFromJsonAsync<JsonElement>(ct);
            var authUrl = startResult.GetProperty("authorizationUrl").GetString()!;
            var flowState = startResult.GetProperty("state").GetString()!;
            // The daemon owns the flow deadline; polling past it, or stopping before it,
            // both misreport the outcome to the operator. A daemon older than this CLI
            // does not report it, which is a normal window during a staged upgrade.
            var deadline = startResult.TryGetProperty("expiresAt", out var expiresAt)
                ? expiresAt.GetDateTimeOffset()
                : DateTimeOffset.UtcNow.AddMinutes(5);

            // Step 2: Try to open browser (detect headless first)
            VerificationUri = authUrl;
            BrowserOpenFailed = !BrowserDetection.CanOpenBrowser();
            FlowState.Value = DeviceFlowState.WaitingForUser;
            _requestRedraw();

            if (!BrowserOpenFailed)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
                }
                catch
                {
                    BrowserOpenFailed = true;
                    _requestRedraw();
                }
            }

            // Step 3: Poll daemon for completion
            var pollTimeout = deadline - DateTimeOffset.UtcNow;
            var pollInterval = TimeSpan.FromSeconds(2);
            var elapsed = TimeSpan.Zero;

            while (elapsed < pollTimeout)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(pollInterval, ct);
                elapsed += pollInterval;

                var statusResponse = await pollStatus(flowState, ct);

                var status = statusResponse.GetProperty("status").GetString();
                if (status is "Completed")
                {
                    var result = parseResult(statusResponse);
                    onSuccess?.Invoke(result);
                    Result = result;
                    FlowState.Value = DeviceFlowState.Succeeded;
                    _requestRedraw();
                    return;
                }

                if (status is "Failed")
                {
                    ErrorMessage = "Authorization failed.";
                    FlowState.Value = DeviceFlowState.Error;
                    _requestRedraw();
                    return;
                }
            }

            ErrorMessage = "Authorization timed out after 5 minutes.";
            FlowState.Value = DeviceFlowState.Expired;
            _requestRedraw();
        }
        catch (OperationCanceledException)
        {
            FlowState.Value = DeviceFlowState.Cancelled;
            _requestRedraw();
        }
        catch (HttpRequestException)
        {
            ErrorMessage = "Could not reach the daemon. Is it running?";
            FlowState.Value = DeviceFlowState.Error;
            _requestRedraw();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            FlowState.Value = DeviceFlowState.Error;
            _requestRedraw();
        }
        finally
        {
            Cancel();
        }
    }

    // ── Device authorization flow (RFC 8628) ─────────────────────────

    private async Task RunDeviceFlowAsync(
        string providerType,
        Action<OAuthDeviceFlowResult>? onSuccess,
        ProviderEntry? entry,
        CancellationToken ct)
    {
        if (_deviceFlowFactory is null)
        {
            ErrorMessage = "OAuth service not available.";
            FlowState.Value = DeviceFlowState.Error;
            _requestRedraw();
            return;
        }

        var descriptor = _registry.Get(providerType);
        OAuthAuth? oauth;
        try
        {
            oauth = ResolveOAuthConfig(providerType, descriptor, entry);
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
            FlowState.Value = DeviceFlowState.Error;
            _requestRedraw();
            return;
        }

        if (oauth is null || oauth.DeviceEndpoint is null)
        {
            ErrorMessage = "Provider does not support OAuth device flow.";
            FlowState.Value = DeviceFlowState.Error;
            _requestRedraw();
            return;
        }

        var service = _deviceFlowFactory.GetFor(oauth);
        var config = OAuthDeviceFlowConfig.FromOAuth(oauth);

        try
        {
            // Step 1: Start device authorization
            var deviceAuth = await service.StartDeviceAuthorizationAsync(config, ct);
            UserCode = deviceAuth.UserCode;
            VerificationUri = deviceAuth.VerificationUri;
            VerificationUriComplete = deviceAuth.VerificationUriComplete;
            FlowState.Value = DeviceFlowState.WaitingForUser;
            _requestRedraw();

            // Step 2: Poll for token
            var result = await service.PollForTokenAsync(config, deviceAuth,
                state =>
                {
                    if (state == DeviceFlowState.Succeeded)
                        return;

                    FlowState.Value = state;
                    _requestRedraw();
                }, ct);

            // Step 3: Store result.
            // Setting FlowState fires its reactive subscribers synchronously
            // (InitWizardPage subscribes to advance to the validation sub-step).
            // We rely on the onSuccess callback below — NOT on subscribers — to
            // kick off the credential probe, so that the lifecycle of the probe
            // CTS doesn't get torpedoed by a duplicate StartProbe call.
            Result = result;
            FlowState.Value = DeviceFlowState.Succeeded;
            _requestRedraw();
            onSuccess?.Invoke(result);
        }
        catch (OAuthDeviceFlowDeniedException)
        {
            ErrorMessage = "Authorization was denied.";
            FlowState.Value = DeviceFlowState.Denied;
            _requestRedraw();
        }
        catch (OAuthDeviceFlowExpiredException)
        {
            ErrorMessage = "The authorization code expired. Please try again.";
            FlowState.Value = DeviceFlowState.Expired;
            _requestRedraw();
        }
        catch (OperationCanceledException)
        {
            FlowState.Value = DeviceFlowState.Cancelled;
            _requestRedraw();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            FlowState.Value = DeviceFlowState.Error;
            _requestRedraw();
        }
        finally
        {
            Cancel();
        }
    }

    private static OAuthAuth? ResolveOAuthConfig(
        string providerType,
        IProviderDescriptor descriptor,
        ProviderEntry? entry)
    {
        if (!string.Equals(providerType, "github-copilot", StringComparison.OrdinalIgnoreCase))
            return descriptor.Auth.GetOAuthConfig();

        if (entry is not null)
            return GitHubCopilotDescriptor.CreateOAuthAuth(entry);

        if (!GitHubCopilotAuthResolver.TryResolveSetupOptions(
                gitHubHost: null,
                gitHubApiBase: null,
                includeAmbientEnvironment: true,
                out var setupOptions,
                out var error))
        {
            throw new InvalidOperationException(error);
        }

        return GitHubCopilotDescriptor.CreateOAuthAuth(setupOptions);
    }
}

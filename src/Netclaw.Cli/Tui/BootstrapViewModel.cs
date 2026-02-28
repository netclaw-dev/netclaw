using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Cli.Daemon;
using R3;
using Termina.Reactive;

namespace Netclaw.Cli.Tui;

/// <summary>
/// Configuration for the bootstrap page, resolved from DI.
/// </summary>
public sealed record BootstrapOptions(string DaemonEndpoint);

/// <summary>
/// ViewModel for the LLM-driven bootstrap conversation.
/// Manages daemon startup, health polling, SignalR session, and message flow.
/// The bootstrap session uses identity_write tools to author SOUL.md and AGENTS.md.
/// </summary>
public sealed class BootstrapViewModel : ReactiveViewModel
{
    private readonly DaemonManager _daemonManager;
    private readonly string _daemonEndpoint;
    private DaemonClient? _client;
    private IDisposable? _outputSubscription;
    private IDisposable? _connectionSubscription;
    private bool _sessionReady;

    private readonly Subject<SessionOutput> _outputSubject = new();

    public ReactiveProperty<bool> IsConnecting { get; } = new(true);
    public ReactiveProperty<bool> IsGenerating { get; } = new(false);
    public ReactiveProperty<bool> IsComplete { get; } = new(false);
    public ReactiveProperty<string> StatusMessage { get; } = new("Starting daemon...");
    public ReactiveProperty<string?> ErrorMessage { get; } = new(null);

    public Observable<SessionOutput> SessionOutput => _outputSubject.AsObservable();

    /// <summary>
    /// Bootstrap system instruction sent as the first message to establish the interview.
    /// </summary>
    internal static readonly string BootstrapInstruction =
        "You are in first-run setup mode. Interview the user to establish your personality " +
        "and their preferences. Ask about:\n" +
        "1. What they'd like to call you (or keep 'Netclaw')\n" +
        "2. Their preferred communication style (concise/detailed, formal/casual)\n" +
        "3. Their name and timezone\n" +
        "4. What they primarily use their homelab for\n\n" +
        "Use identity_write to create SOUL.md with personality/tone and user info. " +
        "Then create AGENTS.md with operating rules. " +
        "Then ask about environment capabilities (Docker, .NET, kubectl, etc.) for TOOLING.md.\n\n" +
        "Keep the conversation natural and friendly. When you've gathered enough info and " +
        "written all three files, tell the user setup is complete.";

    public BootstrapViewModel(DaemonManager daemonManager, BootstrapOptions options)
    {
        _daemonManager = daemonManager;
        _daemonEndpoint = options.DaemonEndpoint;
    }

    public override void OnActivated()
    {
        base.OnActivated();
        _ = StartBootstrapAsync();
    }

    private async Task StartBootstrapAsync()
    {
        // Step 1: Start daemon
        StatusMessage.Value = "Starting Netclaw daemon...";
        RequestRedraw();

        var result = _daemonManager.Start();
        if (!result.Success && !result.Message.Contains("already running", StringComparison.OrdinalIgnoreCase))
        {
            ErrorMessage.Value = $"Failed to start daemon: {result.Message}";
            IsConnecting.Value = false;
            RequestRedraw();
            return;
        }

        // Step 2: Poll health endpoint
        StatusMessage.Value = "Waiting for daemon to become ready...";
        RequestRedraw();

        var healthy = await PollHealthAsync();
        if (!healthy)
        {
            ErrorMessage.Value = "Daemon did not become ready within 30 seconds.";
            IsConnecting.Value = false;
            RequestRedraw();
            return;
        }

        // Step 3: Connect via SignalR
        StatusMessage.Value = "Connecting to daemon...";
        RequestRedraw();

        try
        {
            _client = new DaemonClient(_daemonEndpoint);

            _outputSubscription = _client.SessionOutput.Subscribe(output =>
            {
                _outputSubject.OnNext(output);

                switch (output)
                {
                    case TurnCompleted:
                        IsGenerating.Value = false;
                        StatusMessage.Value = "Your turn";
                        break;
                    case ErrorOutput:
                        IsGenerating.Value = false;
                        StatusMessage.Value = "Error — try again";
                        break;
                }

                RequestRedraw();
            });

            _connectionSubscription = _client.ConnectionEvents.Subscribe(evt =>
            {
                if (evt.State == DaemonConnectionState.Connected && !_sessionReady)
                    _ = InitializeBootstrapSessionAsync();
            });

            await _client.ConnectAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage.Value = $"Failed to connect: {ex.Message}";
            IsConnecting.Value = false;
            RequestRedraw();
        }
    }

    private async Task InitializeBootstrapSessionAsync()
    {
        try
        {
            await _client!.EnsureSessionAsync("bootstrap");
            _sessionReady = true;
            IsConnecting.Value = false;
            StatusMessage.Value = "Connected — starting personality setup...";
            RequestRedraw();

            // Send bootstrap instruction as first user message
            IsGenerating.Value = true;
            StatusMessage.Value = "Generating...";
            await _client.SendAsync(new ChannelInput
            {
                SenderId = "bootstrap-user",
                Contents = [new TextContent(BootstrapInstruction)],
                ReceivedAt = DateTimeOffset.UtcNow
            });
        }
        catch (Exception ex)
        {
            ErrorMessage.Value = $"Session setup failed: {ex.Message}";
            IsConnecting.Value = false;
            RequestRedraw();
        }
    }

    public async Task SubmitAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || _client is null || !_sessionReady)
            return;

        IsGenerating.Value = true;
        StatusMessage.Value = "Generating...";

        try
        {
            await _client.SendAsync(new ChannelInput
            {
                SenderId = "bootstrap-user",
                Contents = [new TextContent(text)],
                ReceivedAt = DateTimeOffset.UtcNow
            });
        }
        catch (Exception ex)
        {
            IsGenerating.Value = false;
            StatusMessage.Value = $"Send failed: {ex.Message}";
        }

        RequestRedraw();
    }

    public void MarkComplete()
    {
        IsComplete.Value = true;
        StatusMessage.Value = "Bootstrap complete!";
        RequestRedraw();
    }

    public void RequestAppShutdown()
    {
        Shutdown();
    }

    private async Task<bool> PollHealthAsync()
    {
        using var httpClient = new HttpClient();
        var healthUrl = $"{_daemonEndpoint}/api/health/ready";

        for (var i = 0; i < 30; i++)
        {
            try
            {
                var response = await httpClient.GetAsync(healthUrl);
                if (response.IsSuccessStatusCode)
                    return true;
            }
            catch
            {
                // Expected while daemon is starting
            }

            await Task.Delay(1000);
            StatusMessage.Value = $"Waiting for daemon... ({i + 1}s)";
            RequestRedraw();
        }

        return false;
    }

    public override void Dispose()
    {
        _outputSubscription?.Dispose();
        _connectionSubscription?.Dispose();
        if (_client is not null)
            _ = _client.DisposeAsync();
        _outputSubject.Dispose();

        IsConnecting.Dispose();
        IsGenerating.Dispose();
        IsComplete.Dispose();
        StatusMessage.Dispose();
        ErrorMessage.Dispose();
        base.Dispose();
    }
}

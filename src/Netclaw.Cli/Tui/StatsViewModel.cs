using System.Net.Http.Json;
using System.Text.Json;
using Netclaw.Configuration;
using R3;
using Termina.Input;
using Termina.Reactive;

namespace Netclaw.Cli.Tui;

public sealed class StatsViewModel : ReactiveViewModel
{
    private readonly string _endpoint;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly int? _days;

    public ReactiveProperty<bool> IsLoading { get; } = new(true);
    public ReactiveProperty<string> StatusMessage { get; } = new("Loading stats...");
    public DaemonStats.Response? Stats { get; private set; }

    public StatsViewModel(
        IHttpClientFactory httpClientFactory,
        Microsoft.Extensions.Configuration.IConfiguration configuration,
        StatsNavigationState navigationState)
    {
        _httpClientFactory = httpClientFactory;
        _days = navigationState.Days;
        _endpoint = configuration["Daemon:Endpoint"]
            ?? Environment.GetEnvironmentVariable("NETCLAW_DAEMON_ENDPOINT")
            ?? "http://127.0.0.1:5199";
    }

    public override void OnActivated()
    {
        base.OnActivated();

        Input.OfType<IInputEvent, KeyPressed>()
            .Subscribe(HandleKeyPress)
            .DisposeWith(Subscriptions);

        _ = LoadStatsAsync();
    }

    private async Task LoadStatsAsync()
    {
        try
        {
            var url = $"{_endpoint.TrimEnd('/')}/api/stats";
            if (_days.HasValue)
                url += $"?days={_days.Value}";

            var client = _httpClientFactory.CreateClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var stats = await client.GetFromJsonAsync<DaemonStats.Response>(
                url,
                new JsonSerializerOptions(JsonSerializerDefaults.Web),
                cts.Token);

            Stats = stats;
            StatusMessage.Value = " [Q] Quit";
        }
        catch
        {
            StatusMessage.Value = " Failed to reach daemon. Is it running?  [Q] Quit";
        }

        IsLoading.Value = false;
        RequestRedraw();
    }

    private void HandleKeyPress(KeyPressed key)
    {
        var keyInfo = key.KeyInfo;

        if (keyInfo.Key == ConsoleKey.Q ||
            keyInfo.Key == ConsoleKey.Escape ||
            (keyInfo.Key == ConsoleKey.C && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control)))
        {
            Shutdown();
        }
    }

    public override void Dispose()
    {
        StatusMessage.Dispose();
        IsLoading.Dispose();
        base.Dispose();
    }
}

/// <summary>
/// Carries the --days parameter from CLI parsing to the stats ViewModel.
/// </summary>
public sealed class StatsNavigationState
{
    public int? Days { get; init; }
}

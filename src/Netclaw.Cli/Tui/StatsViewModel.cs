using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using R3;
using Termina.Input;
using Termina.Reactive;

namespace Netclaw.Cli.Tui;

public sealed class StatsViewModel : ReactiveViewModel
{
    private readonly DaemonApi _api;
    private readonly int? _days;

    public ReactiveProperty<bool> IsLoading { get; } = new(true);
    public ReactiveProperty<string> StatusMessage { get; } = new("Loading stats...");
    public DaemonStats.Response? Stats { get; private set; }

    public StatsViewModel(DaemonApi api, StatsNavigationState navigationState)
    {
        _api = api;
        _days = navigationState.Days;
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
            Stats = await _api.GetStatsAsync(_days);
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

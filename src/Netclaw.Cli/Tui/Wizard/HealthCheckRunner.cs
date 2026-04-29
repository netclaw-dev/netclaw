// -----------------------------------------------------------------------
// <copyright file="HealthCheckRunner.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Cli.Tui.Wizard;

/// <summary>
/// Collects and displays health check results during the wizard's finalization phase.
/// Wraps the result list and notification callback so individual steps can contribute
/// checks without knowing about the UI.
/// </summary>
public sealed class HealthCheckRunner
{
    private readonly Action _notifyChanged;

    public HealthCheckRunner(List<HealthCheckItem> results, Action notifyChanged)
    {
        Results = results;
        _notifyChanged = notifyChanged;
    }

    /// <summary>The shared list of health check results displayed in the UI.</summary>
    public List<HealthCheckItem> Results { get; }

    /// <summary>
    /// Add a health check result and notify the UI.
    /// </summary>
    public void Add(HealthCheckItem item)
    {
        Results.Add(item);
        _notifyChanged();
    }

    /// <summary>
    /// Update the last result in the list and notify the UI.
    /// Typically used to replace a "running" placeholder with a final result.
    /// </summary>
    public void UpdateLast(HealthCheckItem item)
    {
        if (Results.Count > 0)
            Results[^1] = item;
        _notifyChanged();
    }

    /// <summary>
    /// Add a placeholder "in progress" item, then update it with the final result.
    /// </summary>
    public async Task RunCheckAsync(string label, Func<CancellationToken, Task<HealthCheckItem>> check, CancellationToken ct)
    {
        Add(new HealthCheckItem(label, null));
        try
        {
            var result = await check(ct);
            UpdateLast(result);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            UpdateLast(new HealthCheckItem($"{label} timed out", false));
        }
    }

    /// <summary>Whether all checks passed so far.</summary>
    public bool AllPassed => Results.All(h => h.Passed == true);
}

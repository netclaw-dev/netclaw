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
        // Results is read by the render thread while step checks mutate it off-thread; synchronize
        // on the list instance — the same lock HealthCheckStepViewModel uses for its own writes.
        lock (Results)
            Results.Add(item);
        _notifyChanged();
    }

    /// <summary>
    /// Update the last result in the list and notify the UI.
    /// Typically used to replace a "running" placeholder with a final result.
    /// </summary>
    public void UpdateLast(HealthCheckItem item)
    {
        lock (Results)
        {
            if (Results.Count > 0)
                Results[^1] = item;
        }

        _notifyChanged();
    }

    /// <summary>
    /// Emit the standard channel-adapter pre-flight: an in-progress "<paramref name="name"/>
    /// configuration" row, then short-circuit to a passed "(disabled)" row when the adapter is
    /// off, or a failed "(&lt;label&gt; missing)" row for the first blank required credential
    /// (checked in the order given). Returns <c>true</c> only when the adapter is enabled and
    /// every required credential is present, i.e. the caller should continue probing.
    /// </summary>
    public bool BeginAdapterCheck(string name, bool enabled, params (string? value, string label)[] requiredCredentials)
    {
        Add(new HealthCheckItem($"{name} configuration", null));

        if (!enabled)
        {
            UpdateLast(new HealthCheckItem($"{name} configuration (disabled)", true));
            return false;
        }

        foreach (var (value, label) in requiredCredentials)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                UpdateLast(new HealthCheckItem($"{name} configuration ({label} missing)", false));
                return false;
            }
        }

        return true;
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
    public bool AllPassed
    {
        get
        {
            lock (Results)
                return Results.All(h => h.Passed == true && !h.IsWarning);
        }
    }
}

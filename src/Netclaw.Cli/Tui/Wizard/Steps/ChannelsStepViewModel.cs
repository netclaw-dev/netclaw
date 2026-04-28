// -----------------------------------------------------------------------
// <copyright file="ChannelsStepViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Configuration;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Wizard step for per-channel audience configuration.
/// Conditionally shown only when at least one chat service is enabled.
/// Single sub-step with custom keyboard navigation (arrow keys, a/d).
///
/// Channel entries are keyed by source ("slack", "discord", etc.) in the
/// shared context. Each channel step populates its own bucket in OnLeave.
/// This step renders all entries flattened across sources, grouped for display.
/// </summary>
public sealed class ChannelsStepViewModel : IWizardStepViewModel
{
    private WizardContext? _context;

    public string StepId => "channels";
    public string DisplayTitle => "Channels";

    /// <summary>
    /// Flattened view of all channel entries across all sources.
    /// The view reads this for rendering and keyboard navigation.
    /// </summary>
    public List<ChannelEntry> AllEntries
    {
        get
        {
            if (_context is null) return [];
            var all = new List<ChannelEntry>();
            foreach (var entries in _context.ChannelEntries.Values)
                all.AddRange(entries);
            return all;
        }
    }

    public bool HasMultipleSources =>
        _context is not null && _context.ChannelEntries.Count > 1;

    public IReadOnlyList<(ChannelType Source, List<ChannelEntry> Entries)> GroupedEntries
    {
        get
        {
            if (_context is null) return [];
            return _context.ChannelEntries
                .Where(kv => kv.Value.Count > 0)
                .Select(kv => (kv.Key, kv.Value))
                .ToList();
        }
    }

    /// <summary>The selected posture from the shared context, for deriving audience defaults.</summary>
    public DeploymentPosture SelectedPosture => _context?.SelectedPosture ?? DeploymentPosture.Personal;

    public bool IsApplicable(WizardContext context) => context.AnyChatServicesEnabled;

    public int CurrentSubStep => 0;
    public int SubStepCount => 1;

    public string GetHelpText() =>
        "  Use \u2190/\u2192 to change audience. a to add, d to remove. Enter to continue.";

    public bool TryAdvance() => false;
    public bool TryGoBack() => false;

    public void OnEnter(WizardContext context, NavigationDirection direction)
    {
        _context = context;
    }

    public void OnLeave() { }

    public void ContributeConfig(WizardConfigBuilder builder)
    {
        // Channel audiences are set per-source by each channel step's ContributeConfig.
        // This step just allows editing the entries in the shared context.
    }

    public void ContributeSecrets(WizardSecretsBuilder builder) { }

    public Task ContributeHealthChecksAsync(HealthCheckRunner runner, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>
    /// Add a channel entry to a specific source bucket.
    /// Called by the Channels view when the user adds a channel manually.
    /// </summary>
    public void AddEntry(ChannelType source, ChannelEntry entry)
    {
        if (_context is null) return;
        if (!_context.ChannelEntries.TryGetValue(source, out var entries))
        {
            entries = [];
            _context.ChannelEntries[source] = entries;
        }
        entries.Add(entry);
    }

    /// <summary>
    /// Remove a channel entry by reference from any source bucket.
    /// </summary>
    public bool RemoveEntry(ChannelEntry entry)
    {
        if (_context is null) return false;
        foreach (var entries in _context.ChannelEntries.Values)
        {
            if (entries.Remove(entry))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Get the source key for a given entry (for display grouping).
    /// </summary>
    public ChannelType? GetSource(ChannelEntry entry)
    {
        if (_context is null) return null;
        foreach (var (source, entries) in _context.ChannelEntries)
        {
            if (entries.Contains(entry))
                return source;
        }
        return null;
    }

    /// <summary>
    /// Preferred source for new entries added from the Channels view.
    /// When a single chat source is configured, additions go to that source.
    /// When multiple sources exist, prefer Slack for compatibility.
    /// </summary>
    public ChannelType GetPreferredAddSource()
    {
        if (_context is null || _context.ChannelEntries.Count == 0)
            return ChannelType.Slack;

        if (_context.ChannelEntries.Count == 1)
            return _context.ChannelEntries.Keys.First();

        if (_context.ChannelEntries.ContainsKey(ChannelType.Slack))
            return ChannelType.Slack;

        if (_context.ChannelEntries.ContainsKey(ChannelType.Discord))
            return ChannelType.Discord;

        return _context.ChannelEntries.Keys.First();
    }

    public void Dispose() { }
}

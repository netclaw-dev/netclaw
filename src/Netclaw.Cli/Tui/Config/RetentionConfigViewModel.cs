// -----------------------------------------------------------------------
// <copyright file="RetentionConfigViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Config;
using Netclaw.Configuration;
using R3;
using Termina.Reactive;

namespace Netclaw.Cli.Tui.Config;

/// <summary>One editable line: a retention setting, the value on disk, and the text being typed.</summary>
internal sealed class RetentionRow(RetentionSetting setting, RetentionValue saved)
{
    public RetentionSetting Setting { get; } = setting;
    public RetentionValue Saved { get; set; } = saved;
    public ReactiveProperty<string> Draft { get; } = new(saved.Days.ToString());
    /// <summary>True once a key press has started a new number, so later keys extend it.</summary>
    public bool Editing { get; set; }
}

/// <summary>
/// Editor for <c>Retention:*:Days</c>. It shows one row per <see cref="RetentionSettings.All"/>
/// entry, so another retention job adds a row without a change here.
/// </summary>
internal sealed class RetentionConfigViewModel : ReactiveViewModel
{
    private readonly NetclawPaths _paths;

    public RetentionConfigViewModel(NetclawPaths paths)
    {
        _paths = paths;
        string? loadError = null;
        var rows = new List<RetentionRow>();
        foreach (var setting in RetentionSettings.All)
        {
            try
            {
                rows.Add(new RetentionRow(setting, RetentionConfigStore.Read(paths, setting)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                // Degrade to the default instead of throwing from the constructor, which would
                // make the page permanently inaccessible.
                loadError = $"Could not read netclaw.json: {ex.Message}";
                rows.Add(new RetentionRow(setting, new RetentionValue(setting.DefaultDays, IsSet: false)));
            }
        }

        Rows = rows;
        SelectedRow = new ReactiveProperty<int>(0);
        var warnings = string.Join(" ", rows.Select(static r => r.Saved.Warning).Concat(EnvironmentWarnings()).OfType<string>());
        Status = new ReactiveProperty<ConfigStatusMessage>(
            loadError is not null ? new ConfigStatusMessage(loadError, ConfigStatusTone.Error)
            : warnings.Length > 0 ? new ConfigStatusMessage(warnings, ConfigStatusTone.Warning)
            : new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral));
    }

    internal Action<string>? RouteRequested { get; set; }
    internal bool ShutdownRequestedForTest { get; private set; }

    public IReadOnlyList<RetentionRow> Rows { get; }
    public ReactiveProperty<int> SelectedRow { get; }
    public ReactiveProperty<ConfigStatusMessage> Status { get; }

    /// <summary>What the row reads as: the saved value, or the typed text with a note that it is not saved.</summary>
    public string DisplayValue(RetentionRow row)
    {
        if (!row.Editing)
        {
            return row.Saved.Warning is not null
                ? $"{RetentionConfigStore.Describe(row.Saved.Days)} (default; the stored value is not valid, Enter replaces it)"
                : RetentionConfigStore.Describe(row.Saved.Days) + (row.Saved.IsSet ? string.Empty : " (default)");
        }

        return RetentionPolicy.TryParseDays(row.Draft.Value, out var days, out _)
            ? $"{RetentionConfigStore.Describe(days)} (not saved)"
            : $"'{row.Draft.Value}' (not a valid number of days)";
    }

    public void MoveSelection(int delta)
    {
        var next = Math.Clamp(SelectedRow.Value + delta, 0, Rows.Count - 1);
        if (next != SelectedRow.Value)
            SelectedRow.Value = next;
    }

    public void AppendText(string text)
    {
        var row = Rows[SelectedRow.Value];
        // The first key press replaces the saved number instead of extending it.
        row.Draft.Value = (row.Editing ? row.Draft.Value : string.Empty) + text;
        row.Editing = true;
        ClearStatus();
        RequestRedraw();
    }

    public void Backspace()
    {
        var row = Rows[SelectedRow.Value];
        if (row.Draft.Value.Length == 0)
            return;

        // The draft of a stored value that is not valid is the default, not the text in the file.
        // Backspace clears it so the next keys start a new number.
        row.Draft.Value = !row.Editing && row.Saved.Warning is not null ? string.Empty : row.Draft.Value[..^1];
        row.Editing = true;
        ClearStatus();
        RequestRedraw();
    }

    public bool Save()
        => ConfigAutosave.Run(SaveCore, Status, "Data retention save failed", RequestRedraw);

    private bool SaveCore()
    {
        var changes = new List<(RetentionSetting, int)>();
        foreach (var row in Rows.Where(static r => r.Editing || r.Saved.Warning is not null))
        {
            if (!RetentionPolicy.TryParseDays(row.Draft.Value, out var days, out var problem))
            {
                // The next key starts a new number instead of extending the rejected text.
                row.Editing = false;
                Status.Value = new ConfigStatusMessage($"{row.Setting.Label}: {problem}", ConfigStatusTone.Error);
                RequestRedraw();
                return false;
            }

            changes.Add((row.Setting, days));
        }

        var written = RetentionConfigStore.Save(_paths, changes);
        foreach (var row in Rows)
        {
            row.Saved = RetentionConfigStore.Read(_paths, row.Setting);
            row.Draft.Value = row.Saved.Days.ToString();
            row.Editing = false;
        }

        var overrides = string.Join(" ", EnvironmentWarnings().OfType<string>());
        Status.Value = written > 0
            ? new ConfigStatusMessage(
                $"Data retention saved. {RetentionConfigStore.Applied}{(overrides.Length > 0 ? " " + overrides : string.Empty)}",
                overrides.Length > 0 ? ConfigStatusTone.Warning : ConfigStatusTone.Success)
            : new ConfigStatusMessage("Data retention is unchanged.", ConfigStatusTone.Neutral);
        RequestRedraw();
        return true;
    }

    public void GoBack()
    {
        RouteRequested?.Invoke("/config");
        Navigate?.Invoke("/config");
    }

    public void RequestQuit()
    {
        ShutdownRequestedForTest = true;
        Shutdown();
    }

    public override void Dispose()
    {
        foreach (var row in Rows)
            row.Draft.Dispose();
        SelectedRow.Dispose();
        Status.Dispose();
        base.Dispose();
    }

    private static IEnumerable<string?> EnvironmentWarnings()
        => RetentionSettings.All.Select(RetentionConfigStore.EnvironmentOverrideWarning);

    private void ClearStatus()
    {
        if (!string.IsNullOrWhiteSpace(Status.Value.Text))
            Status.Value = new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral);
    }
}

// -----------------------------------------------------------------------
// <copyright file="ApprovalsManagerViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Approvals;
using Netclaw.Configuration;
using R3;
using Termina.Reactive;

namespace Netclaw.Cli.Tui;

public enum ApprovalsManagerState
{
    Loading,
    List,
    Empty,
    RevokeConfirm,
}

public sealed record ApprovalDisplayItem(
    TrustAudience Audience,
    string ToolName,
    ApprovalEntry Entry,
    string AddedText)
{
    public string AudienceWire => Audience.ToWireValue();
    public string DisplayText => Entry.FormatScope();
}

/// <summary>
/// ViewModel for the <c>netclaw approvals</c> interactive TUI. The page is
/// read-and-revoke only; it does not add new grants. All state mutations go
/// through <see cref="ToolApprovalStore"/> so the daemon picks them up on the
/// next approval check.
/// </summary>
public sealed class ApprovalsManagerViewModel : ReactiveViewModel
{
    private readonly ToolApprovalStore _store;
    private readonly TimeProvider _timeProvider;

    public ApprovalsManagerViewModel(NetclawPaths paths, TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _store = new ToolApprovalStore(paths.ToolApprovalsPath, timeProvider);
    }

    public ReactiveProperty<ApprovalsManagerState> CurrentState { get; } = new(ApprovalsManagerState.Loading);
    public ReactiveProperty<string> StatusMessage { get; } = new("");
    public ReactiveProperty<int> StateVersion { get; } = new(0);

    public List<ApprovalDisplayItem> DisplayApprovals { get; } = [];
    public int SelectedIndex { get; set; }

    public ApprovalDisplayItem? PendingRevoke { get; private set; }

    public override void OnActivated()
    {
        base.OnActivated();
        Refresh();
    }

    public void Refresh()
    {
        DisplayApprovals.Clear();
        var snapshot = _store.Snapshot();
        var now = _timeProvider.GetUtcNow();

        foreach (var audienceKey in snapshot.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!SecurityPolicyDefaults.TryParseAudience(audienceKey, out var audience))
                continue;

            var tools = snapshot[audienceKey];
            foreach (var toolName in tools.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                foreach (var entry in tools[toolName]
                    .OrderBy(static e => e.Verb, StringComparer.Ordinal)
                    .ThenBy(static e => e.Directory ?? string.Empty, StringComparer.Ordinal))
                {
                    DisplayApprovals.Add(new ApprovalDisplayItem(
                        audience, toolName, entry, ApprovalTimeText.Added(entry.CreatedAt, now)));
                }
            }
        }

        if (SelectedIndex >= DisplayApprovals.Count)
            SelectedIndex = Math.Max(0, DisplayApprovals.Count - 1);

        CurrentState.Value = DisplayApprovals.Count == 0
            ? ApprovalsManagerState.Empty
            : ApprovalsManagerState.List;

        StateVersion.Value++;
    }

    public void StartRevoke()
    {
        if (CurrentState.Value != ApprovalsManagerState.List) return;
        if (SelectedIndex < 0 || SelectedIndex >= DisplayApprovals.Count) return;

        PendingRevoke = DisplayApprovals[SelectedIndex];
        CurrentState.Value = ApprovalsManagerState.RevokeConfirm;
        StateVersion.Value++;
    }

    public void ConfirmRevoke()
    {
        if (PendingRevoke is not { } target)
        {
            GoBack();
            return;
        }

        var removed = _store.RemoveApproval(target.Audience, target.ToolName, target.Entry);
        StatusMessage.Value = removed
            ? $"✔ Removed '{target.DisplayText}' from {target.AudienceWire} / {target.ToolName}."
            : $"⚠ Entry not found (may have been removed elsewhere).";

        PendingRevoke = null;
        Refresh();
    }

    public void GoBack()
    {
        if (CurrentState.Value == ApprovalsManagerState.RevokeConfirm)
        {
            PendingRevoke = null;
            Refresh();
        }
    }

    public void RequestQuit() => Shutdown();
}

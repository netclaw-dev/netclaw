// -----------------------------------------------------------------------
// <copyright file="ApprovalsManagerViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
    string AudienceWire,
    string ToolName,
    string Pattern);

/// <summary>
/// ViewModel for the <c>netclaw approvals</c> interactive TUI. The page is
/// read-and-revoke only; it does not add new grants. All state mutations go
/// through <see cref="ToolApprovalStore"/> so the daemon picks them up on the
/// next approval check.
/// </summary>
public sealed class ApprovalsManagerViewModel : ReactiveViewModel
{
    private readonly ToolApprovalStore _store;

    public ApprovalsManagerViewModel(NetclawPaths paths)
    {
        _store = new ToolApprovalStore(paths.ToolApprovalsPath);
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

        foreach (var audienceKey in snapshot.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!SecurityPolicyDefaults.TryParseAudience(audienceKey, out var audience))
                continue;

            var tools = snapshot[audienceKey];
            foreach (var toolName in tools.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                foreach (var pattern in tools[toolName].OrderBy(p => p, StringComparer.Ordinal))
                    DisplayApprovals.Add(new ApprovalDisplayItem(audience, audienceKey, toolName, pattern));
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

        var removed = _store.RemoveApproval(target.Audience, target.ToolName, target.Pattern);
        StatusMessage.Value = removed
            ? $"✔ Removed '{target.Pattern}' from {target.AudienceWire} / {target.ToolName}."
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

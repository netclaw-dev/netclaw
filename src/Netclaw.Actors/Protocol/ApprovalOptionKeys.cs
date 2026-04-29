// -----------------------------------------------------------------------
// <copyright file="ApprovalOptionKeys.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Protocol;

/// <summary>
/// Stable wire keys for tool approval options. These are part of the
/// channel/session protocol — channel adapters render them, the user picks one,
/// and the chosen key flows back to the session via
/// <see cref="ToolInteractionResponse.SelectedKey"/>. Renaming a key is a
/// breaking change to every channel adapter.
/// </summary>
public static class ApprovalOptionKeys
{
    public const string ApproveOnce = "approve_once";
    public const string ApproveSession = "approve_session";
    public const string ApproveAlways = "approve_always";
    public const string Deny = "deny";

    public const string ApproveOnceLabel = "Approve once";
    public const string ApproveSessionLabel = "Approve for this chat";
    public const string ApproveAlwaysLabel = "Approve always";
    public const string DenyLabel = "Deny";
}

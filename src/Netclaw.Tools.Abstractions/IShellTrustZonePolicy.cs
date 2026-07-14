// -----------------------------------------------------------------------
// <copyright file="IShellTrustZonePolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Tools;

/// <summary>
/// Authorizes shell command path arguments for non-interactive channels
/// (reminders, webhooks, headless). Delegates to the same audience-scoped
/// write-access resolution that file tools use, so shell and <c>file_write</c>
/// share one interpretation of the audience's filesystem mode
/// (<c>All</c> ⇒ unrestricted, <c>Roots</c> ⇒ confined, <c>None</c> ⇒ denied).
/// </summary>
public interface IShellTrustZonePolicy
{
    /// <summary>
    /// Returns <c>true</c> when the given already-resolved absolute path is
    /// authorized for write under the context's audience. A shell command whose
    /// path argument or working directory is not authorized is denied for
    /// non-interactive channels.
    /// </summary>
    bool IsShellWritePathAuthorized(string fullPath, ToolInvocationContext context);
}

// -----------------------------------------------------------------------
// <copyright file="IShellTrustZonePolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Tools;

/// <summary>
/// Provides trust zone roots for shell command path validation.
/// Non-interactive channels (reminders, webhooks) use this to sandbox
/// shell commands to allowed filesystem paths.
/// </summary>
public interface IShellTrustZonePolicy
{
    /// <summary>
    /// Returns the set of allowed filesystem root directories for the given
    /// execution context. Shell commands with path arguments outside these
    /// roots are denied for non-interactive channels.
    /// </summary>
    IReadOnlyList<string> GetTrustZoneRoots(ToolExecutionContext context);
}

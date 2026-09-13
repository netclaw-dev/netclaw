// -----------------------------------------------------------------------
// <copyright file="DaemonCommandDispatch.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Cli.Daemon;

/// <summary>
/// Help-token gating for <c>netclaw daemon &lt;subcommand&gt;</c>, extracted out of Program.cs's
/// top-level-statement dispatch so it is independently unit-testable.
/// </summary>
/// <remarks>
/// <c>pair</c> and <c>devices</c> take a nested action word and already guard their own
/// trailing <c>--help</c> inline in Program.cs. The remaining lifecycle verbs
/// (<c>start</c>/<c>stop</c>/<c>status</c>/<c>install</c>/<c>uninstall</c>) take no further
/// positional arguments, so previously a trailing help token anywhere after the verb was
/// silently ignored and the verb executed for real — e.g. <c>netclaw daemon stop --help</c>
/// actually stopped the daemon instead of printing help (canary finding, same missed-help
/// pattern audited for the memory subcommand).
/// </remarks>
internal static class DaemonCommandDispatch
{
    private static readonly HashSet<string> LifecycleVerbsRequiringTrailingHelpGuard =
        new(StringComparer.Ordinal) { "start", "stop", "status", "install", "uninstall" };

    /// <summary>
    /// Returns <c>true</c> if <paramref name="subcommand"/> is one of the guarded lifecycle
    /// verbs and <paramref name="args"/> carries a trailing help token (anywhere at or after
    /// index 2 — i.e. after <c>netclaw daemon &lt;subcommand&gt;</c>).
    /// </summary>
    public static bool ShouldShowHelpInsteadOfExecuting(string subcommand, string[] args)
        => LifecycleVerbsRequiringTrailingHelpGuard.Contains(subcommand)
           && CliArgsParser.HasTrailingHelpToken(args, startIndex: 2);
}

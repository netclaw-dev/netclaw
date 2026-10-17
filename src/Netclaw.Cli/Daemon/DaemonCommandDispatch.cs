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

    /// <summary>
    /// Returns <c>true</c> for <c>daemon devices help</c> and for a help flag after the devices
    /// subcommand. A device may be called "help", so past the subcommand only the flags count.
    /// </summary>
    public static bool ShouldShowDevicesHelp(string[] args)
        => CliArgsParser.IsHelpToken(args.Length > 2 ? args[2] : "list")
           || CliArgsParser.HasTrailingHelpToken(args, startIndex: 3, includeBareHelp: false);

    /// <summary>
    /// Collects the <c>--env KEY=VALUE</c> entries of <c>netclaw daemon install</c>. The parser
    /// does not check an entry. <see cref="DaemonManager.InstallAsync"/> checks them all before
    /// it changes anything. A flag with no value and any other argument are refused, so no
    /// argument is dropped without notice.
    /// </summary>
    public static bool TryParseInstallArguments(string[] args, out IReadOnlyList<string> envEntries, out string error)
    {
        var entries = new List<string>();
        envEntries = entries;
        error = string.Empty;

        for (var i = 2; i < args.Length; i++)
        {
            if (args[i] != "--env")
            {
                error = $"Unknown argument '{args[i]}' for `netclaw daemon install`. Run `netclaw daemon install --help`.";
                return false;
            }

            if (i + 1 >= args.Length)
            {
                error = "The --env flag needs a KEY=VALUE argument.";
                return false;
            }

            entries.Add(args[++i]);
        }

        return true;
    }
}

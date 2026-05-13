// -----------------------------------------------------------------------
// <copyright file="ApprovalsCommand.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Json;
using Netclaw.Configuration;

namespace Netclaw.Cli.Approvals;

/// <summary>
/// Handles <c>netclaw approvals</c> single-shot subcommands: list, revoke, help.
/// Operates on <c>tool-approvals.json</c> directly via
/// <see cref="ToolApprovalStore"/>; the daemon picks up changes on its next
/// approval check without a restart.
/// </summary>
internal static class ApprovalsCommand
{
    private sealed record ListOptions(TrustAudience? Audience, string? Tool, bool EmitJson);

    private sealed record RevokeOptions(string? Pattern, TrustAudience? Audience, string? Tool, bool RevokeAll);

    private sealed record TrustVerbOptions(string Verb, TrustAudience Audience, string Tool);

    public const string DefaultTrustVerbTool = "shell_execute";

    public static Task<int> RunAsync(
        string[] args,
        NetclawPaths paths,
        TextWriter? output = null)
    {
        var writer = output ?? Console.Out;
        var subcommand = args.Length > 1 ? args[1] : "help";

        return subcommand switch
        {
            "list" => Task.FromResult(RunList(args, paths, writer)),
            "revoke" => Task.FromResult(RunRevoke(args, paths, writer)),
            "trust-verb" => Task.FromResult(RunTrustVerb(args, paths, writer)),
            "help" or "-h" or "--help" => Task.FromResult(WriteHelp(writer)),
            _ => Task.FromResult(WriteHelp(writer)),
        };
    }

    private static int RunList(string[] args, NetclawPaths paths, TextWriter writer)
    {
        if (TryParseListFlags(args, writer) is not { } opts)
            return 1;

        var store = new ToolApprovalStore(paths.ToolApprovalsPath);
        WarnIfQuarantined(store, writer);

        var view = BuildView(store.Snapshot(), opts.Audience, opts.Tool);

        if (opts.EmitJson)
        {
            writer.WriteLine(JsonSerializer.Serialize(view, JsonDefaults.IndentedOmitNull));
            return 0;
        }

        if (view.Audiences.Count == 0)
        {
            writer.WriteLine("No persistent approvals.");
            return 0;
        }

        var first = true;
        foreach (var (audienceKey, tools) in view.Audiences)
        {
            foreach (var (toolName, entries) in tools)
            {
                if (!first) writer.WriteLine();
                writer.WriteLine($"{audienceKey} / {toolName}");
                foreach (var entry in entries)
                    writer.WriteLine($"  {entry.FormatScope()}");
                first = false;
            }
        }

        return 0;
    }

    private static int RunRevoke(string[] args, NetclawPaths paths, TextWriter writer)
    {
        if (TryParseRevokeFlags(args, writer) is not { } opts)
            return 1;

        if (opts.RevokeAll && opts.Tool is null)
        {
            writer.WriteLine("Error: --all requires --tool <name>.");
            writer.WriteLine("Usage: netclaw approvals revoke --tool <name> --all [--audience personal|team|public]");
            return 1;
        }

        var store = new ToolApprovalStore(paths.ToolApprovalsPath);
        WarnIfQuarantined(store, writer);

        if (opts.RevokeAll)
            return RunRevokeAll(opts, store, writer);

        if (opts.Pattern is null)
        {
            writer.WriteLine("Usage: netclaw approvals revoke <pattern> [--tool <name>] [--audience personal|team|public]");
            writer.WriteLine("       netclaw approvals revoke --tool <name> --all [--audience personal|team|public]");
            return 1;
        }

        // Could not parse — the CLI is the deliberate scriptable path, so
        // we reject unrecognized inputs loudly rather than silently treat a
        // bare verb as a global wildcard.
        if (!ApprovalEntry.TryParseScope(opts.Pattern, out var lookup, out var parseError))
        {
            writer.WriteLine($"Error: {parseError}");
            writer.WriteLine("Could not parse revoke pattern '" + opts.Pattern + "'.");
            writer.WriteLine("Patterns must use the form '<verb> in <directory>' or '<verb> anywhere',");
            writer.WriteLine("matching the labels emitted by 'netclaw approvals list'.");
            return 1;
        }

        var snapshot = store.Snapshot();
        var removedAny = false;

        foreach (var (audienceKey, tools) in snapshot)
        {
            if (!SecurityPolicyDefaults.TryParseAudience(audienceKey, out var audience))
                continue;
            if (opts.Audience is { } target && audience != target)
                continue;

            foreach (var (toolName, _) in tools)
            {
                if (opts.Tool is not null && !string.Equals(toolName, opts.Tool, StringComparison.Ordinal))
                    continue;

                if (store.RemoveApproval(audience, toolName, lookup))
                {
                    writer.WriteLine($"Removed '{lookup.FormatScope()}' from {audienceKey} / {toolName}.");
                    removedAny = true;
                }
            }
        }

        if (!removedAny)
        {
            writer.WriteLine("No matching approval found.");
            return 1;
        }

        return 0;
    }

    private static int RunTrustVerb(string[] args, NetclawPaths paths, TextWriter writer)
    {
        if (TryParseTrustVerbFlags(args, writer) is not { } opts)
            return 1;

        // Reject shapes the user might confuse with the scope-label syntax
        // accepted by `revoke <pattern>`. Accepting "git anywhere" or
        // "git in /repo" here would silently persist a verb token containing
        // a space — the gate would never match a real candidate, and the
        // user would see no error.
        if (opts.Verb.Contains(" anywhere", StringComparison.Ordinal)
            || opts.Verb.Contains(" in ", StringComparison.Ordinal)
            || opts.Verb.StartsWith('-'))
        {
            writer.WriteLine($"Error: '{opts.Verb}' is not a verb. Pass just the executable name (e.g. 'git push').");
            writer.WriteLine("For scope labels (e.g. 'git push anywhere') use 'netclaw approvals revoke' or edit");
            writer.WriteLine("the persisted tool-approvals.json directly.");
            return 1;
        }

        var store = new ToolApprovalStore(paths.ToolApprovalsPath);
        WarnIfQuarantined(store, writer);

        var entry = new ApprovalEntry { Verb = opts.Verb, Directory = null };
        var audienceWire = opts.Audience.ToWireValue();

        if (store.AddApproval(opts.Audience, opts.Tool, entry))
            writer.WriteLine($"Trusted '{entry.FormatScope()}' for {audienceWire} / {opts.Tool}.");
        else
            writer.WriteLine($"No changes: '{entry.FormatScope()}' is already trusted for {audienceWire} / {opts.Tool}.");

        return 0;
    }

    private static TrustVerbOptions? TryParseTrustVerbFlags(string[] args, TextWriter writer)
    {
        string? verb = null;
        TrustAudience? audience = null;
        string? tool = null;

        for (var i = 2; i < args.Length; i++)
        {
            switch (TryConsumeSharedFlag(args, ref i, writer, ref audience, ref tool))
            {
                case FlagOutcome.Consumed: continue;
                case FlagOutcome.Error: return null;
            }

            var arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                writer.WriteLine($"Error: Unknown flag: {arg}");
                return null;
            }

            if (verb is not null)
            {
                writer.WriteLine($"Error: Unexpected extra argument: {arg}");
                return null;
            }
            verb = arg;
        }

        if (string.IsNullOrWhiteSpace(verb))
        {
            writer.WriteLine("Usage: netclaw approvals trust-verb <verb> [--audience personal|team|public] [--tool <name>]");
            writer.WriteLine();
            writer.WriteLine("Adds a global-wildcard '(verb, null)' approval entry — the verb runs in any cwd");
            writer.WriteLine("without prompting. Used to pre-approve verbs for unattended/scheduled tasks.");
            return null;
        }

        return new TrustVerbOptions(
            Verb: verb!.Trim(),
            Audience: audience ?? TrustAudience.Personal,
            Tool: string.IsNullOrWhiteSpace(tool) ? DefaultTrustVerbTool : tool);
    }

    private static int RunRevokeAll(RevokeOptions opts, ToolApprovalStore store, TextWriter writer)
    {
        IEnumerable<TrustAudience> audiences = opts.Audience is { } only ? [only] : TrustAudiences.All;
        var totalRemoved = 0;
        foreach (var audience in audiences)
            totalRemoved += store.RemoveAllForTool(audience, opts.Tool!);

        if (totalRemoved == 0)
        {
            writer.WriteLine($"No approvals found for tool '{opts.Tool}'.");
            return 1;
        }

        writer.WriteLine($"Removed {totalRemoved} approval(s) for tool '{opts.Tool}'.");
        return 0;
    }

    private static int WriteHelp(TextWriter writer)
    {
        writer.WriteLine("Usage: netclaw approvals [<subcommand>] [<args>]");
        writer.WriteLine();
        writer.WriteLine("Subcommands:");
        writer.WriteLine("  (none) | tui      Launch the interactive approvals TUI.");
        writer.WriteLine("  list              List persistent approvals from tool-approvals.json.");
        writer.WriteLine("                    Flags: --audience <personal|team|public>, --tool <name>, --json");
        writer.WriteLine("  revoke <pattern>  Remove an approval entry by its user-visible form:");
        writer.WriteLine("                      '<verb> in <directory>'  — folder-scoped grant");
        writer.WriteLine("                      '<verb> anywhere'         — global wildcard");
        writer.WriteLine("                    Flags: --audience <personal|team|public>, --tool <name>");
        writer.WriteLine("  revoke --tool <name> --all");
        writer.WriteLine("                    Remove every approval entry for a tool.");
        writer.WriteLine("                    Flags: --audience <personal|team|public>");
        writer.WriteLine("  trust-verb <verb> Add a global-wildcard '(verb, null)' approval — the verb runs");
        writer.WriteLine("                    in any cwd without prompting. Use to pre-approve verbs for");
        writer.WriteLine("                    unattended or scheduled invocations.");
        writer.WriteLine("                    Flags: --audience <personal|team|public> (default personal)");
        writer.WriteLine("                           --tool <name>                       (default shell_execute)");
        writer.WriteLine("  help              Show this message.");
        writer.WriteLine();
        writer.WriteLine("Exit codes: 0 success, 1 user error or no match.");
        writer.WriteLine();
        writer.WriteLine("The daemon does not require a restart after these mutations; the next approval");
        writer.WriteLine("check re-reads the file.");
        return 0;
    }

    private enum FlagOutcome { NotMine, Consumed, Error }

    /// <summary>
    /// Tries to consume a flag that <c>list</c> and <c>revoke</c> share
    /// (<c>--audience</c>, <c>--tool</c>). Advances <paramref name="i"/> past
    /// the flag's value when applicable. Returns <see cref="FlagOutcome.Error"/>
    /// after writing a message if the flag is recognized but malformed.
    /// </summary>
    private static FlagOutcome TryConsumeSharedFlag(
        string[] args, ref int i, TextWriter writer,
        ref TrustAudience? audience, ref string? tool)
    {
        var arg = args[i];
        switch (arg)
        {
            case "--audience":
                if (i + 1 >= args.Length)
                {
                    writer.WriteLine("Error: --audience requires a value (personal, team, or public).");
                    return FlagOutcome.Error;
                }
                var value = args[++i];
                if (!SecurityPolicyDefaults.TryParseAudience(value, out var parsed))
                {
                    writer.WriteLine($"Error: Unknown audience '{value}'. Expected: personal, team, public.");
                    return FlagOutcome.Error;
                }
                audience = parsed;
                return FlagOutcome.Consumed;

            case "--tool":
                if (i + 1 >= args.Length)
                {
                    writer.WriteLine("Error: --tool requires a value.");
                    return FlagOutcome.Error;
                }
                tool = args[++i];
                return FlagOutcome.Consumed;

            default:
                return FlagOutcome.NotMine;
        }
    }

    private static ListOptions? TryParseListFlags(string[] args, TextWriter writer)
    {
        TrustAudience? audience = null;
        string? tool = null;
        var emitJson = false;

        for (var i = 2; i < args.Length; i++)
        {
            if (args[i] == "--json")
            {
                emitJson = true;
                continue;
            }

            switch (TryConsumeSharedFlag(args, ref i, writer, ref audience, ref tool))
            {
                case FlagOutcome.Consumed: continue;
                case FlagOutcome.Error: return null;
            }

            writer.WriteLine($"Error: Unknown flag: {args[i]}");
            return null;
        }

        return new ListOptions(audience, tool, emitJson);
    }

    private static RevokeOptions? TryParseRevokeFlags(string[] args, TextWriter writer)
    {
        string? pattern = null;
        TrustAudience? audience = null;
        string? tool = null;
        var revokeAll = false;

        for (var i = 2; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg == "--all")
            {
                revokeAll = true;
                continue;
            }

            switch (TryConsumeSharedFlag(args, ref i, writer, ref audience, ref tool))
            {
                case FlagOutcome.Consumed: continue;
                case FlagOutcome.Error: return null;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                writer.WriteLine($"Error: Unknown flag: {arg}");
                return null;
            }

            if (pattern is not null)
            {
                writer.WriteLine($"Error: Unexpected extra argument: {arg}");
                return null;
            }
            pattern = arg;
        }

        return new RevokeOptions(pattern, audience, tool, revokeAll);
    }

    private static ApprovalsListView BuildView(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ApprovalEntry>>> snapshot,
        TrustAudience? audienceFilter,
        string? toolFilter)
    {
        var view = new ApprovalsListView();

        foreach (var (audienceKey, tools) in snapshot)
        {
            if (audienceFilter is not null)
            {
                if (!SecurityPolicyDefaults.TryParseAudience(audienceKey, out var parsed)) continue;
                if (parsed != audienceFilter.Value) continue;
            }

            var filteredTools = new SortedDictionary<string, List<ApprovalEntry>>(StringComparer.Ordinal);
            foreach (var (toolName, entries) in tools)
            {
                if (toolFilter is not null && !string.Equals(toolName, toolFilter, StringComparison.Ordinal))
                    continue;
                if (entries.Count == 0) continue;
                filteredTools[toolName] =
                [
                    .. entries.OrderBy(static e => e.Verb, StringComparer.Ordinal)
                              .ThenBy(static e => e.Directory ?? string.Empty, StringComparer.Ordinal)
                ];
            }

            if (filteredTools.Count > 0)
                view.Audiences[audienceKey] = filteredTools;
        }

        return view;
    }

    private static void WarnIfQuarantined(ToolApprovalStore store, TextWriter writer)
    {
        // Two quarantine paths exist after the v2 cutover:
        //   - .v1.bak  : legacy v1 file detected and moved aside on upgrade
        //   - .invalid : malformed (unparseable) file moved aside as fail-closed
        // Operators see different remediation guidance for each.
        if (File.Exists(store.V1QuarantinePath))
        {
            writer.WriteLine($"Note: Your previous approvals were quarantined to '{store.V1QuarantinePath}' during the v2 schema upgrade.");
            writer.WriteLine("      Inspect or restore manually if needed; the daemon started with an empty v2 store.");
        }

        if (File.Exists(store.MalformedQuarantinePath))
        {
            writer.WriteLine($"Warning: A malformed approvals file was quarantined to '{store.MalformedQuarantinePath}'.");
            writer.WriteLine("         The active file was reset to empty after a parse failure.");
            writer.WriteLine("         Inspect the .invalid copy before restoring grants.");
        }
    }
}

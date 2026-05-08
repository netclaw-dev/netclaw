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
            writer.WriteLine(JsonSerializer.Serialize(view, JsonDefaults.Indented));
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
            foreach (var (toolName, patterns) in tools)
            {
                if (!first) writer.WriteLine();
                writer.WriteLine($"{audienceKey} / {toolName}");
                foreach (var pattern in patterns)
                    writer.WriteLine($"  {pattern}");
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

                if (store.RemoveApproval(audience, toolName, opts.Pattern))
                {
                    writer.WriteLine($"Removed '{opts.Pattern}' from {audienceKey} / {toolName}.");
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
        writer.WriteLine("  revoke <pattern>  Remove an exact-match approval entry.");
        writer.WriteLine("                    Flags: --audience <personal|team|public>, --tool <name>");
        writer.WriteLine("  revoke --tool <name> --all");
        writer.WriteLine("                    Remove every approval entry for a tool.");
        writer.WriteLine("                    Flags: --audience <personal|team|public>");
        writer.WriteLine("  help              Show this message.");
        writer.WriteLine();
        writer.WriteLine("Exit codes: 0 success, 1 user error or no match.");
        writer.WriteLine();
        writer.WriteLine("The daemon does not require a restart after a revoke; the next approval");
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
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> snapshot,
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

            var filteredTools = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var (toolName, patterns) in tools)
            {
                if (toolFilter is not null && !string.Equals(toolName, toolFilter, StringComparison.Ordinal))
                    continue;
                if (patterns.Count == 0) continue;
                filteredTools[toolName] = [.. patterns.OrderBy(p => p, StringComparer.Ordinal)];
            }

            if (filteredTools.Count > 0)
                view.Audiences[audienceKey] = filteredTools;
        }

        return view;
    }

    private static void WarnIfQuarantined(ToolApprovalStore store, TextWriter writer)
    {
        if (!File.Exists(store.QuarantinePath))
            return;

        writer.WriteLine($"Warning: A quarantined approvals file exists at '{store.QuarantinePath}'.");
        writer.WriteLine("         The active file was reset to empty after a parse failure.");
        writer.WriteLine("         Inspect the .invalid copy before restoring grants.");
    }
}

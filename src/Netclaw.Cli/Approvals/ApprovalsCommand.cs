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
        if (!TryParseListFlags(args, writer, out var audienceFilter, out var toolFilter, out var emitJson))
            return 1;

        WarnIfQuarantined(paths, writer);

        var store = new ToolApprovalStore(paths.ToolApprovalsPath);
        var snapshot = store.Snapshot();
        var view = BuildView(snapshot, audienceFilter, toolFilter);

        if (emitJson)
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
        foreach (var audienceKey in view.Audiences.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            foreach (var toolName in view.Audiences[audienceKey].Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                if (!first) writer.WriteLine();
                writer.WriteLine($"{audienceKey} / {toolName}");
                foreach (var pattern in view.Audiences[audienceKey][toolName].OrderBy(p => p, StringComparer.Ordinal))
                    writer.WriteLine($"  {pattern}");
                first = false;
            }
        }

        return 0;
    }

    private static int RunRevoke(string[] args, NetclawPaths paths, TextWriter writer)
    {
        if (!TryParseRevokeFlags(args, writer, out var pattern, out var audienceFilter, out var toolFilter, out var revokeAll))
            return 1;

        if (revokeAll && toolFilter is null)
        {
            writer.WriteLine("Error: --all requires --tool <name>.");
            writer.WriteLine("Usage: netclaw approvals revoke --tool <name> --all [--audience personal|team|public]");
            return 1;
        }

        WarnIfQuarantined(paths, writer);

        var store = new ToolApprovalStore(paths.ToolApprovalsPath);

        if (revokeAll)
        {
            var audiences = audienceFilter is null
                ? new[] { TrustAudience.Personal, TrustAudience.Team, TrustAudience.Public }
                : [audienceFilter.Value];

            var totalRemoved = 0;
            foreach (var audience in audiences)
                totalRemoved += store.RemoveAllForTool(audience, toolFilter!);

            if (totalRemoved == 0)
            {
                writer.WriteLine($"No approvals found for tool '{toolFilter}'.");
                return 1;
            }

            writer.WriteLine($"Removed {totalRemoved} approval(s) for tool '{toolFilter}'.");
            return 0;
        }

        if (pattern is null)
        {
            writer.WriteLine("Usage: netclaw approvals revoke <pattern> [--tool <name>] [--audience personal|team|public]");
            writer.WriteLine("       netclaw approvals revoke --tool <name> --all [--audience personal|team|public]");
            return 1;
        }

        var snapshot = store.Snapshot();
        var removedAny = false;

        foreach (var (audienceKey, tools) in snapshot.Audiences)
        {
            if (!SecurityPolicyDefaults.TryParseAudience(audienceKey, out var audience))
                continue;
            if (audienceFilter is not null && audience != audienceFilter.Value)
                continue;

            foreach (var (toolName, _) in tools)
            {
                if (toolFilter is not null && !string.Equals(toolName, toolFilter, StringComparison.Ordinal))
                    continue;

                if (store.RemoveApproval(audience, toolName, pattern))
                {
                    writer.WriteLine($"Removed '{pattern}' from {audienceKey} / {toolName}.");
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
        writer.WriteLine("Exit codes: 0 success, 1 user error or no match, 2 malformed file.");
        writer.WriteLine();
        writer.WriteLine("The daemon does not require a restart after a revoke; the next approval");
        writer.WriteLine("check re-reads the file.");
        return 0;
    }

    private static bool TryParseListFlags(
        string[] args, TextWriter writer,
        out TrustAudience? audienceFilter, out string? toolFilter, out bool emitJson)
    {
        audienceFilter = null;
        toolFilter = null;
        emitJson = false;

        for (var i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--json":
                    emitJson = true;
                    break;
                case "--audience" when i + 1 < args.Length:
                    if (!SecurityPolicyDefaults.TryParseAudience(args[++i], out var audience))
                    {
                        writer.WriteLine($"Error: Unknown audience '{args[i]}'. Expected: personal, team, public.");
                        return false;
                    }
                    audienceFilter = audience;
                    break;
                case "--tool" when i + 1 < args.Length:
                    toolFilter = args[++i];
                    break;
                default:
                    writer.WriteLine($"Error: Unknown flag or missing value: {args[i]}");
                    return false;
            }
        }

        return true;
    }

    private static bool TryParseRevokeFlags(
        string[] args, TextWriter writer,
        out string? pattern, out TrustAudience? audienceFilter, out string? toolFilter, out bool revokeAll)
    {
        pattern = null;
        audienceFilter = null;
        toolFilter = null;
        revokeAll = false;

        for (var i = 2; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg == "--all")
            {
                revokeAll = true;
                continue;
            }

            if (arg == "--audience" && i + 1 < args.Length)
            {
                if (!SecurityPolicyDefaults.TryParseAudience(args[++i], out var audience))
                {
                    writer.WriteLine($"Error: Unknown audience '{args[i]}'. Expected: personal, team, public.");
                    return false;
                }
                audienceFilter = audience;
                continue;
            }

            if (arg == "--tool" && i + 1 < args.Length)
            {
                toolFilter = args[++i];
                continue;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                writer.WriteLine($"Error: Unknown flag: {arg}");
                return false;
            }

            // Positional pattern; reject duplicates.
            if (pattern is not null)
            {
                writer.WriteLine($"Error: Unexpected extra argument: {arg}");
                return false;
            }
            pattern = arg;
        }

        return true;
    }

    private static ApprovalsListView BuildView(
        ToolApprovalData snapshot, TrustAudience? audienceFilter, string? toolFilter)
    {
        var view = new ApprovalsListView();

        foreach (var (audienceKey, tools) in snapshot.Audiences)
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

    private static void WarnIfQuarantined(NetclawPaths paths, TextWriter writer)
    {
        var quarantine = paths.ToolApprovalsPath + ".invalid";
        if (File.Exists(quarantine))
        {
            writer.WriteLine($"Warning: A quarantined approvals file exists at '{quarantine}'.");
            writer.WriteLine("         The active file was reset to empty after a parse failure.");
            writer.WriteLine("         Inspect the .invalid copy before restoring grants.");
        }
    }
}

// -----------------------------------------------------------------------
// <copyright file="ApprovalsCommand.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

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

    private sealed record TrustVerbOptions(
        string Verb,
        TrustAudience Audience,
        string Tool,
        ApprovalShell? Shell);

    public const string DefaultTrustVerbTool = "shell_execute";

    public static Task<int> RunAsync(
        string[] args,
        NetclawPaths paths,
        TextWriter? output = null,
        TimeProvider? timeProvider = null,
        TextWriter? diagnostics = null)
    {
        var writer = output ?? Console.Out;
        var diagnosticWriter = diagnostics ?? Console.Error;
        var clock = timeProvider ?? TimeProvider.System;
        var subcommand = args.Length > 1 ? args[1] : "help";

        return subcommand switch
        {
            "list" => Task.FromResult(RunList(args, paths, writer, diagnosticWriter, clock)),
            "revoke" => Task.FromResult(RunRevoke(args, paths, writer, diagnosticWriter, clock)),
            "trust-verb" => Task.FromResult(RunTrustVerb(args, paths, writer, diagnosticWriter, clock)),
            "help" or "-h" or "--help" => Task.FromResult(WriteHelp(paths, writer, clock)),
            _ => Task.FromResult(WriteHelp(paths, writer, clock)),
        };
    }

    private static int RunList(
        string[] args,
        NetclawPaths paths,
        TextWriter writer,
        TextWriter diagnostics,
        TimeProvider clock)
    {
        if (TryParseListFlags(args, writer) is not { } opts)
            return 1;

        var store = CreateStore(paths, clock);
        var load = store.TryLoad();
        if (load is ApprovalStoreLoadResult.Unavailable unavailable)
        {
            WriteStoreError(unavailable.Failure, store, writer);
            return 1;
        }

        ReportMigrationOmissions(store, diagnostics);
        if (!opts.EmitJson)
        {
            WarnIfRecoveryAvailable(store, writer);
        }

        var data = ((ApprovalStoreLoadResult.Ready)load).Data;
        var view = BuildView(data.Audiences, opts.Audience, opts.Tool);

        if (opts.EmitJson)
        {
            writer.WriteLine(JsonSerializer.Serialize(
                view.ToWire(),
                ApprovalsListJsonContext.Default.ApprovalsListWire));
            return 0;
        }

        if (view.Audiences.Count == 0)
        {
            writer.WriteLine("No persistent approvals.");
            return 0;
        }

        var now = clock.GetUtcNow();
        var first = true;
        foreach (var (audienceKey, tools) in view.Audiences)
        {
            foreach (var (toolName, entries) in tools)
            {
                if (!first) writer.WriteLine();
                writer.WriteLine($"{audienceKey} / {toolName}");
                var rows = entries.Select(e => (Scope: e.FormatScope(), e.CreatedAt)).ToList();
                var scopeWidth = rows.Count == 0 ? 0 : rows.Max(r => r.Scope.Length);
                foreach (var (scope, createdAt) in rows)
                    writer.WriteLine($"  {scope.PadRight(scopeWidth)}   {ApprovalTimeText.Added(createdAt, now)}");
                first = false;
            }
        }

        return 0;
    }

    private static int RunRevoke(
        string[] args,
        NetclawPaths paths,
        TextWriter writer,
        TextWriter diagnostics,
        TimeProvider clock)
    {
        if (TryParseRevokeFlags(args, writer) is not { } opts)
            return 1;

        if (opts.RevokeAll && opts.Tool is null)
        {
            writer.WriteLine("Error: --all requires --tool <name>.");
            writer.WriteLine("Usage: netclaw approvals revoke --tool <name> --all [--audience personal|team|public]");
            return 1;
        }

        var store = CreateStore(paths, clock);
        var load = store.TryLoad();
        if (load is ApprovalStoreLoadResult.Unavailable unavailable)
        {
            WriteStoreError(unavailable.Failure, store, writer);
            return 1;
        }

        ReportMigrationOmissions(store, diagnostics);
        WarnIfRecoveryAvailable(store, writer);

        if (opts.RevokeAll)
            return RunRevokeAll(opts, store, writer);

        if (opts.Pattern is null)
        {
            writer.WriteLine("Usage: netclaw approvals revoke <pattern> [--tool <name>] [--audience personal|team|public]");
            writer.WriteLine("       netclaw approvals revoke --tool <name> --all [--audience personal|team|public]");
            return 1;
        }

        var snapshot = ((ApprovalStoreLoadResult.Ready)load).Data.Audiences;
        var exactTargets = new List<(TrustAudience Audience, string AudienceKey, string ToolName, ApprovalEntry Entry)>();
        var legacyTargets = new List<(TrustAudience Audience, string AudienceKey, string ToolName, ApprovalEntry Entry)>();

        foreach (var (audienceKey, tools) in snapshot)
        {
            if (!SecurityPolicyDefaults.TryParseAudience(audienceKey, out var audience))
                continue;
            if (opts.Audience is { } target && audience != target)
                continue;

            foreach (var (toolName, entries) in tools)
            {
                if (opts.Tool is not null && !ToolFlagMatches(opts.Tool, toolName))
                    continue;

                foreach (var entry in entries)
                {
                    var candidateTarget = (audience, audienceKey, toolName, entry);
                    if (ScopeLabelEquals(entry, entry.FormatScope(), opts.Pattern))
                    {
                        exactTargets.Add(candidateTarget);
                    }
                    else if (ScopeLabelEquals(entry, FormatLegacyScope(entry), opts.Pattern))
                    {
                        legacyTargets.Add(candidateTarget);
                    }
                }
            }
        }

        var targets = exactTargets.Count > 0 ? exactTargets : legacyTargets;
        if (exactTargets.Count == 0 && legacyTargets.Count > 1)
        {
            writer.WriteLine("Error: The old approval scope matches more than one typed phrase.");
            writer.WriteLine("Use the typed label from 'netclaw approvals list'.");
            return 1;
        }

        var removedAny = false;
        foreach (var target in targets)
        {
            var removal = store.TryRemoveApproval(target.Audience, target.ToolName, target.Entry);
            if (removal is ApprovalStoreChangeResult.Unavailable removeFailure)
            {
                WriteStoreError(removeFailure.Failure, store, writer);
                return 1;
            }

            if (((ApprovalStoreChangeResult.Completed)removal).ChangeCount > 0)
            {
                writer.WriteLine($"Removed '{target.Entry.FormatScope()}' from {target.AudienceKey} / {target.ToolName}.");
                removedAny = true;
            }
        }

        if (!removedAny)
        {
            writer.WriteLine("No matching approval found.");
            return 1;
        }

        return 0;
    }

    private static string FormatLegacyScope(ApprovalEntry entry) =>
        entry.Directory is null
            ? $"{entry.Verb} anywhere"
            : $"{entry.Verb} in {entry.Directory}";

    private static bool ScopeLabelEquals(ApprovalEntry entry, string label, string supplied) =>
        entry.Shell is { } shell
            ? ToolApprovalEntryComparer.Equals(label, supplied, shell)
            : string.Equals(label, supplied, StringComparison.Ordinal);

    private static int RunTrustVerb(
        string[] args,
        NetclawPaths paths,
        TextWriter writer,
        TextWriter diagnostics,
        TimeProvider clock)
    {
        if (TryParseTrustVerbFlags(args, writer) is not { } opts)
            return 1;

        // Persist under the canonical name so runtime lookups (which
        // query canonical) find the grant. If the operator passed the
        // LLM-facing alias, reverse-resolve it here.
        var canonicalTool = LlmFacingToolName.TryReverseSanitizedToCanonical(opts.Tool) ?? opts.Tool;
        ApprovalEntry entry;
        if (string.Equals(canonicalTool, DefaultTrustVerbTool, StringComparison.Ordinal))
        {
            var shell = opts.Shell ?? NativeShell;
            if (!ShellApprovalGrantParser.TryCreateTokenPrefix(
                    shell,
                    opts.Verb,
                    out var shellEntry,
                    out var parseError))
            {
                writer.WriteLine($"Error: {parseError}");
                return 1;
            }

            entry = shellEntry;
        }
        else
        {
            if (opts.Shell is not null)
            {
                writer.WriteLine("Error: --shell is valid only for shell_execute.");
                return 1;
            }

            try
            {
                entry = ApprovalEntry.CreateNonShell(opts.Verb);
            }
            catch (ArgumentException)
            {
                writer.WriteLine("Error: The verb must be nonempty and canonical.");
                return 1;
            }
        }

        var store = CreateStore(paths, clock);
        var change = store.TryAddApproval(opts.Audience, canonicalTool, entry);
        if (change is ApprovalStoreChangeResult.Unavailable unavailable)
        {
            WriteStoreError(unavailable.Failure, store, writer);
            return 1;
        }

        ReportMigrationOmissions(store, diagnostics);
        WarnIfRecoveryAvailable(store, writer);
        var audienceWire = opts.Audience.ToWireValue();

        if (((ApprovalStoreChangeResult.Completed)change).ChangeCount > 0)
            writer.WriteLine($"Trusted '{entry.FormatScope()}' for {audienceWire} / {canonicalTool}.");
        else
            writer.WriteLine($"No changes: '{entry.FormatScope()}' is already trusted for {audienceWire} / {canonicalTool}.");

        return 0;
    }

    private static TrustVerbOptions? TryParseTrustVerbFlags(string[] args, TextWriter writer)
    {
        string? verb = null;
        TrustAudience? audience = null;
        string? tool = null;
        ApprovalShell? shell = null;

        for (var i = 2; i < args.Length; i++)
        {
            if (args[i] == "--shell")
            {
                if (i + 1 >= args.Length || !TryParseShell(args[++i], out var parsedShell))
                {
                    writer.WriteLine("Error: --shell requires bash or powershell.");
                    return null;
                }

                shell = parsedShell;
                continue;
            }

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
            writer.WriteLine("Usage: netclaw approvals trust-verb <phrase> [--audience personal|team|public] [--tool <name>]");
            writer.WriteLine();
            writer.WriteLine("Adds a global-wildcard approval. Shell phrases use typed canonical token prefixes;");
            writer.WriteLine("other tools use exact phrases. Use it for unattended or scheduled tasks.");
            return null;
        }

        return new TrustVerbOptions(
            Verb: verb!,
            Audience: audience ?? TrustAudience.Personal,
            Tool: string.IsNullOrWhiteSpace(tool) ? DefaultTrustVerbTool : tool,
            Shell: shell);
    }

    /// <summary>
    /// Whether the operator-supplied <c>--tool</c> flag value matches a
    /// stored tool name. Stored names are canonical (server/tool for
    /// MCP); the operator may pass either form (audit logs surface the
    /// canonical name; LLM transcripts surface the LLM-facing alias).
    /// First-party tools have identical canonical and LLM-facing names,
    /// so the additional comparison is a no-op for them.
    /// </summary>
    private static bool ToolFlagMatches(string toolFlag, string storedToolName)
    {
        if (string.Equals(storedToolName, toolFlag, StringComparison.Ordinal))
            return true;
        var reversed = LlmFacingToolName.TryReverseSanitizedToCanonical(toolFlag);
        return reversed is not null && string.Equals(storedToolName, reversed, StringComparison.Ordinal);
    }

    private static int RunRevokeAll(RevokeOptions opts, ToolApprovalStore store, TextWriter writer)
    {
        IEnumerable<TrustAudience> audiences = opts.Audience is { } only ? [only] : TrustAudiences.All;
        var totalRemoved = 0;
        // Approval grants are persisted under the canonical tool name
        // (server/tool for MCP). If the operator passed the LLM-facing
        // alias (server__tool) — the form audit logs and LLM transcripts
        // surface — reverse-resolve it so `revoke --all` actually finds
        // the entries. Names without a '__' separator (first-party
        // tools) pass through unchanged.
        var canonicalTool = LlmFacingToolName.TryReverseSanitizedToCanonical(opts.Tool!) ?? opts.Tool!;
        foreach (var audience in audiences)
        {
            var change = store.TryRemoveAllForTool(audience, canonicalTool);
            if (change is ApprovalStoreChangeResult.Unavailable unavailable)
            {
                WriteStoreError(unavailable.Failure, store, writer);
                return 1;
            }

            totalRemoved += ((ApprovalStoreChangeResult.Completed)change).ChangeCount;
        }

        if (totalRemoved == 0)
        {
            writer.WriteLine($"No approvals found for tool '{opts.Tool}'.");
            return 1;
        }

        writer.WriteLine($"Removed {totalRemoved} approval(s) for tool '{opts.Tool}'.");
        return 0;
    }

    private static int WriteHelp(NetclawPaths paths, TextWriter writer, TimeProvider clock)
    {
        writer.WriteLine("Usage: netclaw approvals [<subcommand>] [<args>]");
        writer.WriteLine();
        writer.WriteLine("Subcommands:");
        writer.WriteLine("  (none) | tui      Launch the interactive approvals TUI.");
        writer.WriteLine("  list              List persistent approvals from tool-approvals.json.");
        writer.WriteLine("                    Flags: --audience <personal|team|public>, --tool <name>, --json");
        writer.WriteLine("  revoke <pattern>  Remove an approval entry by the exact label from list.");
        writer.WriteLine("                    Shell labels include shell, match kind, phrase, and scope;");
        writer.WriteLine("                    non-shell labels use '<verb> in <directory>' or '<verb> anywhere'.");
        writer.WriteLine("                    Flags: --audience <personal|team|public>, --tool <name>");
        writer.WriteLine("  revoke --tool <name> --all");
        writer.WriteLine("                    Remove every approval entry for a tool.");
        writer.WriteLine("                    Flags: --audience <personal|team|public>");
        writer.WriteLine("  trust-verb <phrase>");
        writer.WriteLine("                    Add one static canonical phrase as a global wildcard.");
        writer.WriteLine("                    Shell phrases become typed token prefixes; other tools stay exact.");
        writer.WriteLine("                    Flags: --audience <personal|team|public> (default personal)");
        writer.WriteLine("                           --tool <name>                       (default shell_execute)");
        writer.WriteLine("                           --shell <bash|powershell>           (shell_execute only)");
        writer.WriteLine("  help              Show this message.");
        writer.WriteLine();
        writer.WriteLine("Exit codes: 0 success, 1 user error or no match.");
        writer.WriteLine();
        writer.WriteLine("The daemon does not require a restart after these mutations; the next approval");
        writer.WriteLine("check re-reads the file.");
        WarnIfRecoveryAvailable(CreateStore(paths, clock), writer);
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
        IReadOnlyDictionary<
            string,
            IReadOnlyDictionary<string, IReadOnlyList<ApprovalEntry>>> snapshot,
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
                if (toolFilter is not null && !ToolFlagMatches(toolFilter, toolName))
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

    private static ToolApprovalStore CreateStore(NetclawPaths paths, TimeProvider clock) =>
        new(
            paths.ToolApprovalsPath,
            clock,
            new ApprovalStoreMigrationContext(NativeShell));

    private static ApprovalShell NativeShell => OperatingSystem.IsWindows()
        ? ApprovalShell.PowerShell
        : ApprovalShell.Bash;

    private static bool TryParseShell(string value, out ApprovalShell shell)
    {
        switch (value.ToLowerInvariant())
        {
            case "bash":
                shell = ApprovalShell.Bash;
                return true;
            case "powershell":
                shell = ApprovalShell.PowerShell;
                return true;
            default:
                shell = default;
                return false;
        }
    }

    private static void WriteStoreError(
        ApprovalStoreFailure failure,
        ToolApprovalStore store,
        TextWriter writer)
    {
        writer.WriteLine($"Error: The approval store is unavailable ({failure}).");
        WarnIfRecoveryAvailable(store, writer);
    }

    private static void WarnIfRecoveryAvailable(ToolApprovalStore store, TextWriter writer)
    {
        if (File.Exists(store.V1QuarantinePath))
        {
            writer.WriteLine($"Note: A version-1 store is preserved at '{store.V1QuarantinePath}'.");
        }

        if (File.Exists(store.V2BackupPath))
        {
            writer.WriteLine($"Recovery: A version-2 backup exists at '{store.V2BackupPath}'.");
            writer.WriteLine("Stop the daemon, replace the active approval file with that backup, and start the current daemon.");
        }
    }

    private static void ReportMigrationOmissions(ToolApprovalStore store, TextWriter diagnostics)
    {
        if (store.LastMigrationOmittedEntryCount > 0)
        {
            diagnostics.WriteLine(
                $"Approval store version-2 conversion omitted {store.LastMigrationOmittedEntryCount} unrepresentable entries.");
        }
    }
}

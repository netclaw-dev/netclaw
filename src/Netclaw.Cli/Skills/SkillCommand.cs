// -----------------------------------------------------------------------
// <copyright file="SkillCommand.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Netclaw.Actors.Skills;
using Netclaw.Cli.Config;
using Netclaw.Cli.Json;
using Netclaw.Configuration;

namespace Netclaw.Cli.Skills;

/// <summary>
/// Handles <c>netclaw skill &lt;subcommand&gt;</c> CLI subcommands.
/// All commands are offline — no daemon required.
/// </summary>
internal static class SkillCommand
{
    public static Task<int> RunAsync(string[] args, NetclawPaths paths)
    {
        var subcommand = args.Length > 1 ? args[1] : "list";

        if (subcommand is "help" or "-h" or "--help")
        {
            WriteHelp();
            return Task.FromResult(0);
        }

        if (subcommand is "source")
        {
            var sourceAction = args.Length > 2 ? args[2] : "list";
            return Task.FromResult(sourceAction switch
            {
                "list" => RunSourceList(paths),
                "add" => RunSourceAdd(args, paths),
                "remove" => RunSourceRemove(args, paths),
                "enable" => RunSourceToggle(args, paths, enable: true),
                "disable" => RunSourceToggle(args, paths, enable: false),
                _ => WriteSourceHelp()
            });
        }

        return Task.FromResult(subcommand switch
        {
            "list" => RunList(paths),
            "show" => RunShow(args, paths),
            "validate" => RunValidate(args),
            "remove" => RunRemove(args, paths),
            "issues" => RunIssues(paths),
            "search" => RunSearch(args, paths),
            _ => WriteHelp()
        });
    }

    // ── Subcommand implementations ──

    private static int RunList(NetclawPaths paths)
    {
        var result = ScanAll(paths);

        if (result.AcceptedSkills.Count == 0 && result.Issues.Count == 0)
        {
            Console.WriteLine("No skills found.");
            return 0;
        }

        const int colName = 24;
        const int colSource = 10;
        const int colVersion = 10;

        Console.WriteLine(
            $"{"NAME",-colName}  {"SOURCE",-colSource}  {"VERSION",-colVersion}  STATUS");
        Console.WriteLine(new string('-', colName + colSource + colVersion + 12));

        foreach (var skill in result.AcceptedSkills)
        {
            var source = ClassifySource(skill, paths);
            var version = skill.Version ?? "-";

            Console.WriteLine(
                $"{skill.Name,-colName}  {source,-colSource}  {version,-colVersion}  ok");
        }

        // Also show issues inline
        foreach (var issue in result.Issues)
        {
            var name = issue.SkillName ?? Path.GetFileNameWithoutExtension(issue.Path);
            Console.WriteLine(
                $"{name,-colName}  {"?",-colSource}  {"-",-colVersion}  {issue.Kind}");
        }

        Console.WriteLine();
        Console.WriteLine(
            $"{result.AcceptedSkills.Count} skill(s), {result.Issues.Count} issue(s)");

        return 0;
    }

    private static int RunShow(string[] args, NetclawPaths paths)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: netclaw skill show <name>");
            return 1;
        }

        var name = args[2].ToLowerInvariant();
        var result = ScanAll(paths);
        var skill = result.AcceptedSkills.FirstOrDefault(
            s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

        if (skill is null)
        {
            Console.Error.WriteLine($"[FAIL] skill '{name}' not found.");
            return 1;
        }

        Console.WriteLine($"Name:        {skill.Name}");
        Console.WriteLine($"Display:     {skill.DisplayName}");
        Console.WriteLine($"Source:      {ClassifySource(skill, paths)}");
        Console.WriteLine($"Version:     {skill.Version ?? "-"}");
        Console.WriteLine($"Category:    {skill.Category ?? "-"}");
        Console.WriteLine($"License:     {skill.License ?? "-"}");
        Console.WriteLine($"Path:        {skill.FilePath}");
        Console.WriteLine($"Flat file:   {skill.IsFlatFile}");

        if (skill.AllowedTools is not null)
            Console.WriteLine($"Tools:       {skill.AllowedTools}");
        if (skill.ResourcePaths is { Count: > 0 })
            Console.WriteLine($"Resources:   {string.Join(", ", skill.ResourcePaths)}");

        Console.WriteLine($"Description: {skill.Description}");
        Console.WriteLine();

        // Print full SKILL.md content
        if (File.Exists(skill.FilePath))
        {
            Console.WriteLine("--- content ---");
            Console.WriteLine(File.ReadAllText(skill.FilePath));
        }

        return 0;
    }

    private static int RunValidate(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: netclaw skill validate <path>");
            return 1;
        }

        var filePath = Path.GetFullPath(args[2]);
        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine($"[FAIL] file not found: {filePath}");
            return 1;
        }

        // Delegate to the scanner for full validation (frontmatter, description, name matching).
        var parentDir = Path.GetDirectoryName(filePath)!;
        var fileName = Path.GetFileName(filePath);

        // If it's a SKILL.md inside a directory, scan the parent's parent
        // If it's a flat .md file, scan the parent directory
        string scanDir;
        if (string.Equals(fileName, "SKILL.md", StringComparison.OrdinalIgnoreCase))
        {
            // skill-name/SKILL.md — scan the grandparent
            scanDir = Path.GetDirectoryName(parentDir) ?? parentDir;
        }
        else
        {
            // flat file like my-skill.md — scan the parent
            scanDir = parentDir;
        }

        var scanResult = SkillScanner.Scan(scanDir);

        // Find the entry or issue matching this file
        var matchedSkill = scanResult.AcceptedSkills.FirstOrDefault(
            s => string.Equals(s.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

        if (matchedSkill is not null)
        {
            Console.WriteLine("[OK] Valid skill file.");
            Console.WriteLine($"  Name:        {matchedSkill.Name}");
            Console.WriteLine($"  Description: {matchedSkill.Description}");
            Console.WriteLine($"  Version:     {matchedSkill.Version ?? "-"}");
            Console.WriteLine($"  License:     {matchedSkill.License ?? "-"}");
            return 0;
        }

        // Check if there's a specific issue for this file
        var matchedIssue = scanResult.Issues.FirstOrDefault(
            i => string.Equals(i.Path, filePath, StringComparison.OrdinalIgnoreCase));

        if (matchedIssue is not null)
        {
            Console.Error.WriteLine($"[FAIL] {matchedIssue.Kind}: {matchedIssue.Message}");
            return 1;
        }

        // Scanner didn't find it — likely a structural issue
        Console.Error.WriteLine("[FAIL] scanner did not recognize this file as a valid skill.");
        return 1;
    }

    private static int RunRemove(string[] args, NetclawPaths paths)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: netclaw skill remove <name>");
            return 1;
        }

        var name = args[2].ToLowerInvariant();
        var result = ScanAll(paths);
        var skill = result.AcceptedSkills.FirstOrDefault(
            s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

        if (skill is null)
        {
            Console.Error.WriteLine($"[FAIL] skill '{name}' not found.");
            return 1;
        }

        var source = ClassifySource(skill, paths);
        if (source is "system")
        {
            Console.Error.WriteLine($"[FAIL] cannot remove system skill '{name}'. System skills are managed by the daemon.");
            return 1;
        }

        if (source is "external")
        {
            Console.Error.WriteLine($"[FAIL] cannot remove external skill '{name}'. Manage it in its source directory.");
            return 1;
        }

        if (skill.IsFlatFile)
        {
            File.Delete(skill.FilePath);
            Console.WriteLine($"Removed flat skill file: {skill.FilePath}");
        }
        else
        {
            Directory.Delete(skill.SkillDirectory, recursive: true);

            // Clean empty parent category directories (consistent with SkillManageTool)
            var parent = Path.GetDirectoryName(skill.SkillDirectory);
            if (parent is not null && parent != paths.SkillsDirectory
                && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }

            Console.WriteLine($"Removed skill directory: {skill.SkillDirectory}");
        }

        return 0;
    }

    private static int RunIssues(NetclawPaths paths)
    {
        var result = ScanAll(paths);

        if (result.Issues.Count == 0)
        {
            Console.WriteLine("No issues found.");
            return 0;
        }

        const int colName = 24;
        const int colKind = 30;

        Console.WriteLine($"{"NAME",-colName}  {"KIND",-colKind}  MESSAGE");
        Console.WriteLine(new string('-', colName + colKind + 12));

        foreach (var issue in result.Issues)
        {
            var name = issue.SkillName ?? Path.GetFileNameWithoutExtension(issue.Path);
            Console.WriteLine($"{name,-colName}  {issue.Kind,-colKind}  {issue.Message}");
        }

        Console.WriteLine();
        Console.WriteLine($"{result.Issues.Count} issue(s)");

        return 0;
    }

    private static int RunSearch(string[] args, NetclawPaths paths)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: netclaw skill search <query>");
            return 1;
        }

        var query = string.Join(' ', args.Skip(2)).ToLowerInvariant();
        var result = ScanAll(paths);

        var matches = result.AcceptedSkills
            .Where(s =>
                s.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (s.DisplayName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        if (matches.Count == 0)
        {
            Console.WriteLine($"No skills matching '{query}'.");
            return 0;
        }

        const int colName = 24;
        const int colSource = 10;
        const int colVersion = 10;

        Console.WriteLine(
            $"{"NAME",-colName}  {"SOURCE",-colSource}  {"VERSION",-colVersion}  DESCRIPTION");
        Console.WriteLine(new string('-', colName + colSource + colVersion + 14));

        foreach (var skill in matches)
        {
            var source = ClassifySource(skill, paths);
            var version = skill.Version ?? "-";
            var desc = skill.Description.Length > 60
                ? skill.Description[..57] + "..."
                : skill.Description;
            Console.WriteLine(
                $"{skill.Name,-colName}  {source,-colSource}  {version,-colVersion}  {desc}");
        }

        Console.WriteLine();
        Console.WriteLine($"{matches.Count} match(es)");

        return 0;
    }

    // ── Source subcommands ──

    private static int RunSourceList(NetclawPaths paths)
    {
        var config = LoadExternalSkillsConfig(paths);

        if (config.Sources.Count == 0)
        {
            Console.WriteLine("No external skill sources configured.");

            // Probe for well-known sources
            var probed = ExternalSkillsConfig.ProbeWellKnownSources();
            if (probed.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Detected well-known skill directories on disk:");
                foreach (var p in probed)
                    Console.WriteLine($"  {p.WellKnownAlias,-16} {p.ResolvedPath}");
                Console.WriteLine();
                Console.WriteLine("Add one with: netclaw skill source add <name> --well-known <alias>");
            }

            return 0;
        }

        const int colName = 20;
        const int colEnabled = 10;
        const int colType = 14;

        Console.WriteLine(
            $"{"NAME",-colName}  {"ENABLED",-colEnabled}  {"TYPE",-colType}  PATH / WELL-KNOWN");
        Console.WriteLine(new string('-', colName + colEnabled + colType + 24));

        foreach (var source in config.Sources)
        {
            var type = source.WellKnown is not null ? "well-known" : "path";
            var target = source.WellKnown ?? source.Path ?? "-";
            Console.WriteLine(
                $"{source.Name,-colName}  {(source.Enabled ? "yes" : "no"),-colEnabled}  {type,-colType}  {target}");
        }

        return 0;
    }

    private static int RunSourceAdd(string[] args, NetclawPaths paths)
    {
        // netclaw skill source add <name> --path <dir> | --well-known <alias>
        if (args.Length < 4)
        {
            Console.Error.WriteLine("Usage: netclaw skill source add <name> --path <dir>");
            Console.Error.WriteLine("       netclaw skill source add <name> --well-known <alias>");
            return 1;
        }

        var name = args[3];
        string? sourcePath = null;
        string? wellKnown = null;

        for (var i = 4; i < args.Length; i++)
        {
            if (args[i] is "--path" && i + 1 < args.Length)
                sourcePath = args[++i];
            else if (args[i] is "--well-known" && i + 1 < args.Length)
                wellKnown = args[++i];
        }

        if (sourcePath is null && wellKnown is null)
        {
            Console.Error.WriteLine("[FAIL] must specify --path <dir> or --well-known <alias>.");
            return 1;
        }

        if (sourcePath is not null && wellKnown is not null)
        {
            Console.Error.WriteLine("[FAIL] --path and --well-known are mutually exclusive.");
            return 1;
        }

        // Validate well-known alias resolves
        if (wellKnown is not null && ExternalSkillsConfig.ResolveWellKnownPath(wellKnown) is null)
        {
            Console.Error.WriteLine($"[FAIL] unknown well-known alias '{wellKnown}'. Supported: claude-code, open-code.");
            return 1;
        }

        // Validate path exists
        if (sourcePath is not null && !Directory.Exists(sourcePath))
        {
            Console.Error.WriteLine($"[FAIL] directory not found: {sourcePath}");
            return 1;
        }

        var config = LoadExternalSkillsConfig(paths);

        // Check for duplicate name
        if (config.Sources.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine($"[FAIL] source '{name}' already exists. Remove it first or use a different name.");
            return 1;
        }

        config.Sources.Add(new ExternalSkillSource
        {
            Name = name,
            Path = sourcePath,
            WellKnown = wellKnown,
            Enabled = true,
            AllowSymlinks = wellKnown is not null // well-known sources typically need symlinks
        });

        SaveExternalSkillsConfig(paths, config);
        Console.WriteLine($"Added external source '{name}'.");
        return 0;
    }

    private static int RunSourceRemove(string[] args, NetclawPaths paths)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("Usage: netclaw skill source remove <name>");
            return 1;
        }

        var name = args[3];
        var config = LoadExternalSkillsConfig(paths);

        var existing = config.Sources.FindIndex(
            s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

        if (existing < 0)
        {
            Console.Error.WriteLine($"[FAIL] source '{name}' not found.");
            return 1;
        }

        config.Sources.RemoveAt(existing);
        SaveExternalSkillsConfig(paths, config);
        Console.WriteLine($"Removed external source '{name}'.");
        return 0;
    }

    private static int RunSourceToggle(string[] args, NetclawPaths paths, bool enable)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine($"Usage: netclaw skill source {(enable ? "enable" : "disable")} <name>");
            return 1;
        }

        var name = args[3];
        var config = LoadExternalSkillsConfig(paths);

        var source = config.Sources.FirstOrDefault(
            s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

        if (source is null)
        {
            Console.Error.WriteLine($"[FAIL] source '{name}' not found.");
            return 1;
        }

        source.Enabled = enable;
        SaveExternalSkillsConfig(paths, config);
        Console.WriteLine($"Source '{name}' {(enable ? "enabled" : "disabled")}.");
        return 0;
    }

    // ── Helpers ──

    /// <summary>
    /// Run the full scan (native + external sources) using config from netclaw.json.
    /// </summary>
    private static MergedSkillScanResult ScanAll(NetclawPaths paths)
    {
        var config = LoadExternalSkillsConfig(paths);
        var externalSources = config.ResolveEnabledSources();
        return SkillScanner.ScanAndMerge(paths.SkillsDirectory, externalSources);
    }

    /// <summary>
    /// Load the ExternalSkills section from netclaw.json using Microsoft.Extensions.Configuration.
    /// </summary>
    private static ExternalSkillsConfig LoadExternalSkillsConfig(NetclawPaths paths)
    {
        var configBuilder = new ConfigurationBuilder();
        if (File.Exists(paths.NetclawConfigPath))
            configBuilder.AddJsonFile(paths.NetclawConfigPath, optional: true);
        var configuration = configBuilder.Build();
        return configuration.GetSection("ExternalSkills").Get<ExternalSkillsConfig>()
            ?? new ExternalSkillsConfig();
    }

    /// <summary>
    /// Persist the ExternalSkills config back to netclaw.json via ConfigFileHelper.
    /// </summary>
    private static void SaveExternalSkillsConfig(NetclawPaths paths, ExternalSkillsConfig config)
    {
        var dict = ConfigFileHelper.LoadJsonDict(paths.NetclawConfigPath);

        // Serialize the config to a JsonElement so it round-trips cleanly
        var serialized = JsonSerializer.SerializeToElement(config, JsonDefaults.ConfigFile);
        dict["ExternalSkills"] = serialized;

        ConfigFileHelper.WriteConfigFile(paths.NetclawConfigPath, dict);
    }

    /// <summary>
    /// Classify a skill's source for display purposes.
    /// </summary>
    private static string ClassifySource(SkillEntry skill, NetclawPaths paths)
    {
        var systemPrefix = paths.SkillsDirectory + Path.DirectorySeparatorChar + ".system" + Path.DirectorySeparatorChar;
        if (skill.FilePath.StartsWith(systemPrefix, StringComparison.OrdinalIgnoreCase))
            return "system";

        var nativePrefix = paths.SkillsDirectory + Path.DirectorySeparatorChar;
        if (skill.FilePath.StartsWith(nativePrefix, StringComparison.OrdinalIgnoreCase))
            return "native";

        return "external";
    }

    private static int WriteHelp()
    {
        Console.WriteLine("Usage: netclaw skill <subcommand>");
        Console.WriteLine();
        Console.WriteLine("Subcommands:");
        Console.WriteLine("  list                                          List all discovered skills (default)");
        Console.WriteLine("  show <name>                                   Show skill details and content");
        Console.WriteLine("  validate <path>                               Validate a SKILL.md file's frontmatter");
        Console.WriteLine("  remove <name>                                 Remove a native skill");
        Console.WriteLine("  issues                                        Show only scanner issues");
        Console.WriteLine("  search <query>                                Search skills by name or description");
        Console.WriteLine("  source list                                   List configured external sources");
        Console.WriteLine("  source add <name> --path <dir>                Add a custom external source");
        Console.WriteLine("  source add <name> --well-known <alias>        Add a well-known source (claude-code, open-code)");
        Console.WriteLine("  source remove <name>                          Remove an external source");
        Console.WriteLine("  source enable <name>                          Enable an external source");
        Console.WriteLine("  source disable <name>                         Disable an external source");
        Console.WriteLine();
        Console.WriteLine("All subcommands are offline — no daemon required.");
        return 0;
    }

    private static int WriteSourceHelp()
    {
        Console.WriteLine("Usage: netclaw skill source <action>");
        Console.WriteLine();
        Console.WriteLine("Actions:");
        Console.WriteLine("  list                                          List configured external sources");
        Console.WriteLine("  add <name> --path <dir>                       Add a custom external source");
        Console.WriteLine("  add <name> --well-known <alias>               Add a well-known source (claude-code, open-code)");
        Console.WriteLine("  remove <name>                                 Remove an external source");
        Console.WriteLine("  enable <name>                                 Enable an external source");
        Console.WriteLine("  disable <name>                                Disable an external source");
        return 0;
    }
}

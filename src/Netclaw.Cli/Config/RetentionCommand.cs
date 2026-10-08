// -----------------------------------------------------------------------
// <copyright file="RetentionCommand.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Configuration;

namespace Netclaw.Cli.Config;

/// <summary>
/// <c>netclaw config retention [--logs-days &lt;days&gt;]</c>: shows the retention settings, or
/// sets the ones named. Offline, like the <c>netclaw config</c> editor: it reads and writes
/// netclaw.json and the running daemon applies the change.
/// </summary>
internal static class RetentionCommand
{
    public static int Run(string[] args, NetclawPaths paths, TextWriter output, TextWriter error)
    {
        if (args.Length > 0 && CliArgsParser.IsHelpToken(args[0]))
            return WriteHelp(output);

        if (!File.Exists(paths.NetclawConfigPath))
        {
            error.WriteLine(ConfigCommand.MissingConfigMessage);
            return 1;
        }

        var changes = new List<(RetentionSetting Setting, int Days)>();
        for (var i = 0; i < args.Length; i++)
        {
            var setting = RetentionSettings.All.FirstOrDefault(s => string.Equals(s.CliOption, args[i], StringComparison.Ordinal));
            if (setting is null)
            {
                error.WriteLine($"Unknown option '{args[i]}'. Run `netclaw config retention --help`.");
                return 1;
            }

            if (i + 1 >= args.Length)
            {
                error.WriteLine($"{setting.CliOption} needs a number of days.");
                return 1;
            }

            if (!RetentionPolicy.TryParseDays(args[++i], out var days, out var problem))
            {
                error.WriteLine($"{setting.CliOption}: {problem}");
                return 1;
            }

            changes.Add((setting, days));
        }

        try
        {
            var written = RetentionConfigStore.Save(paths, changes);
            foreach (var setting in RetentionSettings.All)
            {
                var value = RetentionConfigStore.Read(paths, setting);
                output.WriteLine($"{setting.Label}: {RetentionConfigStore.Describe(value.Days)}{(value.IsSet && value.Warning is null ? string.Empty : " (default)")}");
                foreach (var warning in new[] { value.Warning, RetentionConfigStore.EnvironmentOverrideWarning(setting) })
                {
                    if (warning is not null)
                        error.WriteLine($"warning: {warning}");
                }
            }

            if (written > 0)
                output.WriteLine(RetentionConfigStore.Applied);

            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException)
        {
            error.WriteLine($"Could not use netclaw.json: {ex.Message}");
            return 1;
        }
    }

    private static int WriteHelp(TextWriter output)
    {
        output.WriteLine("Usage: netclaw config retention [options]");
        output.WriteLine();
        output.WriteLine("Show how long the daemon keeps data, or change it. With no option, shows the current settings.");
        output.WriteLine();
        output.WriteLine("Options:");
        foreach (var setting in RetentionSettings.All)
            output.WriteLine($"  {setting.CliOption} <days>   Days to keep {setting.Label.ToLowerInvariant()} (default {setting.DefaultDays}). 0 keeps them forever.");
        output.WriteLine();
        output.WriteLine("Example:");
        output.WriteLine("  netclaw config retention --logs-days 30");
        return 0;
    }
}

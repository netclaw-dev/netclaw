// -----------------------------------------------------------------------
// <copyright file="ConfigCommand.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Cli.Config;

internal static class ConfigCommand
{
    internal const string MissingConfigMessage = "No configuration found. Run `netclaw init` first.";

    public static int Run(string[] args, NetclawPaths paths, TextWriter? output = null, TextWriter? error = null)
    {
        var writer = output ?? Console.Out;
        var errorWriter = error ?? Console.Error;

        if (args.Length > 1 && CliArgsParser.IsHelpToken(args[1]))
            return WriteHelp(writer);

        if (args.Length > 1)
        {
            writer.WriteLine("Usage: netclaw config");
            writer.WriteLine("Run `netclaw config --help` for details.");
            return 1;
        }

        if (!File.Exists(paths.NetclawConfigPath))
        {
            errorWriter.WriteLine(MissingConfigMessage);
            return 1;
        }

        return 0;
    }

    private static int WriteHelp(TextWriter writer)
    {
        writer.WriteLine("Usage: netclaw config");
        writer.WriteLine();
        writer.WriteLine("Launch the main post-install settings dashboard.");
        writer.WriteLine("Use `netclaw init` for bootstrap setup on a new install.");
        return 0;
    }
}

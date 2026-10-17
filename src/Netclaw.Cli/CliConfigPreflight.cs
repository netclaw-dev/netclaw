// -----------------------------------------------------------------------
// <copyright file="CliConfigPreflight.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Config;
using Netclaw.Cli.Json;
using Netclaw.Configuration;

namespace Netclaw.Cli;

internal static class CliConfigPreflight
{
    public const string MissingConfigMessage = "daemon not configured - please run netclaw init";

    /// <summary>True when the command line asks for help, which never needs the config.</summary>
    public static bool IsHelpRequest(string[] args)
        => (args.Length > 1 && CliArgsParser.IsHelpToken(args[1]))
           || CliArgsParser.HasTrailingHelpToken(args, 1, includeBareHelp: false);

    /// <summary>Help, doctor and status still run when the config cannot be loaded.</summary>
    public static bool RunsWithUnreadableConfig(string[] args)
        => IsHelpRequest(args) || (args.Length > 0 && args[0] is "doctor" or "status");

    /// <summary>
    /// The daemon's configuration sources. When <paramref name="lenient"/> is set and a config
    /// file cannot be loaded, only the environment is added, so the caller can run without it;
    /// otherwise the load failure throws and <see cref="TryDescribeLoadFailure"/> reports it.
    /// </summary>
    public static IConfigurationBuilder AddConfigSources(IConfigurationBuilder builder, NetclawPaths paths, bool lenient)
    {
        if (!lenient)
            return builder.AddNetclawDaemonSources(paths);

        // Leave out only the file that cannot be loaded; the other still says where the daemon is.
        foreach (var file in new[] { paths.NetclawConfigPath, paths.SecretsPath, paths.OAuthStatePath })
        {
            if (!TryDescribeUnreadableFile(paths, file, out _, out _))
                builder.AddNetclawConfigFile(paths, file);
        }

        return builder.AddEnvironmentVariables("NETCLAW_");
    }

    /// <summary>
    /// Loads each config file with the provider the daemon uses and, for the first one that
    /// fails, returns one line naming the file and the reason, plus what to do about it.
    /// </summary>
    public static bool TryDescribeUnreadableConfig(NetclawPaths paths, out string message, out string fix)
    {
        foreach (var file in new[] { paths.NetclawConfigPath, paths.SecretsPath, paths.OAuthStatePath })
        {
            if (TryDescribeUnreadableFile(paths, file, out message, out fix))
                return true;
        }

        message = fix = string.Empty;
        return false;
    }

    private static bool TryDescribeUnreadableFile(NetclawPaths paths, string file, out string message, out string fix)
    {
        var name = Path.GetFileName(file);
        if (Directory.Exists(file))
        {
            message = $"Couldn't read {name}: it is a directory.";
            fix = $"Move {file} aside and restore the file.";
            return true;
        }

        try
        {
            new ConfigurationBuilder().AddNetclawConfigFile(paths, file).Build();
        }
        catch (Exception ex)
        {
            message = DaemonConfigurationSources.DescribeLoadFailure(name, ex);
            fix = Unwrap(ex) switch
            {
                UnauthorizedAccessException => $"Fix the permissions on {file}.",
                FileNotFoundException or DirectoryNotFoundException => $"{file} is a link to a file that is missing: restore the target or remove the link.",
                _ when string.Equals(file, paths.OAuthStatePath, StringComparison.Ordinal)
                    => $"Delete {file}. Then run `netclaw doctor --fix` if netclaw.json still has an OAuthTokenExpiry key. That command copies each date back. "
                        + "Otherwise sign in again with `netclaw provider` for each OAuth provider. A github-copilot provider with no expiry date does not refresh.",
                _ => $"Fix the JSON in {file} (a duplicate key, or a file that is empty or cut off), or restore it from a backup.",
            };
            return true;
        }

        message = fix = string.Empty;
        return false;
    }

    /// <summary>
    /// A file the daemon reads but the CLI cannot edit: it has comments or trailing commas, and
    /// writing it back would drop them.
    /// </summary>
    public static bool TryDescribeUneditableConfig(NetclawPaths paths, out string message, bool secretsFirst = false)
    {
        foreach (var file in secretsFirst ? new[] { paths.SecretsPath, paths.NetclawConfigPath } : new[] { paths.NetclawConfigPath, paths.SecretsPath })
        {
            try
            {
                if (File.Exists(file))
                    using (JsonDocument.Parse(File.ReadAllText(file))) { }
            }
            catch (JsonException)
            {
                message = $"Couldn't read {Path.GetFileName(file)}: it has comments or trailing commas, which the CLI would drop when it writes the file. Remove them and retry.";
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // an unreadable file is reported by TryDescribeUnreadableConfig
            }
        }

        message = string.Empty;
        return false;
    }

    /// <summary>
    /// Turns an exception that escaped a command into the one line to print when the cause is
    /// the config: the provider failing to load a file, a comment the CLI cannot write around,
    /// or a config directory that cannot be created. Any other exception is not handled here.
    /// </summary>
    public static bool TryDescribeLoadFailure(Exception exception, NetclawPaths paths, out string message)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is NetclawDirectoryInitializationException { Failures.Count: > 0 } directory)
            {
                var failure = directory.Failures[0];
                message = $"Couldn't read {failure.DirectoryPath}: {FirstLine(failure.Exception.Message)}";
                return true;
            }
        }

        // The files as they are now: this names the right one even when the exception does not.
        if (TryDescribeUnreadableConfig(paths, out message, out _))
            return true;

        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is InvalidDataException or FormatException
                && ex.Message.StartsWith("Failed to load configuration from file", StringComparison.Ordinal))
            {
                // The file changed between the check and the load.
                message = DaemonConfigurationSources.DescribeLoadFailure(ex);
                return true;
            }
        }

        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is JsonException)
                return TryDescribeUneditableConfig(paths, out message, secretsFirst: exception.ToString().Contains("Secrets", StringComparison.Ordinal));
        }

        message = string.Empty;
        return false;
    }

    private static Exception Unwrap(Exception ex)
    {
        while (ex is InvalidDataException or FormatException && ex.InnerException is { } inner)
            ex = inner;
        return ex;
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }

    public static bool TryWriteMissingChatConfig(
        NetclawPaths paths,
        string mode,
        bool chatJsonOutput,
        TextWriter writer,
        out int exitCode)
    {
        if (mode is not ("chat" or "headless"))
        {
            exitCode = 0;
            return false;
        }

        return TryWriteMissingConfig(
            paths,
            jsonOutput: mode == "headless" && chatJsonOutput,
            writer,
            out exitCode);
    }

    public static bool TryWriteMissingConfig(
        NetclawPaths paths,
        bool jsonOutput,
        TextWriter writer,
        out int exitCode)
    {
        if (File.Exists(paths.NetclawConfigPath))
        {
            exitCode = 0;
            return false;
        }

        // A missing local daemon config only means "not initialized" when THIS
        // machine hosts the daemon. When the caller explicitly targets a remote
        // daemon (NETCLAW_DAEMON_ENDPOINT) or this install is a paired client
        // (client.json endpoint), the daemon's configuration lives on the
        // daemon host — blocking here regressed paired/remote clients and the
        // containerized eval harness after #1540. These are exactly the
        // endpoint sources DaemonApi.ResolveEndpoint prefers over the local
        // daemon config.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NETCLAW_DAEMON_ENDPOINT"))
            || ClientConfigFile.ReadEndpoint(paths) is not null)
        {
            exitCode = 0;
            return false;
        }

        if (jsonOutput)
        {
            var node = new JsonObject
            {
                ["overall"] = "not-configured",
                ["message"] = MissingConfigMessage,
            };
            writer.WriteLine(node.ToJsonString(JsonDefaults.Indented));
        }
        else
        {
            writer.WriteLine(MissingConfigMessage);
        }

        exitCode = 1;
        return true;
    }
}

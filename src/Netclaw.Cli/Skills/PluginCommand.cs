// -----------------------------------------------------------------------
// <copyright file="PluginCommand.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Text.Json;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Json;
using Netclaw.Configuration;

namespace Netclaw.Cli.Skills;

internal static class PluginCommand
{
    public static async Task<int> RunAsync(
        string[] args,
        DaemonApi? daemonApi,
        TimeProvider timeProvider,
        TextReader input,
        TextWriter output)
    {
        var action = args.Length > 1 ? args[1] : "help";
        if (action is "help" or "-h" or "--help")
            return WriteHelp(output);
        if (daemonApi is null)
        {
            output.WriteLine("Daemon unavailable: the daemon API is not configured.");
            return 1;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            return action switch
            {
                "install" => await InstallAsync(args, daemonApi, timeProvider, input, output, cancellation.Token),
                "list" => args.Length is 2 or 3 && (args.Length == 2 || args[2] == "--json")
                    ? await ListAsync(daemonApi, args.Length == 3, output, cancellation.Token)
                    : WriteListUsage(output),
                "update" => await UpdateAsync(args, daemonApi, input, output, cancellation.Token),
                "enable" => await SetEnabledAsync(args, true, daemonApi, timeProvider, input, output, cancellation.Token),
                "disable" => await SetEnabledAsync(args, false, daemonApi, timeProvider, input, output, cancellation.Token),
                "remove" => await RemoveAsync(args, daemonApi, timeProvider, input, output, cancellation.Token),
                _ => WriteUnknownAction(action, output),
            };
        }
        catch (DaemonProblemException ex)
        {
            output.WriteLine($"Plugin command failed: {ex.Message}");
            return 1;
        }
        catch (HttpRequestException ex)
        {
            output.WriteLine(ex.StatusCode is null
                ? $"Plugin command failed: could not reach the daemon ({ex.Message})."
                : $"Plugin command failed: the daemon returned HTTP {(int)ex.StatusCode}.");
            return 1;
        }
        catch (OperationCanceledException)
        {
            output.WriteLine("Plugin command canceled.");
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteLine($"Plugin command failed: {ex.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<int> InstallAsync(
        string[] args,
        DaemonApi api,
        TimeProvider timeProvider,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (!TryParseInstall(args, output, out var request, out var confirmed, out var errorExitCode))
            return errorExitCode;
        if (!await ConfirmAsync(
                confirmed,
                $"Configure plugin from '{request.Repository}'? [y/N]: ",
                input,
                output,
                cancellationToken))
            return 0;

        var response = await api.InstallPluginAsync(request, cancellationToken);
        if (response?.Plugin is null || string.IsNullOrWhiteSpace(response.Plugin.SourceId))
        {
            output.WriteLine("Plugin install failed: the daemon returned an unreadable result.");
            return 1;
        }

        output.WriteLine($"Configured plugin '{response.Plugin.SourceId}'.");

        return await ApplyAndVerifyAsync(
            api,
            timeProvider,
            response.RestartGeneration,
            response.Plugin.SourceId,
            ManagedPluginApi.PluginStatus.Installed,
            output,
            cancellationToken);
    }

    private static async Task<int> ListAsync(
        DaemonApi api,
        bool json,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var response = await api.ListPluginsAsync(cancellationToken);
        if (response?.Plugins is null)
        {
            output.WriteLine("Plugin list failed: the daemon returned an unreadable result.");
            return 1;
        }
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(response, JsonDefaults.Api));
            return 0;
        }
        if (response.Plugins.Count == 0)
        {
            output.WriteLine("No managed skill plugins.");
            return 0;
        }

        output.WriteLine($"{"SOURCE ID",-24}  {"MANIFEST",-24}  {"STATUS",-14}  {"VERSION",-14}  REFERENCE");
        foreach (var plugin in response.Plugins)
        {
            var version = SafeText(plugin.InstalledVersion ?? "-", 64);
            var manifestName = SafeText(plugin.ManifestName ?? "-", 64);
            var reference = plugin.ReferenceKind == ManagedPluginReferenceKind.Commit
                ? plugin.Reference[..Math.Min(12, plugin.Reference.Length)]
                : plugin.Reference;
            output.WriteLine(
                $"{plugin.SourceId,-24}  {manifestName,-24}  {StatusText(plugin.Status),-14}  {version,-14}  {plugin.ReferenceKind}:{reference}");
        }
        return 0;
    }

    private static async Task<int> SetEnabledAsync(
        string[] args,
        bool enabled,
        DaemonApi api,
        TimeProvider timeProvider,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (!TryReadName(
                args, enabled ? "enable" : "disable", output, out var name, out var confirmed, out var errorExitCode))
        {
            return errorExitCode;
        }
        if (!await ConfirmAsync(
                confirmed,
                $"{(enabled ? "Enable" : "Disable")} plugin '{name}'? [y/N]: ",
                input,
                output,
                cancellationToken))
            return 0;

        var response = await api.SetPluginEnabledAsync(name, enabled, cancellationToken);
        if (response is null)
        {
            output.WriteLine("Plugin change failed: the daemon returned an unreadable result.");
            return 1;
        }
        if (!response.Changed)
        {
            var plugins = await api.ListPluginsAsync(cancellationToken);
            var plugin = plugins?.Plugins.FirstOrDefault(
                item => string.Equals(item.SourceId, name, StringComparison.OrdinalIgnoreCase));
            if (plugin is null)
            {
                output.WriteLine($"Plugin '{name}' state could not be verified.");
                return 1;
            }

            if (plugin.Enabled != enabled)
            {
                if (!await WaitForRestartAsync(
                        api,
                        timeProvider,
                        response.RestartGeneration,
                        output,
                        cancellationToken))
                {
                    return 1;
                }
            }
            else if (!enabled || plugin.Status == ManagedPluginApi.PluginStatus.Installed)
            {
                output.WriteLine(enabled
                    ? $"Plugin '{name}' is already enabled."
                    : $"Plugin '{name}' is already disabled.");
                return 0;
            }

            return await SyncAndVerifyAsync(
                api,
                name,
                enabled ? ManagedPluginApi.PluginStatus.Installed : ManagedPluginApi.PluginStatus.Disabled,
                output,
                cancellationToken);
        }

        return await ApplyAndVerifyAsync(
            api,
            timeProvider,
            response.RestartGeneration,
            name,
            enabled ? ManagedPluginApi.PluginStatus.Installed : ManagedPluginApi.PluginStatus.Disabled,
            output,
            cancellationToken);
    }

    private static async Task<int> UpdateAsync(
        string[] args,
        DaemonApi api,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (!TryParseUpdate(
                args,
                output,
                out var sourceId,
                out var updateAll,
                out var retryRejected,
                out var confirmed,
                out var errorExitCode))
        {
            return errorExitCode;
        }
        var target = updateAll ? "all plugins" : $"plugin '{sourceId}'";
        if (!await ConfirmAsync(
                confirmed,
                $"Update {target}? [y/N]: ",
                input,
                output,
                cancellationToken))
        {
            return 0;
        }

        var list = await api.ListPluginsAsync(cancellationToken);
        if (list?.Plugins is null)
        {
            output.WriteLine("Plugin update failed: the daemon returned an unreadable plugin list.");
            return 1;
        }
        var targets = updateAll
            ? list.Plugins
            : list.Plugins.Where(
                plugin => string.Equals(plugin.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (targets.Count == 0)
        {
            output.WriteLine(updateAll
                ? "No managed plugins."
                : $"Plugin source '{sourceId}' does not exist.");
            return updateAll ? 0 : 1;
        }

        var sync = await api.SyncSkillsAsync(cancellationToken, retryRejected);
        if (sync?.Sources is null || sync.Inventory.Succeeded != true)
        {
            output.WriteLine("Plugin update failed: the shared skill sync did not complete.");
            return 1;
        }

        var failed = false;
        foreach (var plugin in targets)
        {
            var result = sync.Sources.FirstOrDefault(
                source => source.SourceKind == SkillSyncResult.GitPluginSourceKind
                    && string.Equals(source.Name, plugin.SourceId, StringComparison.OrdinalIgnoreCase));
            if (result is null)
            {
                output.WriteLine(plugin.Enabled
                    ? $"{plugin.SourceId}: no sync result"
                    : $"{plugin.SourceId}: disabled");
                failed |= plugin.Enabled;
                continue;
            }

            output.WriteLine(
                $"{plugin.SourceId}: changed={result.ChangedCount} unchanged={result.UnchangedCount} rejected={result.RejectedCount} failed={result.FailedCount}");
            WriteNotices(result, output);
            failed |= result.RejectedCount > 0 || result.FailedCount > 0;
        }
        return failed ? 1 : 0;
    }

    private static async Task<int> RemoveAsync(
        string[] args,
        DaemonApi api,
        TimeProvider timeProvider,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (!TryReadName(args, "remove", output, out var name, out var confirmed, out var errorExitCode))
            return errorExitCode;
        if (!await ConfirmAsync(
                confirmed,
                $"Remove plugin '{name}'? [y/N]: ",
                input,
                output,
                cancellationToken))
            return 0;

        var response = await api.RemovePluginAsync(name, cancellationToken);
        if (response is null)
        {
            output.WriteLine("Plugin removal failed: the daemon returned an unreadable result.");
            return 1;
        }
        if (!await WaitForRestartAsync(api, timeProvider, response.RestartGeneration, output, cancellationToken))
            return 1;

        var sync = await api.SyncSkillsAsync(cancellationToken);
        if (sync?.Inventory.Succeeded != true)
        {
            output.WriteLine($"Plugin '{name}' was removed, but the skill inventory refresh failed.");
            return 1;
        }

        var plugins = await api.ListPluginsAsync(cancellationToken);
        if (plugins?.Plugins.Any(item => string.Equals(item.SourceId, name, StringComparison.OrdinalIgnoreCase)) != false)
        {
            output.WriteLine($"Plugin '{name}' removal could not be verified.");
            return 1;
        }

        output.WriteLine($"Removed plugin '{name}'.");
        return 0;
    }

    private static async Task<int> ApplyAndVerifyAsync(
        DaemonApi api,
        TimeProvider timeProvider,
        int generation,
        string name,
        ManagedPluginApi.PluginStatus expectedStatus,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (!await WaitForRestartAsync(api, timeProvider, generation, output, cancellationToken))
            return 1;

        return await SyncAndVerifyAsync(
            api,
            name,
            expectedStatus,
            output,
            cancellationToken);
    }

    private static async Task<int> SyncAndVerifyAsync(
        DaemonApi api,
        string name,
        ManagedPluginApi.PluginStatus expectedStatus,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var sync = await api.SyncSkillsAsync(cancellationToken);
        var source = sync?.Sources.FirstOrDefault(
            item => item.SourceKind == SkillSyncResult.GitPluginSourceKind
                && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        var plugins = await api.ListPluginsAsync(cancellationToken);
        var plugin = plugins?.Plugins.FirstOrDefault(
            item => string.Equals(item.SourceId, name, StringComparison.OrdinalIgnoreCase));
        if (sync?.Inventory.Succeeded != true
            || plugin?.Status != expectedStatus
            || (expectedStatus == ManagedPluginApi.PluginStatus.Installed
                && (source is null || source.FailedCount > 0 || source.RejectedCount > 0)))
        {
            output.WriteLine($"Plugin '{name}' is configured but is not installed.");
            return 1;
        }

        if (source is not null)
            WriteNotices(source, output);

        output.WriteLine(expectedStatus == ManagedPluginApi.PluginStatus.Disabled
            ? $"Disabled plugin '{name}'."
            : $"Installed plugin '{name}' at commit {plugin.InstalledCommit}.");
        return 0;
    }

    private static async Task<bool> WaitForRestartAsync(
        DaemonApi api,
        TimeProvider timeProvider,
        int generation,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        output.WriteLine("Waiting for the daemon to apply the plugin configuration.");
        var ready = await new DaemonRestartWaiter(api, timeProvider)
            .WaitAsync(generation, cancellationToken);
        if (ready)
            return true;

        output.WriteLine("The plugin configuration was saved, but the daemon did not become ready.");
        return false;
    }

    private static bool TryParseInstall(
        string[] args,
        TextWriter output,
        out ManagedPluginApi.InstallRequest request,
        out bool confirmed,
        out int errorExitCode)
    {
        request = null!;
        confirmed = false;
        errorExitCode = 2;
        if (args.Length < 3)
        {
            output.WriteLine("Usage: netclaw plugin install <repository> [options]");
            return false;
        }

        var repository = args[2];
        string? sourceId = null;
        string? format = null;
        string? subdirectory = null;
        string? reference = null;
        var referenceKind = ManagedPluginApi.InstallReferenceKind.DefaultBranch;
        var referenceOptionSeen = false;
        var timeoutSeconds = 60;

        for (var index = 3; index < args.Length; index++)
        {
            var option = args[index];
            if (option is "--yes" or "-y")
            {
                confirmed = true;
                continue;
            }
            if (index + 1 >= args.Length)
            {
                output.WriteLine($"Option '{option}' requires a value.");
                return false;
            }
            var value = args[++index];
            switch (option)
            {
                case "--id": sourceId = value; break;
                case "--format": format = value; break;
                case "--subdirectory": subdirectory = value; break;
                case "--timeout-seconds" when int.TryParse(value, out timeoutSeconds): break;
                case "--timeout-seconds":
                    output.WriteLine("The plugin timeout must be an integer.");
                    return false;
                case "--branch":
                case "--tag":
                case "--commit":
                    if (referenceOptionSeen)
                    {
                        output.WriteLine("Use only one of --branch, --tag, or --commit.");
                        return false;
                    }
                    referenceOptionSeen = true;
                    reference = value;
                    referenceKind = option switch
                    {
                        "--branch" => ManagedPluginApi.InstallReferenceKind.Branch,
                        "--tag" => ManagedPluginApi.InstallReferenceKind.Tag,
                        _ => ManagedPluginApi.InstallReferenceKind.Commit,
                    };
                    break;
                default:
                    output.WriteLine($"Unknown plugin install option '{option}'.");
                    return false;
            }
        }

        if (!ManagedPluginSourceValidator.TryNormalizeRepository(repository, out _, out var repositoryError))
        {
            output.WriteLine(repositoryError);
            errorExitCode = 1;
            return false;
        }
        if (sourceId is not null && !ManagedPluginSourceValidator.TryValidateId(sourceId, out var idError))
        {
            output.WriteLine(idError);
            errorExitCode = 1;
            return false;
        }
        if (!ManagedPluginSourceValidator.TryNormalizeRelativePath(
                subdirectory,
                allowEmpty: true,
                out _,
                out var pathError))
        {
            output.WriteLine(pathError);
            errorExitCode = 1;
            return false;
        }
        if (timeoutSeconds is < 1 or > 300)
        {
            output.WriteLine("The plugin timeout must be from 1 through 300 seconds.");
            errorExitCode = 1;
            return false;
        }

        request = new ManagedPluginApi.InstallRequest
        {
            Repository = repository,
            SourceId = sourceId,
            Format = format ?? ManagedPluginSourceValidator.AutoFormat,
            Subdirectory = subdirectory,
            ReferenceKind = referenceKind,
            Reference = reference,
            TimeoutSeconds = timeoutSeconds,
        };
        return true;
    }

    private static bool TryReadName(
        string[] args,
        string action,
        TextWriter output,
        out string name,
        out bool confirmed,
        out int errorExitCode)
    {
        name = args.Length > 2 ? args[2] : string.Empty;
        confirmed = args.Length == 4 && args[3] is "--yes" or "-y";
        errorExitCode = 2;
        if (args.Length != 3 && !confirmed)
        {
            output.WriteLine($"Usage: netclaw plugin {action} <source-id> [--yes]");
            return false;
        }
        if (!ManagedPluginSourceValidator.TryValidateId(name, out var error))
        {
            output.WriteLine(error);
            errorExitCode = 1;
            return false;
        }
        return true;
    }

    private static bool TryParseUpdate(
        string[] args,
        TextWriter output,
        out string? sourceId,
        out bool updateAll,
        out bool retryRejected,
        out bool confirmed,
        out int errorExitCode)
    {
        sourceId = null;
        updateAll = false;
        retryRejected = false;
        confirmed = false;
        errorExitCode = 2;
        foreach (var value in args.Skip(2))
        {
            switch (value)
            {
                case "--all": updateAll = true; break;
                case "--retry-rejected": retryRejected = true; break;
                case "--yes" or "-y": confirmed = true; break;
                case string when !value.StartsWith("-", StringComparison.Ordinal) && sourceId is null:
                    sourceId = value;
                    break;
                default:
                    output.WriteLine($"Unknown plugin update option '{value}'.");
                    return false;
            }
        }
        if (updateAll == (sourceId is not null))
        {
            output.WriteLine("Usage: netclaw plugin update <source-id>|--all [--retry-rejected] [--yes]");
            return false;
        }
        if (sourceId is not null && !ManagedPluginSourceValidator.TryValidateId(sourceId, out var error))
        {
            output.WriteLine(error);
            errorExitCode = 1;
            return false;
        }
        return true;
    }

    private static async Task<bool> ConfirmAsync(
        bool confirmed,
        string prompt,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (confirmed)
            return true;

        output.Write(prompt);
        var response = (await input.ReadLineAsync(cancellationToken))?.Trim();
        if (response is "y" or "Y" or "yes" or "Yes" or "YES")
            return true;

        output.WriteLine("Cancelled.");
        return false;
    }

    private static string StatusText(ManagedPluginApi.PluginStatus status) => status switch
    {
        ManagedPluginApi.PluginStatus.NotInstalled => "not-installed",
        _ => status.ToString().ToLowerInvariant(),
    };

    private static void WriteNotices(SkillSyncResult.SourceRow source, TextWriter output)
    {
        foreach (var notice in source.Notices)
            output.WriteLine($"Notice: {SafeText(notice, 512)}");
    }

    private static string SafeText(string value, int maximumLength)
    {
        var safe = new string(value.Select(
            static character => char.IsControl(character)
                || char.GetUnicodeCategory(character) == UnicodeCategory.Format ? ' ' : character).ToArray()).Trim();
        return safe.Length <= maximumLength ? safe : safe[..maximumLength];
    }

    private static int WriteHelp(TextWriter output)
    {
        output.WriteLine("Usage: netclaw plugin <action>");
        output.WriteLine();
        output.WriteLine("Actions:");
        output.WriteLine("  install <repository> [options]    Validate and install a public GitHub plugin");
        output.WriteLine("  list [--json]                     List managed plugins");
        output.WriteLine("  update <source-id>|--all          Run the shared plugin sync");
        output.WriteLine("  enable <source-id>                Enable a plugin");
        output.WriteLine("  disable <source-id>               Disable a plugin");
        output.WriteLine("  remove <source-id>                Remove a plugin");
        output.WriteLine();
        output.WriteLine("Install options: --id, --format, --subdirectory, --branch, --tag,");
        output.WriteLine("                 --commit, --timeout-seconds, --yes");
        output.WriteLine("Mutation options: --yes skips the confirmation prompt.");
        output.WriteLine("All plugin actions need the running daemon.");
        return 0;
    }

    private static int WriteUnknownAction(string action, TextWriter output)
    {
        output.WriteLine($"Unknown plugin action '{action}'.");
        WriteHelp(output);
        return 2;
    }

    private static int WriteListUsage(TextWriter output)
    {
        output.WriteLine("Usage: netclaw plugin list [--json]");
        return 2;
    }
}

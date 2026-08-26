// -----------------------------------------------------------------------
// <copyright file="UpdateCommand.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.IO.Compression;
using System.Security.Cryptography;
using Netclaw.Cli.Config;
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Netclaw.Configuration.Feeds;

namespace Netclaw.Cli.Update;

/// <summary>
/// Handles <c>netclaw update</c> CLI subcommands: check, download + install.
/// </summary>
internal static class UpdateCommand
{
    internal static Func<HttpMessageHandler>? TestHttpMessageHandlerFactory { get; set; }
    internal static Func<NetclawPaths, IDaemonProcessLifecycle>? TestDaemonProcessManagerFactory { get; set; }
    internal static Func<SystemdUserService>? TestSystemdUserServiceFactory { get; set; }

    internal static bool ShouldRunStartupUpdateCheck(string mode, string[] args)
    {
        switch (mode)
        {
            case "init":
            case "update":
            case "secrets":
            case "daemon":
            case "chat":
            case "sessions":
            case "headless":
                return false;
            case "stats":
                return !args.Contains("--tui", StringComparer.Ordinal);
            case "mcp":
                var mcpSubcommand = args.Length > 1 ? args[1] : "help";
                return !((mcpSubcommand is "tools" or "permissions") && args.Length <= 2);
            case "provider":
            case "model":
                return args.Length > 1;
            case "approvals":
                return !(args.Length == 1 || (args.Length > 1 && args[1] is "tui"));
            case "reminder":
                return !(args.Length > 1 && args[1] is ("ui" or "tui"));
            default:
                return true;
        }
    }

    public static async Task<int> RunAsync(string[] args, NetclawPaths paths, bool selfUpdateDisabled = false, UpdateChannel channel = UpdateChannel.Stable)
    {
        var checkOnly = false;
        var force = false;
        UpdateChannel? channelOverride = null;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--check":
                    checkOnly = true;
                    break;
                case "--force":
                    force = true;
                    break;
                case "--channel":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("--channel requires a value: stable or beta.");
                        WriteHelp();
                        return 1;
                    }
                    if (!DaemonConfig.TryParseUpdateChannel(args[++i], out var parsedChannel))
                    {
                        Console.Error.WriteLine($"Unknown channel: '{args[i]}'. Valid values: stable, beta.");
                        WriteHelp();
                        return 1;
                    }
                    channelOverride = parsedChannel;
                    break;
                case "-h" or "--help" or "help":
                    WriteHelp();
                    return 0;
                default:
                    Console.WriteLine($"Unknown option: {args[i]}");
                    WriteHelp();
                    return 1;
            }
        }

        // Specifying --channel selects the channel this run evaluates against.
        // Outside of --check it also switches the channel: the value is written
        // back to netclaw.json so the daemon's background check and future runs
        // follow it. --check is a read-only verb, so it previews the requested
        // channel without persisting anything.
        if (channelOverride is { } overrideChannel)
        {
            channel = overrideChannel;

            if (checkOnly)
            {
                Console.WriteLine($"Checking '{channel.ToWireValue()}' channel (run without --check to switch).");
            }
            else
            {
                try
                {
                    PersistChannel(paths, channel);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error: could not save update channel to {paths.NetclawConfigPath}: {ex.Message}");
                    return 1;
                }

                Console.WriteLine($"Update channel set to '{channel.ToWireValue()}' ({paths.NetclawConfigPath}).");
            }
        }

        var currentVersion = BuildInfo.FullVersion;

        using var httpClient = TestHttpMessageHandlerFactory is { } createHandler
            ? new HttpClient(createHandler())
            : new HttpClient();

        // Fetch manifest with signature verification
        var fetchResult = await UpdateCheckService.FetchVerifiedManifestAsync(
            httpClient);

        if (!fetchResult.IsSuccess)
        {
            if (fetchResult.Status is ManifestFetchStatus.SignatureFailure or ManifestFetchStatus.PlatformUnavailable)
            {
                Console.Error.WriteLine($"Error: {fetchResult.ErrorMessage}");
                Console.Error.WriteLine(fetchResult.Status == ManifestFetchStatus.PlatformUnavailable
                    ? "The update manifest could not be verified because signature verification is unavailable on this platform."
                    : "The update manifest could not be verified. This may indicate tampering.");
                Console.Error.WriteLine("If this persists, report the issue at https://github.com/netclaw-dev/netclaw/issues");
                return 1;
            }

            // Network failure — could be transient
            Console.Error.WriteLine($"Could not check for updates: {fetchResult.ErrorMessage}");
            return 1;
        }

        var result = UpdateCheckService.EvaluateManifest(fetchResult.Manifest!, currentVersion, channel);

        if (!result.IsUpdateAvailable)
        {
            Console.WriteLine($"Netclaw is up to date (v{result.CurrentVersion}).");
            return 0;
        }

        Console.WriteLine($"Update available: v{result.CurrentVersion} → v{result.LatestVersion}");
        if (result.ReleaseNotesUrl is not null)
            Console.WriteLine($"Release notes: {result.ReleaseNotesUrl}");

        if (checkOnly)
            return 0;

        if (selfUpdateDisabled)
        {
            Console.WriteLine();
            Console.WriteLine("Self-update is disabled (Daemon.DisableSelfUpdate=true).");
            Console.WriteLine("Pull a newer container image to upgrade.");
            return 1;
        }

        // Show what will be downloaded
        Console.WriteLine();
        foreach (var asset in result.MatchingAssets)
        {
            var sizeMb = asset.SizeBytes / (1024.0 * 1024.0);
            Console.WriteLine($"  {asset.Component} ({asset.Rid}) — {sizeMb:F1} MB");
        }

        if (!force)
        {
            Console.Write("\nProceed with update? [y/N]: ");
            var response = Console.ReadLine();
            if (!string.Equals(response, "y", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(response, "yes", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Update cancelled.");
                return 0;
            }
        }

        return await PerformUpdateAsync(result, paths, httpClient);
    }

    private static async Task<int> PerformUpdateAsync(
        UpdateCheckResult result, NetclawPaths paths, HttpClient httpClient)
    {
        var installDir = GetInstallDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Download and verify each asset
            var extractedPaths = new Dictionary<string, string>();
            foreach (var asset in result.MatchingAssets)
            {
                Console.Write($"Downloading {asset.Component}...");
                var archivePath = Path.Combine(tempDir, Path.GetFileName(new Uri(asset.Url).AbsolutePath));

                if (!await DownloadAndVerifyAsync(httpClient, asset, archivePath))
                    return 1;

                Console.WriteLine(" verified.");

                // Extract
                var extractDir = Path.Combine(tempDir, asset.Component);
                Directory.CreateDirectory(extractDir);

                if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                    await ExtractTarGzAsync(archivePath, extractDir);
                else if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    ZipFile.ExtractToDirectory(archivePath, extractDir);

                extractedPaths[asset.Component] = extractDir;
            }

            // Check if daemon is running (we'll need to restart it)
            var manager = CreateDaemonProcessManager(paths);
            var systemdService = CreateSystemdUserService();
            var daemonStatus = manager.GetStatus();
            var stopResult = UpdateDaemonStopResult.Succeeded(
                UpdateDaemonOwner.None,
                daemonStatus.Message);

            if (daemonStatus.IsRunning)
            {
                Console.Write("Stopping daemon...");
                stopResult = await StopDaemonForUpdateAsync(manager, systemdService, daemonStatus);
                if (!stopResult.Success)
                {
                    Console.WriteLine($" failed: {stopResult.Message}");
                    Console.WriteLine("Update aborted. Stop the daemon manually, fix service state, and retry.");
                    return 1;
                }
                Console.WriteLine(" done.");
            }

            // Replace binaries
            Console.Write("Installing...");
            Directory.CreateDirectory(installDir);

            foreach (var (component, extractDir) in extractedPaths)
            {
                var binaryName = OperatingSystem.IsWindows()
                    ? $"{component}.exe"
                    : component;

                var sourcePath = FindBinaryInExtracted(extractDir, binaryName);
                if (sourcePath is null)
                {
                    Console.WriteLine($"\n  Could not find {binaryName} in downloaded archive.");
                    return 1;
                }

                var targetPath = Path.Combine(installDir, binaryName);
                var backupPath = targetPath + ".backup";

                try
                {
                    // Swap with automatic rollback: a failed swap restores the
                    // previous binary so the install directory is never left
                    // without an executable (which would brick the CLI).
                    SwapBinaryIntoPlace(sourcePath, targetPath, backupPath);
                }
                catch (Exception ex)
                {
                    var targetRestored = File.Exists(targetPath);
                    Console.WriteLine($"\n  Failed to replace {binaryName}: {ex.Message}");
                    if (targetRestored)
                    {
                        Console.WriteLine("  The previous binary was restored. The daemon is stopped; start it with 'netclaw daemon start'.");
                    }
                    else
                    {
                        Console.WriteLine($"  The install directory is missing {binaryName}. Restore it from {binaryName}.backup, then start the daemon with 'netclaw daemon start'.");
                    }
                    return 1;
                }

                // Set executable permission on Unix
                if (!OperatingSystem.IsWindows())
                    SetExecutable(targetPath);
            }

            Console.WriteLine(" done.");

            // Restart daemon if it was running
            if (stopResult.ShouldRestart)
            {
                Console.Write("Restarting daemon...");
                var startResult = await StartDaemonAfterUpdateAsync(stopResult.Owner, manager, systemdService);
                if (!startResult.Success)
                {
                    Console.WriteLine($" failed: {startResult.Message}");
                    Console.WriteLine("Update installed, but daemon restart failed. Start the daemon manually and check `netclaw status`.");
                    return 1;
                }

                Console.WriteLine(" done.");
            }

            // Clean up backup files. The backup of the currently running CLI
            // binary is the image this process still executes from; on Windows
            // DeleteFile on a running image fails with
            // UnauthorizedAccessException. Leave it — the install step deletes
            // stale backups before moving the new binary on the next update.
            // A leftover backup must never turn a successful update into a
            // fatal error, so any other delete failure only warns.
            var runningBackupPath = Environment.ProcessPath is { } processPath
                ? processPath + ".backup"
                : null;
            foreach (var (component, _) in extractedPaths)
            {
                var binaryName = OperatingSystem.IsWindows()
                    ? $"{component}.exe"
                    : component;
                var backupPath = Path.Combine(installDir, binaryName + ".backup");
                CleanupBackupFile(backupPath, runningBackupPath, OperatingSystem.IsWindows());
            }

            Console.WriteLine($"\nUpdated to v{result.LatestVersion}.");
            return 0;
        }
        finally
        {
            // Clean up temp directory
            try { Directory.Delete(tempDir, recursive: true); }
            catch (Exception ex) { Console.Error.WriteLine($"warn: temp cleanup failed: {ex.Message}"); }
        }
    }

    internal static async Task<UpdateDaemonStopResult> StopDaemonForUpdateAsync(
        IDaemonProcessLifecycle manager,
        SystemdUserService systemdService,
        DaemonStatus? daemonStatus = null)
    {
        daemonStatus ??= manager.GetStatus();
        if (!daemonStatus.IsRunning)
        {
            return UpdateDaemonStopResult.Succeeded(
                UpdateDaemonOwner.None,
                daemonStatus.Message);
        }

        var systemdOwnership = await systemdService.GetOwnershipAsync();
        switch (systemdOwnership.Kind)
        {
            case SystemdUserServiceOwnershipKind.Unknown:
                return UpdateDaemonStopResult.Failed(
                    UpdateDaemonOwner.None,
                    $"Could not determine whether systemd owns the daemon lifecycle: {systemdOwnership.Message}");

            case SystemdUserServiceOwnershipKind.Managed:
            {
                var systemdStop = await systemdService.StopAsync();
                if (!systemdStop.Success)
                {
                    return UpdateDaemonStopResult.Failed(
                        UpdateDaemonOwner.SystemdUserService,
                        $"systemd stop failed: {systemdStop.Message}");
                }

                var remainingStatus = manager.GetStatus();
                if (remainingStatus.IsRunning)
                {
                    var detachedStop = await manager.StopAsync("update", CancellationToken.None);
                    if (!detachedStop.Success)
                    {
                        return UpdateDaemonStopResult.Failed(
                            UpdateDaemonOwner.SystemdUserService,
                            "systemd service stopped, but a detached daemon is still running and "
                            + $"could not be stopped: {detachedStop.Message}");
                    }
                }

                return UpdateDaemonStopResult.Succeeded(
                    UpdateDaemonOwner.SystemdUserService,
                    systemdStop.Message);
            }

            case SystemdUserServiceOwnershipKind.Unmanaged:
            default:
            {
                var detachedStop = await manager.StopAsync("update", CancellationToken.None);
                return detachedStop.Success
                    ? UpdateDaemonStopResult.Succeeded(UpdateDaemonOwner.DetachedProcess, detachedStop.Message)
                    : UpdateDaemonStopResult.Failed(UpdateDaemonOwner.DetachedProcess, detachedStop.Message);
            }
        }
    }

    internal static async Task<DaemonResult> StartDaemonAfterUpdateAsync(
        UpdateDaemonOwner owner,
        IDaemonProcessLifecycle manager,
        SystemdUserService systemdService)
    {
        return owner switch
        {
            UpdateDaemonOwner.None => new DaemonResult(true, "Daemon was not running."),
            UpdateDaemonOwner.SystemdUserService => await systemdService.StartAsync(),
            UpdateDaemonOwner.DetachedProcess => manager.Start(),
            _ => new DaemonResult(false, $"Unknown daemon lifecycle owner: {owner}.")
        };
    }

    private static IDaemonProcessLifecycle CreateDaemonProcessManager(NetclawPaths paths)
    {
        return TestDaemonProcessManagerFactory?.Invoke(paths)
            ?? new DaemonProcessLifecycle(new DaemonManager(paths, TimeProvider.System));
    }

    private static SystemdUserService CreateSystemdUserService()
    {
        return TestSystemdUserServiceFactory?.Invoke()
            ?? new SystemdUserService();
    }

    private sealed class DaemonProcessLifecycle(DaemonManager manager) : IDaemonProcessLifecycle
    {
        public DaemonStatus GetStatus() => manager.GetStatus();

        public Task<DaemonResult> StopAsync(string reason, CancellationToken cancellationToken)
            => manager.StopAsync(reason, cancellationToken);

        public DaemonResult Start() => manager.Start();
    }

    private static async Task<bool> DownloadAndVerifyAsync(
        HttpClient httpClient, BinaryAsset asset, string archivePath)
    {
        try
        {
            using var response = await httpClient.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = File.Create(archivePath);
            await stream.CopyToAsync(fileStream);
        }
        catch (Exception ex)
        {
            Console.WriteLine($" download failed: {ex.Message}");
            return false;
        }

        // Verify SHA-256
        var hash = await ComputeFileSha256Async(archivePath);
        if (!string.Equals(hash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($" checksum mismatch (expected {asset.Sha256}, got {hash})");
            return false;
        }

        return true;
    }

    private static async Task<string> ComputeFileSha256Async(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexStringLower(hash);
    }

    private static async Task ExtractTarGzAsync(string archivePath, string extractDir)
    {
        // Use tar command — available on all supported platforms (Linux, Windows 10+)
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "tar",
            Arguments = $"xzf \"{archivePath}\" -C \"{extractDir}\"",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var proc = System.Diagnostics.Process.Start(psi)!;
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
        {
            var stderr = await proc.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"tar extraction failed: {stderr}");
        }
    }

    private static string? FindBinaryInExtracted(string extractDir, string binaryName)
    {
        // Binary may be directly in the extract dir or in a subdirectory
        var direct = Path.Combine(extractDir, binaryName);
        if (File.Exists(direct))
            return direct;

        // Search one level deep
        foreach (var dir in Directory.GetDirectories(extractDir))
        {
            var candidate = Path.Combine(dir, binaryName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Replaces <paramref name="targetPath"/> with
    /// <paramref name="sourcePath"/>, preserving the previous binary at
    /// <paramref name="backupPath"/>. On failure the previous binary is
    /// restored to <paramref name="targetPath"/> so a failed swap never
    /// leaves the install directory without an executable.
    /// </summary>
    /// <param name="sourcePath">The new binary to install.</param>
    /// <param name="targetPath">The installed binary to replace.</param>
    /// <param name="backupPath">Where the previous binary is preserved.</param>
    internal static void SwapBinaryIntoPlace(string sourcePath, string targetPath, string backupPath)
    {
        var movedOldToBackup = false;
        try
        {
            if (File.Exists(targetPath))
            {
                if (File.Exists(backupPath))
                    File.Delete(backupPath);
                File.Move(targetPath, backupPath);
                movedOldToBackup = true;
            }

            File.Move(sourcePath, targetPath);
        }
        catch
        {
            // Roll the previous binary back so a failed swap leaves a
            // working binary in place instead of a missing executable.
            if (movedOldToBackup && !File.Exists(targetPath) && File.Exists(backupPath))
            {
                try { File.Move(backupPath, targetPath); }
                catch (Exception rollbackEx)
                {
                    // Best-effort rollback; the original swap failure is
                    // rethrown below and reported to the user.
                    Console.Error.WriteLine($"warn: failed to restore {targetPath} from {backupPath}: {rollbackEx.Message}");
                }
            }
            throw;
        }
    }

    /// <summary>
    /// Deletes a leftover <c>.backup</c> file after a successful update.
    /// On Windows the backup of the currently running image cannot be
    /// deleted — the process still executes from that file, so DeleteFile
    /// fails with <see cref="UnauthorizedAccessException"/>. Such backups are
    /// removed by the install step on the next update, before it renames the
    /// new binary. Any other delete failure only warns; a leftover backup must
    /// never turn a successful update into a fatal error.
    /// </summary>
    /// <param name="backupPath">The backup file to delete.</param>
    /// <param name="runningBackupPath">
    /// The backup path of the currently running process
    /// (<c>Environment.ProcessPath + ".backup"</c>), or <c>null</c> if unknown.
    /// </param>
    /// <param name="isWindows">True when running on Windows.</param>
    internal static void CleanupBackupFile(string backupPath, string? runningBackupPath, bool isWindows)
    {
        if (isWindows
            && runningBackupPath is not null
            && string.Equals(backupPath, runningBackupPath, StringComparison.OrdinalIgnoreCase))
        {
            // Running image — cannot be deleted on Windows.
            return;
        }

        try
        {
            if (File.Exists(backupPath))
                File.Delete(backupPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warn: could not remove backup {backupPath}: {ex.Message}");
        }
    }

    private static string GetInstallDirectory()
    {
        // Use the directory containing the current CLI binary
        var processPath = Environment.ProcessPath;
        if (processPath is not null)
            return Path.GetDirectoryName(processPath)!;

        // Fallback to ~/.netclaw/bin/
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".netclaw", "bin");
    }

    private static void SetExecutable(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "chmod",
                Arguments = $"+x \"{path}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            })?.WaitForExit();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warn: chmod +x failed for {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// Persists the chosen update channel to <c>Daemon.UpdateChannel</c> in
    /// netclaw.json, preserving every other field. Writes the canonical wire
    /// value (<c>stable</c>/<c>beta</c>) so the on-disk config stays schema-valid.
    /// </summary>
    private static void PersistChannel(NetclawPaths paths, UpdateChannel channel)
    {
        var config = ConfigFileHelper.LoadJsonDict(paths.NetclawConfigPath);
        var daemon = ConfigFileHelper.GetOrCreateSection(config, "Daemon");
        daemon["UpdateChannel"] = channel.ToWireValue();
        ConfigFileHelper.WriteConfigFile(paths.NetclawConfigPath, config);
    }

    internal static void WriteHelp()
    {
        Console.WriteLine("Usage: netclaw update [options]");
        Console.WriteLine();
        Console.WriteLine("Check for and install Netclaw updates.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --check              Check for updates without installing");
        Console.WriteLine("  --force              Skip confirmation prompt");
        Console.WriteLine("  --channel <name>     Switch the release channel (saved to netclaw.json): stable or beta");
    }

    /// <summary>
    /// Holds the result of the most recent background update check so the
    /// notice can be emitted at a safe time (after a TUI exits, after a CLI
    /// command finishes writing its own output), instead of from inside the
    /// background task itself — which would corrupt any running TUI.
    /// </summary>
    private static string? _pendingNotice;

    /// <summary>
    /// Quick background update check for CLI startup. Stores a one-line
    /// notification in a static buffer if an update is available; emitted by
    /// <see cref="EmitPendingNoticeIfReady"/> when the program is about to exit.
    /// </summary>
    internal static async Task BackgroundUpdateCheckAsync(bool selfUpdateDisabled = false, UpdateChannel channel = UpdateChannel.Stable)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var httpClient = new HttpClient();
            var result = await UpdateCheckService.CheckForUpdateAsync(
                httpClient, BuildInfo.FullVersion, cts.Token, channel);

            if (result.IsUpdateAvailable)
            {
                var hint = selfUpdateDisabled
                    ? "pull a newer container image to upgrade"
                    : "run 'netclaw update'";
                _pendingNotice =
                    $"Update available: v{result.CurrentVersion} → v{result.LatestVersion} — {hint}";
            }
        }
        catch (Exception ex)
        {
            // A failed background check must never write to stderr mid-TUI;
            // Debug.WriteLine is a no-op in Release and bypasses the alt-screen.
            System.Diagnostics.Debug.WriteLine(
                $"background update check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Emit the buffered update notice (if the background check completed
    /// and an update is available) to stderr. Safe to call from anywhere —
    /// no-op if the check is still in flight or no update was found.
    /// Intended to run after the mode handler returns, when any TUI has
    /// already torn down its alt screen.
    /// </summary>
    internal static void EmitPendingNoticeIfReady()
    {
        var notice = Interlocked.Exchange(ref _pendingNotice, null);
        if (notice is not null)
            Console.Error.WriteLine(notice);
    }
}

internal interface IDaemonProcessLifecycle
{
    DaemonStatus GetStatus();

    Task<DaemonResult> StopAsync(string reason, CancellationToken cancellationToken);

    DaemonResult Start();
}

internal enum UpdateDaemonOwner
{
    None,
    DetachedProcess,
    SystemdUserService
}

internal sealed record UpdateDaemonStopResult(
    bool Success,
    UpdateDaemonOwner Owner,
    string Message)
{
    public bool ShouldRestart => Success && Owner is not UpdateDaemonOwner.None;

    public static UpdateDaemonStopResult Succeeded(UpdateDaemonOwner owner, string message) =>
        new(true, owner, message);

    public static UpdateDaemonStopResult Failed(UpdateDaemonOwner owner, string message) =>
        new(false, owner, message);
}

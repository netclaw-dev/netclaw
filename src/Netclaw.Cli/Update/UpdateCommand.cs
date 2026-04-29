// -----------------------------------------------------------------------
// <copyright file="UpdateCommand.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.IO.Compression;
using System.Security.Cryptography;
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

    public static async Task<int> RunAsync(string[] args, NetclawPaths paths, bool selfUpdateDisabled = false)
    {
        var checkOnly = false;
        var force = false;

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
                case "-h" or "--help" or "help":
                    WriteHelp();
                    return 0;
                default:
                    Console.WriteLine($"Unknown option: {args[i]}");
                    WriteHelp();
                    return 1;
            }
        }

        var currentVersion = BuildInfo.Version;

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
                Console.Error.WriteLine("If this persists, report the issue at https://github.com/stannardlabs/netclaw/issues");
                return 1;
            }

            // Network failure — could be transient
            Console.Error.WriteLine($"Could not check for updates: {fetchResult.ErrorMessage}");
            return 1;
        }

        var result = UpdateCheckService.EvaluateManifest(fetchResult.Manifest!, currentVersion);

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
            var manager = new DaemonManager(paths, TimeProvider.System);
            var daemonStatus = manager.GetStatus();
            var daemonWasRunning = daemonStatus.IsRunning;

            if (daemonWasRunning)
            {
                Console.Write("Stopping daemon...");
                var stopResult = await manager.StopAsync("update");
                if (!stopResult.Success)
                {
                    Console.WriteLine($" failed: {stopResult.Message}");
                    Console.WriteLine("Update aborted. Stop the daemon manually and retry with --force.");
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

                // Backup existing binary
                if (File.Exists(targetPath))
                {
                    if (File.Exists(backupPath))
                        File.Delete(backupPath);
                    File.Move(targetPath, backupPath);
                }

                File.Move(sourcePath, targetPath);

                // Set executable permission on Unix
                if (!OperatingSystem.IsWindows())
                    SetExecutable(targetPath);
            }

            Console.WriteLine(" done.");

            // Restart daemon if it was running
            if (daemonWasRunning)
            {
                Console.Write("Restarting daemon...");
                var startResult = manager.Start();
                Console.WriteLine(startResult.Success ? " done." : $" {startResult.Message}");
            }

            // Clean up backup files
            foreach (var (component, _) in extractedPaths)
            {
                var binaryName = OperatingSystem.IsWindows()
                    ? $"{component}.exe"
                    : component;
                var backupPath = Path.Combine(installDir, binaryName + ".backup");
                if (File.Exists(backupPath))
                    File.Delete(backupPath);
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

    internal static void WriteHelp()
    {
        Console.WriteLine("Usage: netclaw update [options]");
        Console.WriteLine();
        Console.WriteLine("Check for and install Netclaw updates.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --check    Check for updates without installing");
        Console.WriteLine("  --force    Skip confirmation prompt");
    }

    /// <summary>
    /// Quick background update check for CLI startup.
    /// Prints a one-line notification if an update is available.
    /// </summary>
    internal static async Task BackgroundUpdateCheckAsync(bool selfUpdateDisabled = false)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var httpClient = new HttpClient();
            var result = await UpdateCheckService.CheckForUpdateAsync(
                httpClient, BuildInfo.Version, cts.Token);

            if (result.IsUpdateAvailable)
            {
                var hint = selfUpdateDisabled
                    ? "pull a newer container image to upgrade"
                    : "run 'netclaw update'";
                Console.Error.WriteLine(
                    $"Update available: v{result.CurrentVersion} → v{result.LatestVersion} — {hint}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warn: background update check failed: {ex.Message}");
        }
    }
}

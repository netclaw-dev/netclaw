using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Netclaw.Cli.Mcp;

internal interface IBrowserAutomationBootstrapper
{
    Task<BrowserAutomationBootstrapResult> EnsureReadyAsync(string backend, CancellationToken ct = default);
}

internal sealed record BrowserAutomationBootstrapResult(
    bool Success,
    bool NeedsManualAction,
    string Message,
    string? ManualCommand = null);

internal sealed class BrowserAutomationBootstrapper : IBrowserAutomationBootstrapper
{
    public async Task<BrowserAutomationBootstrapResult> EnsureReadyAsync(string backend, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(backend))
        {
            return new BrowserAutomationBootstrapResult(
                Success: false,
                NeedsManualAction: false,
                Message: "Browser automation backend was not selected.");
        }

        if (await HasNodeRuntimeAsync(ct))
        {
            if (backend == BrowserAutomationMcpProfiles.ChromeDevToolsBackend)
            {
                var chrome = BrowserAutomationRuntimeDetector.DetectChrome();
                if (!chrome.IsInstalled)
                {
                    return new BrowserAutomationBootstrapResult(
                        Success: false,
                        NeedsManualAction: false,
                        Message: "Chrome DevTools MCP requires a local Chrome executable, but none was found.");
                }
            }

            return new BrowserAutomationBootstrapResult(
                Success: true,
                NeedsManualAction: false,
                Message: "Node.js runtime detected.");
        }

        var install = await TryInstallNodeJsAsync(ct);
        if (install.Succeeded && await HasNodeRuntimeAsync(ct))
        {
            if (backend == BrowserAutomationMcpProfiles.ChromeDevToolsBackend)
            {
                var chrome = BrowserAutomationRuntimeDetector.DetectChrome();
                if (!chrome.IsInstalled)
                {
                    return new BrowserAutomationBootstrapResult(
                        Success: false,
                        NeedsManualAction: false,
                        Message: "Node.js installed, but Chrome DevTools MCP still needs a local Chrome executable.");
                }
            }

            return new BrowserAutomationBootstrapResult(
                Success: true,
                NeedsManualAction: false,
                Message: "Installed Node.js runtime automatically.");
        }

        var backendLabel = backend == BrowserAutomationMcpProfiles.PlaywrightBackend
            ? "Playwright MCP"
            : "Chrome DevTools MCP";

        var manualCommand = install.ManualCommand ?? GetDefaultManualInstallCommand();
        var baseMessage = install.Attempted
            ? "Automatic Node.js install failed."
            : "Node.js is not installed.";

        var message =
            $"{baseMessage} {backendLabel} requires Node.js (20+ recommended). Install it, then press Enter to retry setup.";

        return new BrowserAutomationBootstrapResult(
            Success: false,
            NeedsManualAction: true,
            Message: message,
            ManualCommand: manualCommand);
    }

    private static async Task<bool> HasNodeRuntimeAsync(CancellationToken ct)
    {
        if (BrowserAutomationRuntimeDetector.HasNodeRuntime())
            return true;

        var hasNode = await CommandExistsAsync("node", ct);
        var hasNpx = await CommandExistsAsync("npx", ct);
        return hasNode && hasNpx;
    }

    private static async Task<(bool Attempted, bool Succeeded, string? ManualCommand)> TryInstallNodeJsAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            if (await CommandExistsAsync("winget", ct))
            {
                var command = "winget install --id OpenJS.NodeJS.LTS --scope user --accept-package-agreements --accept-source-agreements --silent";
                var ok = await RunCommandAsync("winget",
                    "install --id OpenJS.NodeJS.LTS --scope user --accept-package-agreements --accept-source-agreements --silent",
                    TimeSpan.FromMinutes(4), ct);
                return (true, ok, command);
            }

            return (false, false,
                "winget install --id OpenJS.NodeJS.LTS --scope user --accept-package-agreements --accept-source-agreements --silent");
        }

        if (await CommandExistsAsync("volta", ct))
        {
            const string command = "volta install node@20";
            var ok = await RunCommandAsync("volta", "install node@20", TimeSpan.FromMinutes(2), ct);
            return (true, ok, command);
        }

        if (await CommandExistsAsync("fnm", ct))
        {
            const string command = "fnm install 20";
            var ok = await RunCommandAsync("fnm", "install 20", TimeSpan.FromMinutes(2), ct);
            return (true, ok, command);
        }

        if (await CommandExistsAsync("brew", ct))
        {
            const string command = "brew install node@20";
            var ok = await RunCommandAsync("brew", "install node@20", TimeSpan.FromMinutes(5), ct);
            return (true, ok, command);
        }

        var userSpace = await TryInstallNodeJsUserSpaceAsync(ct);
        if (userSpace.Attempted)
            return userSpace;

        return (false, false, GetDefaultManualInstallCommand());
    }

    private static async Task<(bool Attempted, bool Succeeded, string? ManualCommand)> TryInstallNodeJsUserSpaceAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return (false, false, null);

        var platformToken = BrowserAutomationRuntimeDetector.GetNodeArchivePlatformToken();
        var toolsRoot = BrowserAutomationRuntimeDetector.GetUserToolsRoot();
        var installRoot = Path.Combine(toolsRoot, "node");
        var downloadsRoot = Path.Combine(toolsRoot, "downloads");

        Directory.CreateDirectory(toolsRoot);
        Directory.CreateDirectory(downloadsRoot);

        const string latestBaseUrl = "https://nodejs.org/dist/latest-v20.x/";
        var archivePattern = $"node-v[0-9]+\\.[0-9]+\\.[0-9]+-{Regex.Escape(platformToken)}\\.tar\\.xz";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            var listing = await http.GetStringAsync(latestBaseUrl, ct);
            var match = Regex.Match(listing, archivePattern, RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return (true, false,
                    "Could not resolve a Node.js archive for this platform from https://nodejs.org/dist/latest-v20.x/");
            }

            var archiveName = match.Value;
            var archivePath = Path.Combine(downloadsRoot, archiveName);

            await using (var remote = await http.GetStreamAsync(latestBaseUrl + archiveName, ct))
            await using (var local = File.Create(archivePath))
            {
                await remote.CopyToAsync(local, ct);
            }

            if (Directory.Exists(installRoot))
                Directory.Delete(installRoot, recursive: true);

            var extractedRootName = archiveName.Replace(".tar.xz", string.Empty, StringComparison.OrdinalIgnoreCase);
            var extractedPath = Path.Combine(toolsRoot, extractedRootName);
            if (Directory.Exists(extractedPath))
                Directory.Delete(extractedPath, recursive: true);

            var extracted = await RunCommandAsync(
                "tar",
                $"-xJf \"{archivePath}\" -C \"{toolsRoot}\"",
                TimeSpan.FromMinutes(2),
                ct);

            if (!extracted || !Directory.Exists(extractedPath))
                return (true, false, "tar -xJf <node-archive>.tar.xz -C ~/.netclaw/tools");

            Directory.Move(extractedPath, installRoot);
            return (true, true, null);
        }
        catch
        {
            return (true, false,
                "Install Node.js LTS in user space from https://nodejs.org/dist/latest-v20.x/ and ensure node+npx are available");
        }
    }

    private static string GetDefaultManualInstallCommand()
    {
        if (OperatingSystem.IsWindows())
            return "winget install --id OpenJS.NodeJS.LTS --scope user --accept-package-agreements --accept-source-agreements --silent";

        if (OperatingSystem.IsMacOS())
            return "brew install node@20";

        return "Install Node.js LTS from https://nodejs.org/ or use your user-scoped package manager";
    }

    private static async Task<bool> CommandExistsAsync(string command, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            return await RunCommandAsync("where", command, TimeSpan.FromSeconds(10), ct);

        return await RunCommandAsync("which", command, TimeSpan.FromSeconds(10), ct);
    }

    private static async Task<bool> RunCommandAsync(string command, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return false;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            await proc.WaitForExitAsync(timeoutCts.Token);
            return proc.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }
}

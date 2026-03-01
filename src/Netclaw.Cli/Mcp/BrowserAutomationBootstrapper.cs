using System.Diagnostics;

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
            return new BrowserAutomationBootstrapResult(
                Success: true,
                NeedsManualAction: false,
                Message: "Node.js runtime detected.");
        }

        var install = await TryInstallNodeJsAsync(ct);
        if (install.Succeeded && await HasNodeRuntimeAsync(ct))
        {
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

        return (false, false, GetDefaultManualInstallCommand());
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

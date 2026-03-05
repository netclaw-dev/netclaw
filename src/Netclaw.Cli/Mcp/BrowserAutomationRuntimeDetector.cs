using System.Runtime.InteropServices;

namespace Netclaw.Cli.Mcp;

internal sealed record ChromeDetectionResult(
    bool IsInstalled,
    string? ExecutablePath,
    string? Reason);

internal static class BrowserAutomationRuntimeDetector
{
    private const string ToolsDirectoryName = ".netclaw/tools";
    private const string PlaywrightBrowsersDirectoryName = "playwright-browsers";
    private static readonly HashSet<string> SupportedPlaywrightBrowsers =
    ["chrome", "firefox", "webkit", "msedge"];

    private static readonly string[] ChromeCommandCandidates =
    [
        "google-chrome",
        "google-chrome-stable",
        "chromium",
        "chromium-browser",
        "chrome"
    ];

    private static readonly string[] LinuxPathCandidates =
    [
        "/opt/google/chrome/chrome",
        "/usr/bin/google-chrome",
        "/usr/bin/google-chrome-stable",
        "/usr/bin/chromium",
        "/usr/bin/chromium-browser"
    ];

    private static readonly string[] MacPathCandidates =
    [
        "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
        "/Applications/Chromium.app/Contents/MacOS/Chromium"
    ];

    public static ChromeDetectionResult DetectChrome()
    {
        var envPath = Environment.GetEnvironmentVariable("CHROME_PATH");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
        {
            return new ChromeDetectionResult(true, envPath, null);
        }

        foreach (var command in ChromeCommandCandidates)
        {
            var resolved = ResolveCommandPath(command);
            if (resolved is not null)
                return new ChromeDetectionResult(true, resolved, null);
        }

        var platformPaths = OperatingSystem.IsMacOS() ? MacPathCandidates : LinuxPathCandidates;
        foreach (var path in platformPaths)
        {
            if (File.Exists(path))
                return new ChromeDetectionResult(true, path, null);
        }

        return new ChromeDetectionResult(
            false,
            null,
            "local Chrome executable not found");
    }

    public static bool HasNodeRuntime()
    {
        var bundled = GetBundledNodeBinDirectory();
        if (bundled is not null
            && File.Exists(Path.Combine(bundled, "node"))
            && File.Exists(Path.Combine(bundled, "npx")))
        {
            return true;
        }

        return ResolveCommandPath("node") is not null && ResolveCommandPath("npx") is not null;
    }

    public static string GetPreferredNpxCommand()
    {
        var bundled = GetBundledNodeBinDirectory();
        if (bundled is not null)
        {
            var candidate = Path.Combine(bundled, "npx");
            if (File.Exists(candidate))
                return candidate;
        }

        return ResolveCommandPath("npx") ?? "npx";
    }

    public static Dictionary<string, string>? BuildMcpEnvironmentOverlay(string commandPath)
    {
        if (!Path.IsPathRooted(commandPath))
            return null;

        var commandDir = Path.GetDirectoryName(commandPath);
        if (string.IsNullOrWhiteSpace(commandDir))
            return null;

        var existingPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var separator = OperatingSystem.IsWindows() ? ';' : ':';
        var combinedPath = string.IsNullOrWhiteSpace(existingPath)
            ? commandDir
            : $"{commandDir}{separator}{existingPath}";

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = combinedPath
        };
    }

    public static Dictionary<string, string>? BuildPlaywrightEnvironmentOverlay(string commandPath)
    {
        var env = BuildMcpEnvironmentOverlay(commandPath)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        env["PLAYWRIGHT_BROWSERS_PATH"] = GetPlaywrightBrowsersPath();
        return env;
    }

    public static string GetUserToolsRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ToolsDirectoryName);
    }

    public static string GetPlaywrightBrowsersPath()
        => Path.Combine(GetUserToolsRoot(), PlaywrightBrowsersDirectoryName);

    public static bool HasPlaywrightFirefoxBrowserInstalled()
    {
        var browsersPath = GetPlaywrightBrowsersPath();
        if (!Directory.Exists(browsersPath))
            return false;

        return Directory.EnumerateDirectories(browsersPath, "firefox-*", SearchOption.TopDirectoryOnly)
            .Any();
    }

    public static string GetPreferredPlaywrightBrowser()
    {
        var explicitBrowser = Environment.GetEnvironmentVariable("NETCLAW_PLAYWRIGHT_BROWSER")
            ?.Trim()
            .ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(explicitBrowser)
            && SupportedPlaywrightBrowsers.Contains(explicitBrowser))
        {
            return explicitBrowser;
        }

        return DetectChrome().IsInstalled ? "chrome" : "firefox";
    }

    public static string GetPlaywrightBrowserFromArguments(IReadOnlyList<string>? args)
    {
        if (args is not null)
        {
            for (var i = 0; i < args.Count - 1; i++)
            {
                if (string.Equals(args[i], "--browser", StringComparison.OrdinalIgnoreCase))
                {
                    var value = args[i + 1].Trim().ToLowerInvariant();
                    if (SupportedPlaywrightBrowsers.Contains(value))
                        return value;
                }
            }
        }

        return GetPreferredPlaywrightBrowser();
    }

    public static bool HasPlaywrightBrowserRuntime(string browser)
    {
        return browser.ToLowerInvariant() switch
        {
            "chrome" => DetectChrome().IsInstalled,
            "firefox" => HasPlaywrightFirefoxBrowserInstalled(),
            "webkit" => HasPlaywrightWebkitBrowserInstalled(),
            "msedge" => HasMicrosoftEdgeInstalled(),
            _ => false
        };
    }

    private static bool HasPlaywrightWebkitBrowserInstalled()
    {
        var browsersPath = GetPlaywrightBrowsersPath();
        if (!Directory.Exists(browsersPath))
            return false;

        return Directory.EnumerateDirectories(browsersPath, "webkit-*", SearchOption.TopDirectoryOnly)
            .Any();
    }

    private static bool HasMicrosoftEdgeInstalled()
    {
        var edgeCommands = new[] { "microsoft-edge", "microsoft-edge-stable", "msedge" };
        if (edgeCommands.Any(command => ResolveCommandPath(command) is not null))
            return true;

        var edgePaths = OperatingSystem.IsMacOS()
            ? new[] { "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge" }
            : new[] { "/usr/bin/microsoft-edge", "/usr/bin/msedge" };

        return edgePaths.Any(File.Exists);
    }

    public static string? GetBundledNodeBinDirectory()
    {
        var toolsRoot = GetUserToolsRoot();
        var nodeRoot = Path.Combine(toolsRoot, "node");
        if (!Directory.Exists(nodeRoot))
            return null;

        var bin = OperatingSystem.IsWindows()
            ? Path.Combine(nodeRoot)
            : Path.Combine(nodeRoot, "bin");

        return Directory.Exists(bin) ? bin : null;
    }

    public static string GetNodeArchivePlatformToken()
    {
        var os = OperatingSystem.IsMacOS() ? "darwin" : "linux";
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            _ => "x64"
        };

        return $"{os}-{arch}";
    }

    private static string? ResolveCommandPath(string command)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVar))
            return null;

        foreach (var segment in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.IsNullOrWhiteSpace(segment))
                continue;

            var candidate = Path.Combine(segment, command);
            if (File.Exists(candidate))
                return candidate;

            if (OperatingSystem.IsWindows())
            {
                var candidateExe = candidate + ".exe";
                if (File.Exists(candidateExe))
                    return candidateExe;
            }
        }

        return null;
    }
}

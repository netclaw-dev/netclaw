namespace Netclaw.Cli.Mcp;

internal sealed record ChromeDetectionResult(
    bool IsInstalled,
    string? ExecutablePath,
    string? Reason);

internal static class BrowserAutomationRuntimeDetector
{
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
        => ResolveCommandPath("node") is not null && ResolveCommandPath("npx") is not null;

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

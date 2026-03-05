using Netclaw.Configuration;

namespace Netclaw.Cli.Mcp;

internal static class BrowserAutomationMcpProfiles
{
    public const string ChromeDevToolsBackend = "chrome-devtools";
    public const string PlaywrightBackend = "playwright";

    public static (string Name, McpServerEntry Entry) Create(string backend)
    {
        var npxCommand = BrowserAutomationRuntimeDetector.GetPreferredNpxCommand();
        var env = BrowserAutomationRuntimeDetector.BuildMcpEnvironmentOverlay(npxCommand);
        var playwrightEnv = BrowserAutomationRuntimeDetector.BuildPlaywrightEnvironmentOverlay(npxCommand);
        var playwrightBrowser = BrowserAutomationRuntimeDetector.GetPreferredPlaywrightBrowser();

        return backend switch
        {
            PlaywrightBackend => ("browser_playwright", new McpServerEntry
            {
                Transport = "stdio",
                Enabled = true,
                GrantCategory = "browser_automation",
                Command = npxCommand,
                EnvironmentVariables = playwrightEnv,
                Arguments =
                [
                    "-y",
                    "@playwright/mcp@latest",
                    "--isolated",
                    "--headless",
                    "--image-responses",
                    "omit",
                    "--snapshot-mode",
                    "none",
                    "--browser",
                    playwrightBrowser
                ]
            }),

            _ => ("browser_chrome_devtools", new McpServerEntry
            {
                Transport = "stdio",
                Enabled = true,
                GrantCategory = "browser_automation",
                Command = npxCommand,
                EnvironmentVariables = env,
                Arguments =
                [
                    "-y",
                    "chrome-devtools-mcp@latest",
                    "--headless=true",
                    "--no-usage-statistics"
                ]
            })
        };
    }
}

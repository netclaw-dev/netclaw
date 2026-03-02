using Netclaw.Configuration;

namespace Netclaw.Cli.Mcp;

internal static class BrowserAutomationMcpProfiles
{
    public const string ChromeDevToolsBackend = "chrome-devtools";
    public const string PlaywrightBackend = "playwright";

    public static (string Name, McpServerEntry Entry) Create(string backend)
    {
        return backend switch
        {
            PlaywrightBackend => ("browser_playwright", new McpServerEntry
            {
                Transport = "stdio",
                Enabled = true,
                GrantCategory = "browser_automation",
                Command = "npx",
                Arguments =
                [
                    "-y",
                    "@playwright/mcp@latest",
                    "--headless",
                    "--image-responses",
                    "omit",
                    "--snapshot-mode",
                    "none"
                ]
            }),

            _ => ("browser_chrome_devtools", new McpServerEntry
            {
                Transport = "stdio",
                Enabled = true,
                GrantCategory = "browser_automation",
                Command = "npx",
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

// -----------------------------------------------------------------------
// <copyright file="BrowserAutomationMcpProfiles.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Cli.Mcp;

internal static class BrowserAutomationMcpProfiles
{
    public static (string Name, McpServerEntry Entry) Create(BrowserAutomationBackend backend)
    {
        var npxCommand = BrowserAutomationRuntimeDetector.GetPreferredNpxCommand();
        var env = BrowserAutomationRuntimeDetector.BuildMcpEnvironmentOverlay(npxCommand);
        var playwrightEnv = BrowserAutomationRuntimeDetector.BuildPlaywrightEnvironmentOverlay(npxCommand);
        var playwrightBrowser = BrowserAutomationRuntimeDetector.GetPreferredPlaywrightBrowser();

        return backend switch
        {
            BrowserAutomationBackend.Playwright => ("browser_playwright", new McpServerEntry
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

            BrowserAutomationBackend.ChromeDevTools => ("browser_chrome_devtools", new McpServerEntry
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
            }),

            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend,
                $"Unknown browser automation backend: {backend}")
        };
    }
}

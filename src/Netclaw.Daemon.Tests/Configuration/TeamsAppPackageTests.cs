// -----------------------------------------------------------------------
// <copyright file="TeamsAppPackageTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Netclaw.Daemon.Tests.Mcp;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class TeamsAppPackageTests
{
    private static readonly string PackageDirectory = Path.Combine(
        SmokeMcpServerLocator.LocateRepositoryRoot(),
        "deploy",
        "teams");

    [Fact]
    public void Manifest_template_uses_only_approved_Teams_capabilities_and_RSC()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(PackageDirectory, "manifest.template.json")));
        var root = document.RootElement;

        Assert.Equal("1.28", root.GetProperty("manifestVersion").GetString());
        Assert.Equal("${{TEAMS_APP_ID}}", root.GetProperty("id").GetString());
        Assert.Equal("tier1", root.GetProperty("supportsChannelFeatures").GetString());
        Assert.DoesNotContain("secret", root.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var bots = root.GetProperty("bots");
        var bot = Assert.Single(bots.EnumerateArray());
        Assert.Equal("${{TEAMS_APP_ID}}", bot.GetProperty("botId").GetString());
        Assert.Equal(
            ["personal", "team", "groupchat"],
            bot.GetProperty("scopes").EnumerateArray().Select(scope => scope.GetString()!).ToArray());
        Assert.False(bot.GetProperty("supportsCalling").GetBoolean());
        Assert.True(bot.GetProperty("supportsFiles").GetBoolean());
        Assert.False(bot.GetProperty("supportsVideo").GetBoolean());
        Assert.False(bot.TryGetProperty("supportsChannelFeatures", out _));

        var webApplication = root.GetProperty("webApplicationInfo");
        Assert.Equal("${{TEAMS_APP_ID}}", webApplication.GetProperty("id").GetString());
        Assert.Equal("https://netclaw", webApplication.GetProperty("resource").GetString());
        var rsc = root.GetProperty("authorization").GetProperty("permissions").GetProperty("resourceSpecific");
        Assert.Equal(
            ["ChannelMessage.Read.Group", "ChatMessage.Read.Chat"],
            rsc.EnumerateArray().Select(permission => permission.GetProperty("name").GetString()!).ToArray());
        Assert.All(rsc.EnumerateArray(), permission =>
            Assert.Equal("Application", permission.GetProperty("type").GetString()));
        foreach (var forbiddenPermission in new[]
                 {
                     "Chat.Read.All",
                     "ChatMessage.Read.All",
                     "Files.Read.All",
                     "Sites.Read.All"
                 })
        {
            Assert.DoesNotContain(forbiddenPermission, root.GetRawText(), StringComparison.Ordinal);
        }

        foreach (var forbiddenProperty in new[]
                 {
                     "configurableTabs",
                     "composeExtensions",
                     "meetingExtensionDefinition",
                     "permissions",
                     "staticTabs",
                     "validDomains"
                 })
        {
            Assert.False(root.TryGetProperty(forbiddenProperty, out _),
                $"The Teams manifest must not contain '{forbiddenProperty}'.");
        }
    }

    [Theory]
    [InlineData("color.png", 192, 192)]
    [InlineData("outline.png", 32, 32)]
    public void AppIconMatchesTeamsDimensionsAndUsesAlpha(string fileName, int expectedWidth, int expectedHeight)
    {
        var header = File.ReadAllBytes(Path.Combine(PackageDirectory, fileName)).AsSpan(0, 26);

        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, header[..8].ToArray());
        Assert.Equal(expectedWidth, BinaryPrimitives.ReadInt32BigEndian(header[16..20]));
        Assert.Equal(expectedHeight, BinaryPrimitives.ReadInt32BigEndian(header[20..24]));
        Assert.Contains(header[25], new byte[] { 4, 6 });
    }

    [Fact]
    public async Task PackageBuilderCreatesExactRootFilesAndResolvedManifest()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), $"netclaw-teams-package-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);

        try
        {
            var appId = Guid.Parse("d0d67285-d282-4f08-9d85-0a4cbd0211bf");
            var packagePath = Path.Combine(testDirectory, "netclaw-teams.zip");
            var result = await RunPackageBuilder(
                appId,
                "Netclaw Test Operator",
                "https://example.test/privacy",
                "https://example.test/terms",
                packagePath);

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(packagePath), result.StandardError);

            using var archive = ZipFile.OpenRead(packagePath);
            Assert.Equal(
                ["color.png", "manifest.json", "outline.png"],
                archive.Entries.Select(entry => entry.FullName).Order().ToArray());

            var manifestEntry = Assert.Single(archive.Entries, entry => entry.FullName == "manifest.json");
            await using var manifestStream = manifestEntry.Open();
            using var manifest = await JsonDocument.ParseAsync(
                manifestStream,
                cancellationToken: TestContext.Current.CancellationToken);
            var root = manifest.RootElement;

            Assert.Equal(appId.ToString(), root.GetProperty("id").GetString());
            Assert.Equal("1.2.3", root.GetProperty("version").GetString());
            Assert.Equal("tier1", root.GetProperty("supportsChannelFeatures").GetString());
            Assert.Equal(appId.ToString(), root.GetProperty("bots")[0].GetProperty("botId").GetString());
            Assert.Equal(appId.ToString(), root.GetProperty("webApplicationInfo").GetProperty("id").GetString());
            Assert.Equal("https://netclaw", root.GetProperty("webApplicationInfo").GetProperty("resource").GetString());
            var rsc = root.GetProperty("authorization").GetProperty("permissions").GetProperty("resourceSpecific");
            Assert.Equal(
                ["ChannelMessage.Read.Group", "ChatMessage.Read.Chat"],
                rsc.EnumerateArray().Select(permission => permission.GetProperty("name").GetString()!).ToArray());
            Assert.All(rsc.EnumerateArray(), permission =>
                Assert.Equal("Application", permission.GetProperty("type").GetString()));
            Assert.Equal("Netclaw Test Operator", root.GetProperty("developer").GetProperty("name").GetString());
            Assert.Equal("https://example.test/privacy", root.GetProperty("developer").GetProperty("privacyUrl").GetString());
            Assert.Equal("https://example.test/terms", root.GetProperty("developer").GetProperty("termsOfUseUrl").GetString());
            Assert.DoesNotContain("${{", root.GetRawText(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task PackageBuilderRejectsNonHttpsPolicyUrl()
    {
        var packagePath = Path.Combine(Path.GetTempPath(), $"netclaw-teams-{Guid.NewGuid():N}.zip");
        var result = await RunPackageBuilder(
            Guid.NewGuid(),
            "Netclaw Test Operator",
            "http://example.test/privacy",
            "https://example.test/terms",
            packagePath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(packagePath));
        Assert.Contains("Policy URLs must use absolute HTTPS addresses", result.StandardError);
    }

    [Fact]
    public async Task PackageBuilderRejectsDeveloperNameAboveManifestLimit()
    {
        var packagePath = Path.Combine(Path.GetTempPath(), $"netclaw-teams-{Guid.NewGuid():N}.zip");
        var result = await RunPackageBuilder(
            Guid.NewGuid(),
            new string('a', 33),
            "https://example.test/privacy",
            "https://example.test/terms",
            packagePath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(packagePath));
    }

    private static async Task<ProcessResult> RunPackageBuilder(
        Guid appId,
        string developerName,
        string privacyUrl,
        string termsOfUseUrl,
        string outputPath)
    {
        var startInfo = new ProcessStartInfo("pwsh")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(PackageDirectory, "build-package.ps1"));
        startInfo.ArgumentList.Add("-AppId");
        startInfo.ArgumentList.Add(appId.ToString());
        startInfo.ArgumentList.Add("-DeveloperName");
        startInfo.ArgumentList.Add(developerName);
        startInfo.ArgumentList.Add("-PrivacyUrl");
        startInfo.ArgumentList.Add(privacyUrl);
        startInfo.ArgumentList.Add("-TermsOfUseUrl");
        startInfo.ArgumentList.Add(termsOfUseUrl);
        startInfo.ArgumentList.Add("-Version");
        startInfo.ArgumentList.Add("1.2.3");
        startInfo.ArgumentList.Add("-OutputPath");
        startInfo.ArgumentList.Add(outputPath);

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}

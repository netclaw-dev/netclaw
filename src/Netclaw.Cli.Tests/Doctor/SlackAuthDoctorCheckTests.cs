using System.Text.Json;
using Netclaw.Channels.Slack;
using Netclaw.Cli.Doctor;
using Netclaw.Cli.Tests.Tui;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class SlackAuthDoctorCheckTests
{
    [Fact]
    public async Task ReturnsPass_WhenSlackDisabled()
    {
        var (paths, _) = CreateTempPaths();
        WriteConfig(paths, slackEnabled: false);

        var probe = new FakeSlackProbe();
        var check = new SlackAuthDoctorCheck(paths, probe);
        var result = await check.RunAsync();

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("disabled", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, probe.ProbeCallCount);
    }

    [Fact]
    public async Task ReturnsError_WhenSlackEnabled_NoBotToken()
    {
        var (paths, _) = CreateTempPaths();
        WriteConfig(paths, slackEnabled: true);
        // No secrets.json written — bot token missing

        var probe = new FakeSlackProbe();
        var check = new SlackAuthDoctorCheck(paths, probe);
        var result = await check.RunAsync();

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("no bot token", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result.Remediation);
        Assert.Equal(0, probe.ProbeCallCount);
    }

    [Fact]
    public async Task ReturnsPass_WhenSlackEnabled_ProbeSucceeds()
    {
        var (paths, _) = CreateTempPaths();
        WriteConfig(paths, slackEnabled: true);
        WriteSecrets(paths, botToken: "xoxb-valid-token");

        var probe = new FakeSlackProbe
        {
            NextResult = new SlackProbeResult(true, null, "Test Team", new SlackUserId("U12345"))
        };

        var check = new SlackAuthDoctorCheck(paths, probe);
        var result = await check.RunAsync();

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("Test Team", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, probe.ProbeCallCount);
        Assert.Equal("xoxb-valid-token", probe.LastBotToken);
    }

    [Fact]
    public async Task ReturnsError_WhenSlackEnabled_ProbeFailsInvalidAuth()
    {
        var (paths, _) = CreateTempPaths();
        WriteConfig(paths, slackEnabled: true);
        WriteSecrets(paths, botToken: "xoxb-bad-token");

        var probe = new FakeSlackProbe
        {
            NextResult = new SlackProbeResult(false,
                "Bot token is invalid. Check your Slack app's Bot User OAuth Token.", null, null)
        };

        var check = new SlackAuthDoctorCheck(paths, probe);
        var result = await check.RunAsync();

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("invalid", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result.Remediation);
    }

    [Fact]
    public async Task DecryptsEncryptedBotToken_BeforeProbe()
    {
        var (paths, _) = CreateTempPaths();
        WriteConfig(paths, slackEnabled: true);

        var secrets = new Dictionary<string, object>
        {
            ["Slack"] = new Dictionary<string, object>
            {
                ["BotToken"] = "xoxb-valid-token"
            }
        };

        var protector = SecretsProtection.CreateProtector(paths);
        SecretsFileWriter.Write(paths.SecretsPath, secrets,
            options: new JsonSerializerOptions { WriteIndented = true },
            protector: protector);

        var probe = new FakeSlackProbe
        {
            NextResult = new SlackProbeResult(true, null, "Test Team", new SlackUserId("U12345"))
        };

        var check = new SlackAuthDoctorCheck(paths, probe);
        var result = await check.RunAsync();

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Equal("xoxb-valid-token", probe.LastBotToken);
    }

    [Fact]
    public async Task ReturnsError_WhenEncryptedBotTokenCannotBeDecrypted()
    {
        var (paths, _) = CreateTempPaths();
        WriteConfig(paths, slackEnabled: true);

        var secrets = new Dictionary<string, object>
        {
            ["Slack"] = new Dictionary<string, object>
            {
                ["BotToken"] = "ENC:corrupted-token"
            }
        };

        File.WriteAllText(paths.SecretsPath,
            JsonSerializer.Serialize(secrets, new JsonSerializerOptions { WriteIndented = true }));

        var probe = new FakeSlackProbe();
        var check = new SlackAuthDoctorCheck(paths, probe);
        var result = await check.RunAsync();

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("could not be decrypted", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result.Remediation);
        Assert.Equal(0, probe.ProbeCallCount);
    }

    private static (NetclawPaths paths, string basePath) CreateTempPaths()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "netclaw-tests", Guid.NewGuid().ToString("N"));
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();
        return (paths, basePath);
    }

    private static void WriteConfig(NetclawPaths paths, bool slackEnabled)
    {
        var config = new Dictionary<string, object>
        {
            ["Slack"] = new Dictionary<string, object>
            {
                ["Enabled"] = slackEnabled
            }
        };

        File.WriteAllText(paths.NetclawConfigPath,
            JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteSecrets(NetclawPaths paths, string botToken)
    {
        var secrets = new Dictionary<string, object>
        {
            ["Slack"] = new Dictionary<string, object>
            {
                ["BotToken"] = botToken
            }
        };

        File.WriteAllText(paths.SecretsPath,
            JsonSerializer.Serialize(secrets, new JsonSerializerOptions { WriteIndented = true }));
    }
}

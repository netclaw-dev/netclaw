// -----------------------------------------------------------------------
// <copyright file="WizardConfigBuilderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

/// <summary>
/// Tests for <see cref="WizardConfigBuilder"/> config dictionary assembly.
/// </summary>
public sealed class WizardConfigBuilderTests : WizardStepTestBase
{

    [Fact]
    public void BuildConfigDictionary_AlwaysIncludesConfigVersion()
    {
        var builder = new WizardConfigBuilder(Context.Paths);
        var config = builder.BuildConfigDictionary();

        Assert.Equal(1, config["configVersion"]);
    }

    [Fact]
    public void BuildConfigDictionary_IncludesProviderSection()
    {
        var builder = new WizardConfigBuilder(Context.Paths)
        {
            Provider = new ProviderConfigSection
            {
                TypeKey = "openai",
                AuthMethod = AuthMethod.ApiKey,
                Endpoint = "https://api.openai.com"
            }
        };

        var config = builder.BuildConfigDictionary();

        var providers = (Dictionary<string, object>)config["Providers"];
        var openai = (Dictionary<string, object>)providers["openai"];
        Assert.Equal("openai", openai["Type"]);
        Assert.Equal("ApiKey", openai["AuthMethod"]);
        Assert.Equal("https://api.openai.com", openai["Endpoint"]);
    }

    [Fact]
    public void BuildConfigDictionary_OmitsAuthMethod_WhenNone()
    {
        var builder = new WizardConfigBuilder(Context.Paths)
        {
            Provider = new ProviderConfigSection
            {
                TypeKey = "ollama",
                AuthMethod = AuthMethod.None
            }
        };

        var config = builder.BuildConfigDictionary();

        var providers = (Dictionary<string, object>)config["Providers"];
        var ollama = (Dictionary<string, object>)providers["ollama"];
        Assert.False(ollama.ContainsKey("AuthMethod"));
    }

    [Fact]
    public void BuildConfigDictionary_IncludesModelsSection()
    {
        var builder = new WizardConfigBuilder(Context.Paths)
        {
            Model = new ModelConfigSection
            {
                Provider = "openai",
                ModelId = "gpt-4.1"
            }
        };

        var config = builder.BuildConfigDictionary();

        var models = (Dictionary<string, object>)config["Models"];
        var main = (Dictionary<string, object>)models["Main"];
        Assert.Equal("openai", main["Provider"]);
        Assert.Equal("gpt-4.1", main["ModelId"]);
    }

    [Fact]
    public void BuildConfigDictionary_IncludesSlackSection_WhenEnabled()
    {
        var builder = new WizardConfigBuilder(Context.Paths)
        {
            Slack = new SlackConfigSection
            {
                Enabled = true,
                AllowedChannelIds = ["C123", "C456"],
                AllowDirectMessages = true,
                AllowedUserIds = ["U789"],
                ChannelAudiences = new Dictionary<string, string> { ["C123"] = "team" }
            }
        };

        var config = builder.BuildConfigDictionary();

        var slack = (Dictionary<string, object>)config["Slack"];
        Assert.Equal(true, slack["Enabled"]);
        Assert.Equal(true, slack["SocketMode"]);
        Assert.Equal(true, slack["AllowDirectMessages"]);
        Assert.Equal("C123", ((string[])slack["AllowedChannelIds"])[0]);
        Assert.Equal("C123", slack["DefaultChannelId"]);
        Assert.Equal("U789", ((string[])slack["AllowedUserIds"])[0]);
    }

    [Fact]
    public void BuildConfigDictionary_OmitsSlack_WhenNotEnabled()
    {
        var builder = new WizardConfigBuilder(Context.Paths)
        {
            Slack = new SlackConfigSection { Enabled = false }
        };

        var config = builder.BuildConfigDictionary();

        Assert.False(config.ContainsKey("Slack"));
    }

    [Fact]
    public void BuildConfigDictionary_IncludesDiscordSection_WhenEnabled()
    {
        var builder = new WizardConfigBuilder(Context.Paths)
        {
            Discord = new DiscordConfigSection
            {
                Enabled = true,
                DefaultChannelId = "129847561203948576",
                AllowedChannelIds = ["129847561203948576"],
                AllowDirectMessages = true,
                AllowedUserIds = ["130111223344556677"],
                ChannelAudiences = new Dictionary<string, string> { ["dm"] = "team" }
            }
        };

        var config = builder.BuildConfigDictionary();

        var discord = (Dictionary<string, object>)config["Discord"];
        Assert.Equal(true, discord["Enabled"]);
        Assert.Equal("129847561203948576", discord["DefaultChannelId"]);
        Assert.Equal(true, discord["AllowDirectMessages"]);
        Assert.Equal("129847561203948576", ((string[])discord["AllowedChannelIds"])[0]);
        Assert.Equal("130111223344556677", ((string[])discord["AllowedUserIds"])[0]);
    }

    [Fact]
    public void BuildConfigDictionary_OmitsDiscord_WhenNotEnabled()
    {
        var builder = new WizardConfigBuilder(Context.Paths)
        {
            Discord = new DiscordConfigSection { Enabled = false }
        };

        var config = builder.BuildConfigDictionary();

        Assert.False(config.ContainsKey("Discord"));
    }

    [Fact]
    public void BuildConfigDictionary_IncludesMattermostSection_WhenEnabled()
    {
        var builder = new WizardConfigBuilder(Context.Paths)
        {
            Mattermost = new MattermostConfigSection
            {
                Enabled = true,
                ServerUrl = "https://mm.example.com",
                CallbackUrl = "http://netclaw-host:5199/api/mattermost/actions",
                DefaultChannelId = "4xp9p3onpins8",
                AllowedChannelIds = ["4xp9p3onpins8"],
                AllowDirectMessages = true,
                AllowedUserIds = ["9rp7q1abcdef"],
                ChannelAudiences = new Dictionary<string, string> { ["dm"] = "team" }
            }
        };

        var config = builder.BuildConfigDictionary();

        var mattermost = (Dictionary<string, object>)config["Mattermost"];
        Assert.Equal(true, mattermost["Enabled"]);
        Assert.Equal("https://mm.example.com", mattermost["ServerUrl"]);
        Assert.Equal("http://netclaw-host:5199/api/mattermost/actions", mattermost["CallbackUrl"]);
        Assert.Equal("4xp9p3onpins8", mattermost["DefaultChannelId"]);
        Assert.Equal(true, mattermost["AllowDirectMessages"]);
        Assert.Equal("4xp9p3onpins8", ((string[])mattermost["AllowedChannelIds"])[0]);
        Assert.Equal("9rp7q1abcdef", ((string[])mattermost["AllowedUserIds"])[0]);
    }

    [Fact]
    public void BuildConfigDictionary_OmitsMattermost_WhenNotEnabled()
    {
        var builder = new WizardConfigBuilder(Context.Paths)
        {
            Mattermost = new MattermostConfigSection { Enabled = false }
        };

        var config = builder.BuildConfigDictionary();

        Assert.False(config.ContainsKey("Mattermost"));
    }

    [Fact]
    public void BuildConfigDictionary_OmitsMattermostCallbackUrl_WhenBlank()
    {
        var builder = new WizardConfigBuilder(Context.Paths)
        {
            Mattermost = new MattermostConfigSection
            {
                Enabled = true,
                ServerUrl = "https://mm.example.com",
                CallbackUrl = null
            }
        };

        var config = builder.BuildConfigDictionary();

        var mattermost = (Dictionary<string, object>)config["Mattermost"];
        Assert.False(mattermost.ContainsKey("CallbackUrl"));
    }

    [Fact]
    public void BuildConfigDictionary_IncludesSecuritySection()
    {
        var builder = new WizardConfigBuilder(Context.Paths)
        {
            Security = new SecurityConfigSection
            {
                DeploymentPosture = DeploymentPosture.Team,
                ShellExecutionMode = ShellExecutionMode.Off
            }
        };

        var config = builder.BuildConfigDictionary();

        var security = (Dictionary<string, object>)config["Security"];
        Assert.Equal("Team", security["DeploymentPosture"]);
        Assert.Equal("Off", security["ShellExecutionMode"]);
        Assert.Equal(true, security["StrictDefaults"]);
    }

    [Fact]
    public void BuildConfigDictionary_OmitsSearch_WhenDuckDuckGo()
    {
        var builder = new WizardConfigBuilder(Context.Paths)
        {
            Search = new SearchConfigSection { Backend = SearchBackend.DuckDuckGo }
        };

        var config = builder.BuildConfigDictionary();

        Assert.False(config.ContainsKey("Search"));
    }

    [Fact]
    public void BuildConfigDictionary_IncludesSearch_WhenBrave()
    {
        var builder = new WizardConfigBuilder(Context.Paths)
        {
            Search = new SearchConfigSection { Backend = SearchBackend.Brave }
        };

        var config = builder.BuildConfigDictionary();

        var search = (Dictionary<string, object>)config["Search"];
        Assert.Equal("brave", search["Backend"]);
    }

    [Fact]
    public void BuildConfigDictionary_IncludesNotifications_WhenWebhookSet()
    {
        var builder = new WizardConfigBuilder(Context.Paths)
        {
            Notifications = new NotificationsConfigSection
            {
                WebhookUrl = "https://hooks.example.com/alert"
            }
        };

        var config = builder.BuildConfigDictionary();

        Assert.True(config.ContainsKey("Notifications"));
    }

    [Fact]
    public void BuildConfigDictionary_OmitsNotifications_WhenNoWebhook()
    {
        var builder = new WizardConfigBuilder(Context.Paths)
        {
            Notifications = new NotificationsConfigSection { WebhookUrl = null }
        };

        var config = builder.BuildConfigDictionary();

        Assert.False(config.ContainsKey("Notifications"));
    }

    [Fact]
    public void BuildConfigDictionary_IncludesExternalSkills_WhenSet()
    {
        var builder = new WizardConfigBuilder(Context.Paths)
        {
            ExternalSkillSources =
            [
                new ExternalSkillSource
                {
                    Name = "claude-code",
                    WellKnown = "claude-code",
                    Enabled = true,
                    AllowSymlinks = true
                },
                new ExternalSkillSource
                {
                    Name = "custom",
                    Path = "/opt/team/skills",
                    Enabled = true,
                    AllowSymlinks = false
                }
            ]
        };

        var config = builder.BuildConfigDictionary();

        Assert.True(config.ContainsKey("ExternalSkills"));
        var section = (Dictionary<string, object>)config["ExternalSkills"];
        var sources = (object[])section["Sources"];
        Assert.Equal(2, sources.Length);

        var first = (Dictionary<string, object>)sources[0];
        Assert.Equal("claude-code", first["Name"]);
        Assert.Equal("claude-code", first["WellKnown"]);
        Assert.Equal(true, first["Enabled"]);
        Assert.Equal(true, first["AllowSymlinks"]);
        Assert.False(first.ContainsKey("Path"));

        var second = (Dictionary<string, object>)sources[1];
        Assert.Equal("custom", second["Name"]);
        Assert.Equal("/opt/team/skills", second["Path"]);
        Assert.False(second.ContainsKey("WellKnown"));
    }

    [Fact]
    public void BuildConfigDictionary_OmitsExternalSkills_WhenNull()
    {
        var builder = new WizardConfigBuilder(Context.Paths);

        var config = builder.BuildConfigDictionary();

        Assert.False(config.ContainsKey("ExternalSkills"));
    }

    [Fact]
    public void WriteSecretsFile_ExistingSection_OverwritesContributedSecretsAndPreservesUnrelatedValues()
    {
        var priorProtector = SensitiveStringTypeConverter.Protector;
        var protector = SecretsProtection.CreateProtector(Context.Paths);
        SensitiveStringTypeConverter.Protector = protector;

        try
        {
            SecretsFileWriter.Write(Context.Paths.SecretsPath,
                """
                {
                  "Discord": {
                    "BotToken": "old-token",
                    "OtherSecret": "keep-discord"
                  },
                  "Discord:BotToken": "literal-collision",
                  "Search": {
                    "BraveApiKey": "keep-search"
                  }
                }
                """,
                protector);

            var builder = new WizardSecretsBuilder(Context.Paths);
            builder.AddSection("Discord", new Dictionary<string, object>
            {
                ["BotToken"] = "new-token"
            });

            builder.WriteSecretsFile();

            var encryptedJson = File.ReadAllText(Context.Paths.SecretsPath);
            Assert.DoesNotContain("\"Discord:BotToken\"", encryptedJson, StringComparison.Ordinal);

            var decryptedJson = SecretsFileWriter.DecryptJsonLeaves(encryptedJson, protector);
            using var document = JsonDocument.Parse(decryptedJson);

            var root = document.RootElement;
            var discord = root.GetProperty("Discord");
            Assert.Equal("new-token", discord.GetProperty("BotToken").GetString());
            Assert.Equal("keep-discord", discord.GetProperty("OtherSecret").GetString());
            Assert.Equal("keep-search", root.GetProperty("Search").GetProperty("BraveApiKey").GetString());
            Assert.False(root.TryGetProperty("Discord:BotToken", out _));
        }
        finally
        {
            SensitiveStringTypeConverter.Protector = priorProtector;
        }
    }

    [Fact]
    public void BuildConfigDictionary_OmitsFeatureFlags_WhenFeatureSelectionsNull()
    {
        var builder = new WizardConfigBuilder(Context.Paths);

        var config = builder.BuildConfigDictionary();

        // No Enabled flag should be injected into any feature section.
        // Some sections (e.g., SkillSync) exist unconditionally with other keys.
        AssertNoEnabledKey(config, "Memory");
        AssertNoEnabledKey(config, "Search");
        AssertNoEnabledKey(config, "SkillSync");
        AssertNoEnabledKey(config, "Scheduling");
        AssertNoEnabledKey(config, "SubAgents");
        AssertNoEnabledKey(config, "Webhooks");
    }

    // ── Daemon.UpdateChannel ────────────────────────────────────────────────

    [Fact]
    public void BuildConfigDictionary_DaemonWithBetaChannel_WritesUpdateChannel()
    {
        var builder = new WizardConfigBuilder(Context.Paths)
        {
            Daemon = new DaemonConfigSection
            {
                UpdateChannel = UpdateChannel.Beta,
            }
        };

        var config = builder.BuildConfigDictionary();

        Assert.True(config.ContainsKey("Daemon"));
        var daemon = (Dictionary<string, object>)config["Daemon"];
        Assert.Equal("beta", daemon["UpdateChannel"]);
        Assert.False(daemon.ContainsKey("ExposureMode"));
    }

    [Fact]
    public void BuildConfigDictionary_DaemonWithBetaChannelAndNonLocalMode_WritesBoth()
    {
        var builder = new WizardConfigBuilder(Context.Paths)
        {
            Daemon = new DaemonConfigSection
            {
                ExposureMode = ExposureMode.TailscaleServe,
                UpdateChannel = UpdateChannel.Beta,
            }
        };

        var config = builder.BuildConfigDictionary();

        var daemon = (Dictionary<string, object>)config["Daemon"];
        Assert.Equal("tailscale-serve", daemon["ExposureMode"]);
        Assert.Equal("beta", daemon["UpdateChannel"]);
    }

    [Fact]
    public void BuildConfigDictionary_DaemonWithNullChannel_OmitsUpdateChannel()
    {
        var builder = new WizardConfigBuilder(Context.Paths)
        {
            Daemon = new DaemonConfigSection
            {
                ExposureMode = ExposureMode.TailscaleServe,
            }
        };

        var config = builder.BuildConfigDictionary();

        var daemon = (Dictionary<string, object>)config["Daemon"];
        Assert.False(daemon.ContainsKey("UpdateChannel"));
    }

    [Fact]
    public void WriteConfigFile_PreservesExistingBetaChannel_WhenWizardDoesNotSetOne()
    {
        File.WriteAllText(Context.Paths.NetclawConfigPath, """
            {
              "configVersion": 1,
              "Daemon": { "UpdateChannel": "beta" }
            }
            """);

        var builder = new WizardConfigBuilder(Context.Paths)
        {
            Security = new SecurityConfigSection()
        };

        builder.WriteConfigFile();

        var written = JsonSerializer.Deserialize<JsonElement>(
            File.ReadAllText(Context.Paths.NetclawConfigPath));
        var daemon = written.GetProperty("Daemon");
        Assert.Equal("beta", daemon.GetProperty("UpdateChannel").GetString());
    }

    [Fact]
    public void WriteConfigFile_DoesNotPreserveStableChannel()
    {
        File.WriteAllText(Context.Paths.NetclawConfigPath, """
            {
              "configVersion": 1,
              "Daemon": { "UpdateChannel": "stable" }
            }
            """);

        var builder = new WizardConfigBuilder(Context.Paths)
        {
            Security = new SecurityConfigSection()
        };

        builder.WriteConfigFile();

        var written = JsonSerializer.Deserialize<JsonElement>(
            File.ReadAllText(Context.Paths.NetclawConfigPath));
        Assert.False(written.TryGetProperty("Daemon", out _));
    }

    [Fact]
    public void WriteConfigFile_ExplicitChannelOnBuilder_TakesPrecedenceOverExisting()
    {
        File.WriteAllText(Context.Paths.NetclawConfigPath, """
            {
              "configVersion": 1,
              "Daemon": { "UpdateChannel": "beta" }
            }
            """);

        var builder = new WizardConfigBuilder(Context.Paths)
        {
            Daemon = new DaemonConfigSection
            {
                UpdateChannel = UpdateChannel.Stable,
            },
            Security = new SecurityConfigSection()
        };

        builder.WriteConfigFile();

        var written = JsonSerializer.Deserialize<JsonElement>(
            File.ReadAllText(Context.Paths.NetclawConfigPath));
        var daemon = written.GetProperty("Daemon");
        Assert.Equal("stable", daemon.GetProperty("UpdateChannel").GetString());
    }

}

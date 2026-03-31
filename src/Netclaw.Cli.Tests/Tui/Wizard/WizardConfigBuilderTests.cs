using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

/// <summary>
/// Tests for <see cref="WizardConfigBuilder"/> config dictionary assembly.
/// </summary>
public sealed class WizardConfigBuilderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly NetclawPaths _paths;

    public WizardConfigBuilderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-test-{Guid.NewGuid():N}");
        _paths = new NetclawPaths(_tempDir);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void BuildConfigDictionary_AlwaysIncludesConfigVersion()
    {
        var builder = new WizardConfigBuilder(_paths);
        var config = builder.BuildConfigDictionary();

        Assert.Equal(1, config["configVersion"]);
    }

    [Fact]
    public void BuildConfigDictionary_IncludesProviderSection()
    {
        var builder = new WizardConfigBuilder(_paths)
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
        var builder = new WizardConfigBuilder(_paths)
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
        var builder = new WizardConfigBuilder(_paths)
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
        var builder = new WizardConfigBuilder(_paths)
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
        var builder = new WizardConfigBuilder(_paths)
        {
            Slack = new SlackConfigSection { Enabled = false }
        };

        var config = builder.BuildConfigDictionary();

        Assert.False(config.ContainsKey("Slack"));
    }

    [Fact]
    public void BuildConfigDictionary_IncludesSecuritySection()
    {
        var builder = new WizardConfigBuilder(_paths)
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
        var builder = new WizardConfigBuilder(_paths)
        {
            Search = new SearchConfigSection { Backend = SearchBackend.DuckDuckGo }
        };

        var config = builder.BuildConfigDictionary();

        Assert.False(config.ContainsKey("Search"));
    }

    [Fact]
    public void BuildConfigDictionary_IncludesSearch_WhenBrave()
    {
        var builder = new WizardConfigBuilder(_paths)
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
        var builder = new WizardConfigBuilder(_paths)
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
        var builder = new WizardConfigBuilder(_paths)
        {
            Notifications = new NotificationsConfigSection { WebhookUrl = null }
        };

        var config = builder.BuildConfigDictionary();

        Assert.False(config.ContainsKey("Notifications"));
    }

    [Fact]
    public void BuildConfigDictionary_IncludesExternalSkills_WhenSet()
    {
        var builder = new WizardConfigBuilder(_paths)
        {
            ExternalSkills = new ExternalSkillsConfigSection
            {
                Sources =
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
            }
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
        var builder = new WizardConfigBuilder(_paths);

        var config = builder.BuildConfigDictionary();

        Assert.False(config.ContainsKey("ExternalSkills"));
    }
}

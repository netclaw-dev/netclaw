// -----------------------------------------------------------------------
// <copyright file="ConfigEditorSessionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Config;
using Netclaw.Cli.Tui.Sections;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Sections;

public sealed class ConfigEditorSessionTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public ConfigEditorSessionTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Save_AppliesFieldActionsAndPreservesSiblings()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Daemon": {
                "ExposureMode": "reverse-proxy",
                "Host": "10.0.0.5",
                "Port": 5299
              },
              "Security": {
                "DeploymentPosture": "Team"
              }
            }
            """);

        var session = new ConfigEditorSession(_paths);
        session.Apply(new SectionContribution(
            FieldActions:
            [
                new SectionFieldAction("Daemon.ExposureMode", SectionFieldActionKind.Set, "local"),
                new SectionFieldAction("Daemon.Host", SectionFieldActionKind.Delete)
            ]));

        session.Save();

        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Daemon.ExposureMode", out var exposureMode));
        Assert.Equal("local", exposureMode);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Daemon.Port", out var port));
        Assert.Equal(5299L, port);
        Assert.False(ConfigFileHelper.TryGetPathValue(config, "Daemon.Host", out _));
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Security.DeploymentPosture", out var posture));
        Assert.Equal("Team", posture);
    }

    [Fact]
    public void Save_AppliesSecretActionsAndPreservesUnrelatedSecrets()
    {
        File.WriteAllText(_paths.SecretsPath,
            """
            {
              "configVersion": 1,
              "Providers": {
                "openai": {
                  "ApiKey": "stored-provider-key"
                }
              },
              "Slack": {
                "BotToken": "stored-slack-token"
              }
            }
            """);

        var session = new ConfigEditorSession(_paths);
        session.Apply(new SectionContribution(
            SecretActions:
            [
                new SectionSecretAction("Providers.openai.ApiKey", SectionSecretActionKind.Delete),
                new SectionSecretAction("Search.BraveApiKey", SectionSecretActionKind.Set, new SensitiveString("new-brave-key"))
            ]));

        session.Save();

        var serializedSecrets = File.ReadAllText(_paths.SecretsPath);
        Assert.DoesNotContain("new-brave-key", serializedSecrets, StringComparison.Ordinal);
        Assert.DoesNotContain("***REDACTED***", serializedSecrets, StringComparison.Ordinal);

        var secrets = ConfigFileHelper.LoadJsonDict(_paths.SecretsPath);
        Assert.False(ConfigFileHelper.TryGetPathValue(secrets, "Providers.openai.ApiKey", out _));
        Assert.True(ConfigFileHelper.TryGetPathValue(secrets, "Search.BraveApiKey", out var braveKey));
        Assert.Equal("new-brave-key", ConfigFileHelper.DecryptIfEncrypted(_paths, braveKey?.ToString()));
        Assert.True(ConfigFileHelper.TryGetPathValue(secrets, "Slack.BotToken", out var slackToken));
        Assert.Equal("stored-slack-token", ConfigFileHelper.DecryptIfEncrypted(_paths, slackToken?.ToString()));
    }

    [Fact]
    public void Save_SecretSetNormalizesColonPathAndRemovesLiteralCollision()
    {
        File.WriteAllText(_paths.SecretsPath,
            """
            {
              "configVersion": 1,
              "Search": {
                "BraveApiKey": "old-brave-key",
                "OtherSecret": "keep-search"
              },
              "Search:BraveApiKey": "literal-collision",
              "Slack": {
                "BotToken": "stored-slack-token"
              }
            }
            """);

        var session = new ConfigEditorSession(_paths);
        session.Apply(new SectionContribution(
            SecretActions:
            [
                new SectionSecretAction("Search:BraveApiKey", SectionSecretActionKind.Set, new SensitiveString("new-brave-key"))
            ]));

        session.Save();

        var serializedSecrets = File.ReadAllText(_paths.SecretsPath);
        Assert.DoesNotContain("\"Search:BraveApiKey\"", serializedSecrets, StringComparison.Ordinal);
        Assert.DoesNotContain("new-brave-key", serializedSecrets, StringComparison.Ordinal);

        var secrets = ConfigFileHelper.LoadJsonDict(_paths.SecretsPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(secrets, "Search.BraveApiKey", out var braveKey));
        Assert.Equal("new-brave-key", ConfigFileHelper.DecryptIfEncrypted(_paths, braveKey?.ToString()));
        Assert.True(ConfigFileHelper.TryGetPathValue(secrets, "Search.OtherSecret", out var otherSecret));
        Assert.Equal("keep-search", ConfigFileHelper.DecryptIfEncrypted(_paths, otherSecret?.ToString()));
        Assert.True(ConfigFileHelper.TryGetPathValue(secrets, "Slack.BotToken", out var slackToken));
        Assert.Equal("stored-slack-token", ConfigFileHelper.DecryptIfEncrypted(_paths, slackToken?.ToString()));
    }

    [Fact]
    public void Apply_StoresAndDeletesPassiveEditorState()
    {
        var session = new ConfigEditorSession(_paths);
        session.Apply(new SectionContribution(
            StateActions:
            [
                new SectionEditorStateAction("exposure", "ReverseProxy.Host", SectionEditorStateActionKind.Set, "10.0.0.5")
            ]));

        var state = new ConfigEditorStateStore(_paths);
        Assert.True(state.TryGetValue("exposure", "ReverseProxy.Host", out var storedHost));
        Assert.Equal("10.0.0.5", storedHost);

        session.Apply(new SectionContribution(
            StateActions:
            [
                new SectionEditorStateAction("exposure", "ReverseProxy.Host", SectionEditorStateActionKind.Delete)
            ]));

        Assert.False(state.TryGetValue("exposure", "ReverseProxy.Host", out _));
    }
}

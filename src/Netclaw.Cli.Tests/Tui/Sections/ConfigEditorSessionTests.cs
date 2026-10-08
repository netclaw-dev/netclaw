// -----------------------------------------------------------------------
// <copyright file="ConfigEditorSessionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
using Netclaw.Cli.Config;
using Netclaw.Cli.Tui.Sections;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
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

    // `netclaw config` and the redo flow save through this writer.
    [Fact]
    public void Save_keeps_the_mode_of_an_owner_only_config()
    {
        if (OperatingSystem.IsWindows())
            return; // file modes are a POSIX concept

        File.WriteAllText(_paths.NetclawConfigPath, """{ "configVersion": 1 }""");
        File.SetUnixFileMode(_paths.NetclawConfigPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var session = new ConfigEditorSession(_paths);
        session.Apply(new SectionContribution([new SectionFieldAction("Daemon.Port", SectionFieldActionKind.Set, 5299)]));
        session.Save();

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_paths.NetclawConfigPath));
    }

    // A link into a directory that cannot take a new file: the save replaces the link, as it did
    // before links were followed, and does not fail.
    [Fact]
    public void Save_replaces_a_symbolic_link_whose_target_directory_is_read_only()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
            return; // needs POSIX permissions and a non-root user

        var targetDir = Path.Combine(_dir.Path, "dotfiles");
        Directory.CreateDirectory(targetDir);
        var real = Path.Combine(targetDir, "netclaw.json");
        File.WriteAllText(real, """{ "configVersion": 1, "Security": { "DeploymentPosture": "Team" } }""");
        File.CreateSymbolicLink(_paths.NetclawConfigPath, real);
        File.SetUnixFileMode(targetDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var session = new ConfigEditorSession(_paths);
            session.Apply(new SectionContribution([new SectionFieldAction("Daemon.Port", SectionFieldActionKind.Set, 5299)]));
            session.Save();

            Assert.Null(new FileInfo(_paths.NetclawConfigPath).LinkTarget);
            Assert.Contains("5299", File.ReadAllText(_paths.NetclawConfigPath), StringComparison.Ordinal);
            Assert.Contains("Team", File.ReadAllText(_paths.NetclawConfigPath), StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(targetDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // Only "cannot write here" errors replace the link. A full disk (ENOSPC, 28) is a save error:
    // replacing the link would detach it from the file it points at.
    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 30)]  // EROFS, a read-only file system
    [InlineData(false, 16)]  // EBUSY, a single file mounted into a container
    [InlineData(false, 28)]  // ENOSPC, no space left
    [InlineData(false, 5)]   // EIO
    [InlineData(false, 0)]
    public void Only_permission_and_read_only_errors_replace_a_symbolic_link(bool unauthorized, int errno)
    {
        if (OperatingSystem.IsWindows())
            return; // errno values are POSIX

        Exception ex = unauthorized ? new UnauthorizedAccessException() : new IOException("write failed", errno);

        Assert.Equal(unauthorized || errno is 30 or 16, ConfigFileHelper.CanReplaceLink(ex));
    }

    [Fact]
    public void Save_writes_into_the_key_spelling_the_file_already_has()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """{ "daemon": { "port": 5000, "Host": "10.0.0.5" } }""");

        var session = new ConfigEditorSession(_paths);
        session.Apply(new SectionContribution(
        [
            new SectionFieldAction("Daemon.Port", SectionFieldActionKind.Set, 5299),
            new SectionFieldAction("Daemon.Host", SectionFieldActionKind.Delete)
        ]));
        session.Save();

        using var doc = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var daemon = Assert.Single(doc.RootElement.EnumerateObject(), p => p.Name.Equals("daemon", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("daemon", daemon.Name);
        Assert.Equal(5299, daemon.Value.GetProperty("port").GetInt32());
        Assert.False(daemon.Value.TryGetProperty("Host", out _));
    }

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
    public void Save_ReplaysSecretActionsAgainstLatestFileAndPreservesMcpTokenRefresh()
    {
        var protector = SecretsProtection.CreateProtector(_paths);
        SecretsFileWriter.Write(_paths.SecretsPath,
            """
            {
              "Slack": {
                "BotToken": "stored-slack-token"
              }
            }
            """,
            protector);

        var session = new ConfigEditorSession(_paths);

        SecretsFileWriter.Update(
            _paths.SecretsPath,
            (root, _) =>
            {
                root["McpOAuthTokens"] = new JsonObject
                {
                    ["memorizer"] = new JsonObject
                    {
                        ["AccessToken"] = "rotated-access-token"
                    }
                };
                return (root, true);
            },
            protector: protector,
            cancellationToken: TestContext.Current.CancellationToken);

        session.Apply(new SectionContribution(
            SecretActions:
            [
                new SectionSecretAction("Search.BraveApiKey", SectionSecretActionKind.Set, new SensitiveString("new-brave-key"))
            ]));

        session.Save();

        var decrypted = SecretsFileWriter.DecryptJsonLeaves(File.ReadAllText(_paths.SecretsPath), protector);
        using var doc = JsonDocument.Parse(decrypted);
        Assert.Equal("stored-slack-token", doc.RootElement.GetProperty("Slack").GetProperty("BotToken").GetString());
        Assert.Equal("new-brave-key", doc.RootElement.GetProperty("Search").GetProperty("BraveApiKey").GetString());
        Assert.Equal("rotated-access-token",
            doc.RootElement.GetProperty("McpOAuthTokens").GetProperty("memorizer").GetProperty("AccessToken").GetString());
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
    public void Apply_SecretSetThroughScalarIntermediate_RejectsMalformedSecrets()
    {
        // secrets.json has "Search" as a scalar string, not an object. ConfigEditorSession
        // deliberately refuses to traverse INTO a scalar at an intermediate path segment, rejecting
        // the write rather than silently overwriting the scalar. (SecretsJsonUpdater, the
        // JsonObject-based engine the wizard uses, instead overwrites.) This pins ConfigEditorSession's
        // stricter behavior so any future consolidation onto that engine is a conscious change.
        File.WriteAllText(_paths.SecretsPath,
            """
            {
              "configVersion": 1,
              "Search": "not-an-object"
            }
            """);

        var session = new ConfigEditorSession(_paths);

        Assert.ThrowsAny<JsonException>(() => session.Apply(new SectionContribution(
            SecretActions:
            [
                new SectionSecretAction("Search.BraveApiKey", SectionSecretActionKind.Set, new SensitiveString("new-brave-key"))
            ])));
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

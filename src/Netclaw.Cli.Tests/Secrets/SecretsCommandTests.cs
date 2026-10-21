// -----------------------------------------------------------------------
// <copyright file="SecretsCommandTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Secrets;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Secrets;

public sealed class SecretsCommandTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public SecretsCommandTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        _dir.Dispose();
    }

    [Fact]
    public void Set_a_provider_token_expiry_stores_it_unencrypted_and_says_so()
    {
        using var output = new StringWriter();

        var exitCode = SecretsCommand.Run(
            ["secrets", "set", "Providers.copilot.OAuthTokenExpiry", "2026-06-01T01:00:00+00:00"], _paths, output);

        Assert.Equal(0, exitCode);
        Assert.Contains("not encrypted", output.ToString(), StringComparison.Ordinal);
        using var document = JsonDocument.Parse(File.ReadAllText(_paths.SecretsPath));
        Assert.Equal("2026-06-01T01:00:00+00:00",
            document.RootElement.GetProperty("Providers").GetProperty("copilot").GetProperty("OAuthTokenExpiry").GetString());
    }

    [Fact]
    public void Set_a_provider_token_expiry_that_is_not_a_date_leaves_secrets_json_unchanged()
    {
        File.WriteAllText(_paths.SecretsPath, """{"Slack":{"BotToken":"xoxb-1"}}""");
        var before = File.ReadAllBytes(_paths.SecretsPath);
        using var output = new StringWriter();

        var exitCode = SecretsCommand.Run(
            ["secrets", "set", "Providers.copilot.OAuthTokenExpiry", "soon"], _paths, output);

        Assert.Equal(1, exitCode);
        Assert.Contains("must be an ISO 8601 date", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(_paths.SecretsPath));
    }

    [Theory]
    [InlineData("set")]
    [InlineData("add")]
    public void Run_ColonDelimitedPath_UpsertsNestedSecretAndRemovesLiteralCollision(string subcommand)
    {
        File.WriteAllText(_paths.SecretsPath,
            """
            {
              "Discord": {
                "BotToken": "old-token"
              },
              "Discord:BotToken": "literal-collision"
            }
            """);

        using var output = new StringWriter();

        var exitCode = SecretsCommand.Run(["secrets", subcommand, "Discord:BotToken", "new-token"], _paths, output);

        Assert.Equal(0, exitCode);
        Assert.Contains("Set Discord.BotToken", output.ToString());

        var encryptedJson = File.ReadAllText(_paths.SecretsPath);
        Assert.DoesNotContain("\"Discord:BotToken\"", encryptedJson, StringComparison.Ordinal);

        new ConfigurationBuilder()
            .AddJsonFile(_paths.SecretsPath, optional: false, reloadOnChange: false)
            .Build();

        var protector = SecretsProtection.CreateProtector(_paths);
        var decryptedJson = SecretsFileWriter.DecryptJsonLeaves(encryptedJson, protector);
        using var document = JsonDocument.Parse(decryptedJson);

        Assert.Equal("new-token", document.RootElement.GetProperty("Discord").GetProperty("BotToken").GetString());
        Assert.False(document.RootElement.TryGetProperty("Discord:BotToken", out _));
    }
}

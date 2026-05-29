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

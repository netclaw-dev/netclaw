// -----------------------------------------------------------------------
// <copyright file="CliConfigPreflightTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Netclaw.Cli;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

public sealed class CliConfigPreflightTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public CliConfigPreflightTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void TryWriteMissingConfig_Text_PrintsInitGuidanceAndBlocksCommand()
    {
        using var writer = new StringWriter();

        var blocked = CliConfigPreflight.TryWriteMissingConfig(_paths, jsonOutput: false, writer, out var exitCode);

        Assert.True(blocked);
        Assert.Equal(1, exitCode);
        Assert.Equal(CliConfigPreflight.MissingConfigMessage, writer.ToString().Trim());
    }

    [Fact]
    public void TryWriteMissingConfig_Json_PrintsNotConfiguredPayload()
    {
        using var writer = new StringWriter();

        var blocked = CliConfigPreflight.TryWriteMissingConfig(_paths, jsonOutput: true, writer, out var exitCode);

        var json = JsonNode.Parse(writer.ToString())!.AsObject();

        Assert.True(blocked);
        Assert.Equal(1, exitCode);
        Assert.Equal("not-configured", json["overall"]!.GetValue<string>());
        Assert.Equal(CliConfigPreflight.MissingConfigMessage, json["message"]!.GetValue<string>());
    }

    [Fact]
    public void TryWriteMissingConfig_ConfigExists_AllowsCommand()
    {
        File.WriteAllText(_paths.NetclawConfigPath, "{}");
        using var writer = new StringWriter();

        var blocked = CliConfigPreflight.TryWriteMissingConfig(_paths, jsonOutput: false, writer, out var exitCode);

        Assert.False(blocked);
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, writer.ToString());
    }

    [Fact]
    public void TryWriteMissingChatConfig_HeadlessJson_PrintsJsonPayload()
    {
        using var writer = new StringWriter();

        var blocked = CliConfigPreflight.TryWriteMissingChatConfig(
            _paths,
            mode: "headless",
            chatJsonOutput: true,
            writer,
            out var exitCode);

        var json = JsonNode.Parse(writer.ToString())!.AsObject();

        Assert.True(blocked);
        Assert.Equal(1, exitCode);
        Assert.Equal("not-configured", json["overall"]!.GetValue<string>());
        Assert.Equal(CliConfigPreflight.MissingConfigMessage, json["message"]!.GetValue<string>());
    }
}

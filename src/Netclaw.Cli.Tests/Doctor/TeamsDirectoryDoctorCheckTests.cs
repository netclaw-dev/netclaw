// -----------------------------------------------------------------------
// <copyright file="TeamsDirectoryDoctorCheckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class TeamsDirectoryDoctorCheckTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public TeamsDirectoryDoctorCheckTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Returns_warning_when_enabled_direct_messages_have_no_global_principal_allow_list()
    {
        WriteConfig(new
        {
            Teams = new
            {
                Enabled = true,
                TenantId = "tenant-a",
                ClientId = "client-a",
                BotId = "bot-a",
                AllowedTeamIds = new[] { "team-a" },
                AllowDirectMessages = true
            }
        });

        var result = await new TeamsDirectoryDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("fail closed", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Returns_permission_guidance_for_group_authorization()
    {
        WriteConfig(new
        {
            Teams = new
            {
                Enabled = true,
                TenantId = "tenant-a",
                ClientId = "client-a",
                BotId = "bot-a",
                AllowedChannelIds = new[] { "channel-a" },
                AllowedGroupIds = new[] { "group-a" }
            }
        });

        var result = await new TeamsDirectoryDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("Graph consent", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Returns_warning_when_enabled_group_chats_have_no_chat_ids()
    {
        WriteConfig(new
        {
            Teams = new
            {
                Enabled = true,
                TenantId = "tenant-a",
                ClientId = "client-a",
                BotId = "bot-a",
                AllowGroupChats = true,
                AllowedUserIds = new[] { "user-a" }
            }
        });

        var result = await new TeamsDirectoryDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("no allowed chat IDs", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Returns_warning_when_group_chats_use_a_noncanonical_chat_id()
    {
        WriteConfig(new
        {
            Teams = new
            {
                Enabled = true,
                TenantId = "tenant-a",
                ClientId = "client-a",
                BotId = "bot-a",
                AllowGroupChats = true,
                AllowedGroupChatIds = new[] { "Operations chat" },
                AllowedUserIds = new[] { "user-a" }
            }
        });

        var result = await new TeamsDirectoryDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("noncanonical", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Returns_warning_when_group_chats_have_no_global_principal()
    {
        WriteConfig(new
        {
            Teams = new
            {
                Enabled = true,
                TenantId = "tenant-a",
                ClientId = "client-a",
                BotId = "bot-a",
                AllowGroupChats = true,
                AllowedGroupChatIds = new[] { "19:group-a@thread.v2" }
            }
        });

        var result = await new TeamsDirectoryDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("no global user or group", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reports_optional_group_chat_metadata_consent()
    {
        WriteConfig(new
        {
            Teams = new
            {
                Enabled = true,
                TenantId = "tenant-a",
                ClientId = "client-a",
                BotId = "bot-a",
                AllowGroupChats = true,
                AllowedGroupChatIds = new[] { "19:group-a@thread.v2" },
                AllowedUserIds = new[] { "user-a" }
            }
        });

        var result = await new TeamsDirectoryDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("Chat.ReadBasic.All is optional", result.Message, StringComparison.Ordinal);
    }

    private void WriteConfig(object config)
        => File.WriteAllText(_paths.NetclawConfigPath,
            JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
}

// -----------------------------------------------------------------------
// <copyright file="ChannelsConfigNavigationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Channels;
using Netclaw.Channels.Slack;
using Netclaw.Channels.Teams;
using Netclaw.Cli.Config;
using Netclaw.Cli.Discord;
using Netclaw.Cli.Tests.Tui;
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Config;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using R3;
using Termina;
using Termina.Hosting;
using Termina.Input;
using Termina.Terminal;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Config;

public sealed class ChannelsConfigNavigationTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public ChannelsConfigNavigationTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        File.WriteAllText(_paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Slack": { "Enabled": true, "AllowedChannelIds": ["C01"] },
              "Discord": { "Enabled": true, "AllowedChannelIds": ["123456789"] },
              "Mattermost": {
                "Enabled": true,
                "ServerUrl": "https://mattermost.example.com",
                "AllowedChannelIds": ["town-square"]
              }
            }
            """);
        File.WriteAllText(_paths.SecretsPath,
            """
            {
              "configVersion": 1,
              "Slack": { "BotToken": "xoxb-existing", "AppToken": "xapp-existing" },
              "Discord": { "BotToken": "discord-existing" },
              "Mattermost": { "BotToken": "mattermost-existing" }
            }
            """);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Channels_Escape_ReturnsToDashboardUsingTerminaHistory()
    {
        var app = CreateHeadlessApp(out var input, out var dashboardVm, out var getChannelsVm);
        dashboardVm.SelectedIndex.Value = dashboardVm.Items
            .Select((item, index) => (item, index))
            .Single(entry => entry.item.Label == "Channels")
            .index;

        dashboardVm.ActivateSelected();
        input.EnqueueKey(ConsoleKey.Escape);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.NotNull(getChannelsVm());
        Assert.Equal("/config", app.CurrentPath);
    }

    [Fact]
    public async Task Channels_DoneAddingChannelsRow_ReturnsToDashboardUsingTerminaHistory()
    {
        var app = CreateHeadlessApp(out var input, out var dashboardVm, out var getChannelsVm);
        OpenChannels(dashboardVm);

        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.NotNull(getChannelsVm());
        Assert.Equal("/config", app.CurrentPath);
    }

    [Theory]
    [InlineData(ChannelType.Slack)]
    [InlineData(ChannelType.Discord)]
    [InlineData(ChannelType.Mattermost)]
    public async Task Channels_RotateCredentials_AcceptsTypedCredentialInput(ChannelType channelType)
    {
        var app = CreateHeadlessApp(out var input, out var dashboardVm, out var getChannelsVm);
        OpenChannels(dashboardVm);
        MoveToAdapter(input, channelType);

        input.EnqueueKey(ConsoleKey.Enter); // Open configured adapter management.
        MoveToRotateCredentials(input);
        input.EnqueueKey(ConsoleKey.Enter); // Rotate credentials.
        TypeCredentials(input, channelType);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var channelsVm = Assert.IsType<ChannelsConfigViewModel>(getChannelsVm());
        AssertPersistedCredentials(channelType, typed: true);
        Assert.Equal("Credential changes saved.", channelsVm.Status.Value.Text);
    }

    [Theory]
    [InlineData(ChannelType.Slack)]
    [InlineData(ChannelType.Discord)]
    [InlineData(ChannelType.Mattermost)]
    public async Task Channels_FirstTimeAdapterSetup_AcceptsTypedCredentialInput(ChannelType channelType)
    {
        WriteEmptyChannelFiles();
        var app = CreateHeadlessApp(out var input, out var dashboardVm, out var getChannelsVm);
        OpenChannels(dashboardVm);
        MoveToAdapter(input, channelType);

        input.EnqueueKey(ConsoleKey.Enter); // Enable selected adapter and enter first-time setup.
        TypeFirstTimeSetup(input, channelType);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var channelsVm = Assert.IsType<ChannelsConfigViewModel>(getChannelsVm());
        Assert.Equal(ChannelsConfigScreen.ChannelPermissions, channelsVm.Screen.Value);
        Assert.Equal(channelType, channelsVm.ActiveAdapterType);
        AssertFirstTimeSetupPersisted(channelsVm, channelType);
    }

    [Fact]
    public async Task Channels_FirstTimeSlackSetup_AcceptsPastedCredentialInput()
    {
        WriteEmptyChannelFiles();
        var app = CreateHeadlessApp(out var input, out var dashboardVm, out var getChannelsVm);
        OpenChannels(dashboardVm);

        input.EnqueueKey(ConsoleKey.Enter); // Enable Slack and enter first-time setup.
        input.EnqueuePaste("xoxb-pasted-token");
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueuePaste("xapp-pasted-token");
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var channelsVm = Assert.IsType<ChannelsConfigViewModel>(getChannelsVm());
        var slack = channelsVm.Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack);
        Assert.Equal("xoxb-pasted-token", slack.BotToken);
        Assert.Equal("xapp-pasted-token", slack.AppToken);
    }

    [Fact]
    public async Task Channels_SlackAllowedUserIds_AcceptsPasteAfterTyping()
    {
        // Regression: Termina auto-routes a bracketed paste straight into the focused
        // TextInputNode, bypassing the page's PasteEvent handler. Because the node is rebuilt
        // and re-seeded from the view-model every render, a paste that lands only in the node
        // was wiped by the next reseed unless it was synced back. After typing one ID by hand,
        // pasting a second must land in the view-model immediately (via TextChanged sync).
        WriteEmptyChannelFiles();
        var app = CreateHeadlessApp(out var input, out var dashboardVm, out var getChannelsVm);
        OpenChannels(dashboardVm);

        input.EnqueueKey(ConsoleKey.Enter); // Enable Slack -> bot token substep.
        input.EnqueuePaste("xoxb-token");
        input.EnqueueKey(ConsoleKey.Enter); // -> app token substep.
        input.EnqueuePaste("xapp-token");
        input.EnqueueKey(ConsoleKey.Enter); // -> channel names substep.
        input.EnqueueKey(ConsoleKey.Enter); // skip channel names -> DM substep.
        input.EnqueueKey(ConsoleKey.Enter); // DM default -> user access choice substep.
        input.EnqueueKey(ConsoleKey.Enter); // "Restrict to specific users" default -> allowed user IDs substep.
        input.EnqueueString("U044U1S8P,");  // Type the first ID by hand.
        input.EnqueuePaste("U12345678");    // Then paste a second ID into the non-empty field.
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var channelsVm = Assert.IsType<ChannelsConfigViewModel>(getChannelsVm());
        var slack = channelsVm.Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack);
        Assert.Equal("U044U1S8P,U12345678", slack.AllowedUserIdsInput);
    }

    [Fact]
    public async Task Channels_SlackBotToken_AcceptsPasteAfterTyping()
    {
        // Same auto-routed-paste regression for a credential field, which syncs through the
        // BotTokenDraft path: type a token prefix, then paste the rest into the non-empty field.
        WriteEmptyChannelFiles();
        var app = CreateHeadlessApp(out var input, out var dashboardVm, out var getChannelsVm);
        OpenChannels(dashboardVm);

        input.EnqueueKey(ConsoleKey.Enter); // Enable Slack -> bot token substep.
        input.EnqueueString("xoxb-");        // Type the prefix by hand.
        input.EnqueuePaste("0123456789");    // Paste the rest into the non-empty field.
        input.EnqueueKey(ConsoleKey.Enter);  // Submit -> advances, capturing the full token.
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var channelsVm = Assert.IsType<ChannelsConfigViewModel>(getChannelsVm());
        var slack = channelsVm.Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack);
        Assert.Equal("xoxb-0123456789", slack.BotToken);
    }

    [Fact]
    public async Task Channels_AddChannel_AcceptsPastedChannelInput()
    {
        var app = CreateHeadlessApp(out var input, out var dashboardVm, out var getChannelsVm);
        OpenChannels(dashboardVm);

        input.EnqueueKey(ConsoleKey.Enter); // Open configured Slack management.
        input.EnqueueKey(ConsoleKey.DownArrow); // Add channel.
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueuePaste("#C09");
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var channelsVm = Assert.IsType<ChannelsConfigViewModel>(getChannelsVm());
        Assert.Contains(channelsVm.GetChannelRows(), row => row.Id == "C09" && !row.IsAddAction);
        Assert.Equal("Added C09 at the Team default and saved.", channelsVm.Status.Value.Text);
    }

    [Fact]
    public async Task Channels_Teams_user_search_accepts_typed_and_pasted_query_input()
    {
        WriteTeamsChannelFiles();
        var app = CreateHeadlessApp(out var input, out var dashboardVm, out var getChannelsVm);
        OpenChannels(dashboardVm);

        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter); // Microsoft Teams management.
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter); // Add users or groups.
        input.EnqueueKey(ConsoleKey.Enter); // User search.
        input.EnqueueString("Ada");
        input.EnqueuePaste(" Lovelace");
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var channelsVm = Assert.IsType<ChannelsConfigViewModel>(getChannelsVm());
        Assert.Equal(ChannelsConfigScreen.TeamsUserSearch, channelsVm.Screen.Value);
        Assert.Equal("Ada Lovelace", channelsVm.DirectorySearchInput);
        Assert.Empty(channelsVm.UserSearchResults);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Channels_Group_Chat_ingress_menu_saves_and_reloads_at_80_by_24(bool initiallyEnabled)
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            $$"""
            {
              "configVersion": 1,
              "Teams": {
                "Enabled": true,
                "TenantId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "ClientId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                "BotId": "cccccccc-cccc-cccc-cccc-cccccccccccc",
                "AllowGroupChats": {{initiallyEnabled.ToString().ToLowerInvariant()}},
                "AllowedGroupChatIds": ["19:boston@thread.v2", "19:operations@thread.v2"],
                "AllowedTeamIds": ["dddddddd-dddd-dddd-dddd-dddddddddddd"],
                "AllowedChannelIds": ["19:channel@thread.tacv2"],
                "AllowedUserIds": ["11111111-1111-1111-1111-111111111111"],
                "AllowedGroupIds": ["22222222-2222-2222-2222-222222222222"],
                "ChannelAccessOverrides": [{
                  "TeamId": "dddddddd-dddd-dddd-dddd-dddddddddddd",
                  "ChannelId": "19:channel@thread.tacv2",
                  "AllowedUserIds": ["33333333-3333-3333-3333-333333333333"],
                  "AllowedGroupIds": ["44444444-4444-4444-4444-444444444444"]
                }],
                "MentionOnly": true,
                "AllowDirectMessages": false,
                "AllowAttachments": true
              }
            }
            """);
        ConfigFileHelper.WriteSecretsFile(_paths, new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Teams"] = new Dictionary<string, object> { ["ClientSecret"] = "teams-test-secret" }
        });
        using var before = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var secretsBefore = File.ReadAllText(_paths.SecretsPath);
        var app = CreateHeadlessApp(out var input, out var dashboardVm, out var getChannelsVm, out var terminal,
            terminalWidth: 80, terminalHeight: 24);
        OpenChannels(dashboardVm);
        MoveToAdapter(input, ChannelType.Teams);
        input.EnqueueKey(ConsoleKey.Enter);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = app.RunAsync(cts.Token);
        try
        {
            var initialLabel = initiallyEnabled ? "Group Chat ingress: ON" : "Group Chat ingress: OFF";
            var frame = await WaitForFrameAsync(app, terminal,
                snapshot => snapshot.Contains(initialLabel, StringComparison.Ordinal), cts.Token);
            Assert.Contains("Connection & credentials", frame);
            Assert.Contains("Done", frame);

            input.EnqueueKey(ConsoleKey.DownArrow);
            input.EnqueueKey(ConsoleKey.DownArrow);
            input.EnqueueKey(ConsoleKey.Enter);
            await WaitForFrameAsync(app, terminal,
                snapshot => snapshot.Contains("Enable Group Chat ingress", StringComparison.Ordinal)
                            && snapshot.Contains("19:boston@thread.v2", StringComparison.Ordinal), cts.Token);
            input.EnqueueKey(ConsoleKey.Spacebar);
            input.EnqueueKey(ConsoleKey.Enter);
            var savedLabel = initiallyEnabled ? "Group Chat ingress: OFF" : "Group Chat ingress: ON";
            await WaitForFrameAsync(app, terminal,
                snapshot => getChannelsVm()?.Screen.Value == ChannelsConfigScreen.AdapterMenu
                            && snapshot.Contains(savedLabel, StringComparison.Ordinal), cts.Token);
            await getChannelsVm()!.PendingConfigWrite.WaitAsync(cts.Token);
        }
        finally
        {
            input.EnqueueKey(ConsoleKey.Q, false, false, true);
            await run;
        }

        using var after = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var savedTeams = after.RootElement.GetProperty("Teams");
        Assert.Equal(!initiallyEnabled, savedTeams.GetProperty("AllowGroupChats").GetBoolean());
        foreach (var property in before.RootElement.GetProperty("Teams").EnumerateObject())
        {
            if (property.Name != "AllowGroupChats")
                Assert.True(JsonElement.DeepEquals(property.Value, savedTeams.GetProperty(property.Name)), property.Name);
        }
        Assert.Equal(secretsBefore, File.ReadAllText(_paths.SecretsPath));
        Assert.False(savedTeams.TryGetProperty("ClientSecret", out _));

        using var runtimeConfig = new ConfigurationManager();
        runtimeConfig.AddJsonFile(_paths.NetclawConfigPath);
        var runtimeOptions = runtimeConfig.GetSection("Teams").Get<TeamsChannelOptions>()!;
        var activity = new TeamsInboundActivity(
            new TeamsIngressTrustContext(
                TrustAudience.Public,
                PrincipalClassification.UntrustedExternal,
                TrustBoundary.Public,
                new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Community),
                "11111111-1111-1111-1111-111111111111",
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "19:boston@thread.v2",
                TeamsConversationScope.GroupChat,
                "toggle-check",
                TimeProvider.System.GetUtcNow()),
            "hello",
            isMentioned: true);
        var decision = TeamsGroupChatAclPolicy.Evaluate(activity, runtimeOptions);
        Assert.Equal(!initiallyEnabled, decision.IsAllowed);
        Assert.Equal(initiallyEnabled ? "group_chats_disabled" : null, decision.DenyReason);

        using var reloaded = new ChannelsConfigViewModel(_paths,
            new FakeSlackProbe(), new FakeDiscordProbe(), new FakeMattermostProbe(), TimeProvider.System);
        reloaded.OpenAdapterManagement(ChannelType.Teams);
        reloaded.MoveManagementMenu(2);
        reloaded.ActivateManagementMenuItem();
        Assert.Equal(ChannelsConfigScreen.GroupChats, reloaded.Screen.Value);
        Assert.Equal(!initiallyEnabled, reloaded.GroupChatsEnabled);
        Assert.Equal("19:boston@thread.v2, 19:operations@thread.v2", reloaded.AllowedGroupChatsInput);
    }

    [Theory]
    [InlineData("boston", "tech")]
    [InlineData("BostonTech ", "Operations")]
    public async Task Channels_Group_Chat_submits_typed_and_pasted_names_to_chat_search(string typed, string pasted)
    {
        WriteTeamsChannelFiles();
        var searched = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var directory = new GroupChatNameSearchDirectory
        {
            SearchHandler = (query, _) =>
            {
                searched.TrySetResult(query);
                return ValueTask.FromResult(TeamsDirectoryOperationResult<TeamsDirectoryGroupChatSearchPage>.Available(
                    new([new("19:boston-operations@thread.v2", "BostonTech Operations", ["Ada", "Grace"])], null, 2, 0, 3, 4)));
            }
        };
        var app = CreateHeadlessApp(out var input, out var dashboardVm, out var getChannelsVm, out var terminal,
            teamsDirectoryFactory: _ => directory);
        OpenChannels(dashboardVm);
        MoveToAdapter(input, ChannelType.Teams);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter); // Add a channel or Group Chat.
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter); // Search Group Chats by name.
        input.EnqueueString(typed);
        input.EnqueuePaste(pasted);
        input.EnqueueKey(ConsoleKey.Enter);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = app.RunAsync(cts.Token);
        string submittedQuery;
        try
        {
            submittedQuery = await searched.Task.WaitAsync(cts.Token);
        }
        finally
        {
            input.EnqueueKey(ConsoleKey.Q, false, false, true);
            await run;
        }

        var channelsVm = Assert.IsType<ChannelsConfigViewModel>(getChannelsVm());
        Assert.Equal(typed + pasted, submittedQuery);
        Assert.Equal(typed + pasted, channelsVm.GroupChatSearchInput);
        Assert.Equal(ChannelsConfigScreen.TeamsGroupChatSearch, channelsVm.Screen.Value);
        Assert.Equal(0, directory.UserSearchCalls);
        Assert.Contains("Microsoft Teams > Find a Group Chat", terminal.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Channels_Group_Chat_can_stop_resume_and_select_later_results_in_an_80_by_24_terminal(bool stopWithShortcut)
    {
        WriteTeamsChannelFiles();
        var firstPage = Enumerable.Range(0, 25)
            .Select(index => new TeamsDirectoryGroupChat($"19:boston-{index}@thread.v2", $"BostonTech {index}", []))
            .ToArray();
        var heldPage = new TaskCompletionSource<TeamsDirectoryOperationResult<TeamsDirectoryGroupChatSearchPage>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var nextStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextAttempts = 0;
        var laterPage = TeamsDirectoryOperationResult<TeamsDirectoryGroupChatSearchPage>.Available(
            new([new("19:boston-later@thread.v2", "BostonTech Later", [])], null, 10, 0, 20, 40));
        var directory = new GroupChatNameSearchDirectory
        {
            SearchHandler = (_, continuation) =>
            {
                if (continuation is null)
                    return ValueTask.FromResult(TeamsDirectoryOperationResult<TeamsDirectoryGroupChatSearchPage>.Available(
                        new(firstPage, "next-page", 5, 0, 10, 25)));
                if (++nextAttempts > 1)
                    return ValueTask.FromResult(laterPage);
                nextStarted.TrySetResult();
                return new(heldPage.Task);
            }
        };
        var app = CreateHeadlessApp(out var input, out var dashboardVm, out var getChannelsVm, out var terminal,
            teamsDirectoryFactory: _ => directory, terminalWidth: 80, terminalHeight: 24);
        OpenChannels(dashboardVm);
        MoveToAdapter(input, ChannelType.Teams);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueString("BostonTech");
        input.EnqueueKey(ConsoleKey.Enter);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = app.RunAsync(cts.Token);
        try
        {
            await WaitForFrameAsync(app, terminal, frame => frame.Contains("BostonTech 0", StringComparison.Ordinal), cts.Token);
            await nextStarted.Task.WaitAsync(cts.Token);
            for (var index = 0; index < firstPage.Length; index++)
                input.EnqueueKey(ConsoleKey.DownArrow);

            var frame = await WaitForFrameAsync(
                app,
                terminal,
                snapshot => getChannelsVm()?.DirectoryResultIndex == firstPage.Length
                            && snapshot.Contains("Stop search (Ctrl+S)", StringComparison.Ordinal)
                            && snapshot.Contains("Advanced canonical-ID entry", StringComparison.Ordinal)
                            && snapshot.Contains("[Type] Chat name", StringComparison.Ordinal),
                cts.Token);
            Assert.Contains("BostonTech 24", frame);
            Assert.DoesNotContain("BostonTech 0", frame);
            input.EnqueueKey(ConsoleKey.Enter); // A second Enter must not restart an active search.
            if (stopWithShortcut)
                input.EnqueueKey(ConsoleKey.S, false, false, true);
            else
            {
                input.EnqueueKey(ConsoleKey.DownArrow);
                input.EnqueueKey(ConsoleKey.Enter);
            }

            await WaitForFrameAsync(app, terminal, snapshot => snapshot.Contains("Resume search", StringComparison.Ordinal), cts.Token);
            Assert.Equal(2, directory.SearchCalls.Count);
            var stoppedTask = getChannelsVm()!.PendingGroupChatSearch!;
            heldPage.TrySetResult(laterPage);
            await stoppedTask.WaitAsync(cts.Token);
            Assert.Equal(firstPage.Length, getChannelsVm()!.GroupChatSearchResults.Count);
            input.EnqueueKey(ConsoleKey.Enter);
            await WaitForFrameAsync(app, terminal,
                snapshot => getChannelsVm()?.IsGroupChatSearchRunning == false
                            && snapshot.Contains("BostonTech Later", StringComparison.Ordinal), cts.Token);
            input.EnqueueKey(ConsoleKey.UpArrow);
            input.EnqueueKey(ConsoleKey.Enter);
            await WaitForFrameAsync(app, terminal, _ => getChannelsVm()?.Screen.Value == ChannelsConfigScreen.GroupChats, cts.Token);

            var channelsVm = Assert.IsType<ChannelsConfigViewModel>(getChannelsVm());
            Assert.Equal("19:boston-later@thread.v2", channelsVm.AllowedGroupChatsInput);
            Assert.Equal([("BostonTech", (string?)null), ("BostonTech", "next-page"), ("BostonTech", "next-page")], directory.SearchCalls);
        }
        finally
        {
            heldPage.TrySetResult(laterPage);
            input.EnqueueKey(ConsoleKey.Q, false, false, true);
            await run;
        }
    }

    private static Task<string> WaitForFrameAsync(
        TerminaApplication app,
        VirtualTerminal terminal,
        Func<string, bool> predicate,
        CancellationToken cancellationToken)
        => Observable.EveryUpdate(app.RenderFrameProvider, cancellationToken)
            .Select(_ => terminal.ToString())
            .FirstAsync(predicate, cancellationToken);

    [Fact]
    public async Task Channels_ChannelPermissions_DoesNotRemoveSelectedChannelWithDoneKey()
    {
        var app = CreateHeadlessApp(out var input, out var dashboardVm, out var getChannelsVm);
        OpenChannels(dashboardVm);

        input.EnqueueKey(ConsoleKey.Enter); // Open configured Slack management.
        input.EnqueueKey(ConsoleKey.Enter); // Manage channels and permissions.
        input.EnqueueKey(ConsoleKey.D);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var channelsVm = Assert.IsType<ChannelsConfigViewModel>(getChannelsVm());
        Assert.Contains(channelsVm.GetChannelRows(), row => row.Id == "C01" && !row.IsAddAction);
    }

    [Fact]
    public async Task Channels_ChannelPermissions_DoneRow_ReturnsToAdapterMenu()
    {
        var app = CreateHeadlessApp(out var input, out var dashboardVm, out var getChannelsVm);
        OpenChannels(dashboardVm);
        MoveToAdapter(input, ChannelType.Discord);

        input.EnqueueKey(ConsoleKey.Enter); // Open configured Discord management.
        input.EnqueueKey(ConsoleKey.Enter); // Manage channels and permissions.
        input.EnqueueKey(ConsoleKey.DownArrow); // + Add channel.
        input.EnqueueKey(ConsoleKey.DownArrow); // Done adding channels.
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var channelsVm = Assert.IsType<ChannelsConfigViewModel>(getChannelsVm());
        Assert.Equal(ChannelsConfigScreen.AdapterMenu, channelsVm.Screen.Value);
        Assert.Equal("Done adding channels. Completed changes are already saved.", channelsVm.Status.Value.Text);
    }

    [Fact]
    public async Task Channels_ChannelPermissions_DeleteRemovesSelectedChannel()
    {
        var app = CreateHeadlessApp(out var input, out var dashboardVm, out var getChannelsVm);
        OpenChannels(dashboardVm);

        input.EnqueueKey(ConsoleKey.Enter); // Open configured Slack management.
        input.EnqueueKey(ConsoleKey.Enter); // Manage channels and permissions.
        input.EnqueueKey(ConsoleKey.Delete);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var channelsVm = Assert.IsType<ChannelsConfigViewModel>(getChannelsVm());
        Assert.DoesNotContain(channelsVm.GetChannelRows(), row => row.Id == "C01");
        Assert.Equal("Removed C01 and saved.", channelsVm.Status.Value.Text);
    }

    [Fact]
    public async Task Channels_ChannelPermissions_RendersResolvedDiscordLabelWithoutRawId()
    {
        var discordProbe = new FakeDiscordProbe
        {
            NextResolutionResult = new DiscordChannelResolutionResult(
                true,
                null,
                [new ResolvedDiscordChannel("123456789", "general", "NetclawTest")],
                [])
        };
        var app = CreateHeadlessApp(
            out var input,
            out var dashboardVm,
            out _,
            out var terminal,
            discordProbe: discordProbe);
        OpenChannels(dashboardVm);
        MoveToAdapter(input, ChannelType.Discord);

        input.EnqueueKey(ConsoleKey.Enter); // Open configured Discord management.
        input.EnqueueKey(ConsoleKey.Enter); // Manage channels and permissions.
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var screen = terminal.ToString();
        Assert.Contains("NetclawTest / #general", screen);
        Assert.DoesNotContain("123456789", screen);
    }

    [Fact]
    public async Task Channels_FirstTimeSlackBotToken_ShowsValidationError()
    {
        WriteEmptyChannelFiles();
        var app = CreateHeadlessApp(out var input, out var dashboardVm, out var getChannelsVm);
        OpenChannels(dashboardVm);

        input.EnqueueKey(ConsoleKey.Enter); // Enable Slack and enter first-time setup.
        input.EnqueueString("not-a-slack-token");
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var channelsVm = Assert.IsType<ChannelsConfigViewModel>(getChannelsVm());
        var slack = channelsVm.Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack);
        Assert.Equal(ChannelsEditorValidationMessages.SlackBotTokenPrefix, channelsVm.Status.Value.Text);
        Assert.Equal(ConfigStatusTone.Error, channelsVm.Status.Value.Tone);
        Assert.Equal(1, slack.CurrentSubStep);
        Assert.Null(slack.BotToken);
    }

    [Fact]
    public async Task Channels_RotateCredentials_InvalidSlackBotToken_ShowsValidationError()
    {
        var app = CreateHeadlessApp(out var input, out var dashboardVm, out var getChannelsVm);
        OpenChannels(dashboardVm);

        input.EnqueueKey(ConsoleKey.Enter); // Open configured Slack management.
        MoveToRotateCredentials(input);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueString("not-a-slack-token");
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var channelsVm = Assert.IsType<ChannelsConfigViewModel>(getChannelsVm());
        var slack = channelsVm.Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack);
        Assert.Equal(ChannelsConfigScreen.RotateCredentials, channelsVm.Screen.Value);
        Assert.Equal(ChannelsEditorValidationMessages.SlackBotTokenPrefix, channelsVm.Status.Value.Text);
        Assert.Equal(ConfigStatusTone.Error, channelsVm.Status.Value.Tone);
        Assert.Null(slack.BotToken);
    }

    [Fact]
    public async Task Channels_EnableSlackByName_thenDiscordById_persistsBothSectionsAndSecrets()
    {
        // End-to-end reproduction of the reported live trace: a fresh config, enable
        // Slack through the picker sub-flow entering a channel NAME (resolved to an ID
        // on the completion autosave), then enable Discord entering a channel ID, then
        // Escape back to the dashboard. Both sections + bot tokens must survive on disk.
        WriteEmptyChannelFiles();
        var slackProbe = new FakeSlackProbe
        {
            NextResolutionResult = new SlackChannelResolutionResult(
                true, null, [new ResolvedSlackChannel("general", "C100")], [])
        };
        var discordProbe = new FakeDiscordProbe
        {
            NextResolutionResult = new DiscordChannelResolutionResult(
                true, null, [new ResolvedDiscordChannel("555000111", "ops", "Guild")], [])
        };
        var app = CreateHeadlessApp(
            out var input,
            out var dashboardVm,
            out var getChannelsVm,
            out _,
            slackProbe: slackProbe,
            discordProbe: discordProbe);
        OpenChannels(dashboardVm);

        // Slack: Enter to enable + enter sub-flow, type tokens + channel NAME.
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueString("xoxb-live");
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueString("xapp-live");
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueString("general"); // NAME, not ID.
        input.EnqueueKey(ConsoleKey.Enter);
        SelectSecondOption(input); // Disable DMs.
        SelectSecondOption(input); // Allow anyone.
        // Now on ChannelPermissions for Slack; go back to the picker.
        input.EnqueueKey(ConsoleKey.Escape); // ChannelPermissions -> AdapterMenu.
        input.EnqueueKey(ConsoleKey.Escape); // AdapterMenu -> Picker.

        // Discord: move down, Enter to enable + sub-flow, type token + channel ID.
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueString("discord-live");
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueString("555000111");
        input.EnqueueKey(ConsoleKey.Enter);
        SelectSecondOption(input); // Disable DMs.
        SelectSecondOption(input); // Allow anyone.
        input.EnqueueKey(ConsoleKey.Escape); // ChannelPermissions -> AdapterMenu.
        input.EnqueueKey(ConsoleKey.Escape); // AdapterMenu -> Picker.

        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var channelsVm = Assert.IsType<ChannelsConfigViewModel>(getChannelsVm());

        // The sub-flow's channel-NAME resolution runs as a fire-and-forget background probe that marshals the
        // reconcile (name -> id on disk) back onto the loop via InvokeAsync. This script quits (Ctrl+Q) right
        // after the last sub-flow, which can race the loop running that final apply. The real loop has stopped
        // here, so settle the canonicalization deterministically on this (loop-equivalent) thread the way the
        // next frame would have — drive the inline resolution path, which probes and applies synchronously.
        if (channelsVm.PendingLabelRefresh is { } pendingRefresh)
            await pendingRefresh;
        await channelsVm.RefreshChannelLabelsAsync(ChannelType.Slack, TestContext.Current.CancellationToken);

        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        var secrets = ConfigFileHelper.LoadJsonDict(_paths.SecretsPath);

        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Slack.Enabled", out var slackEnabled), "Slack.Enabled missing");
        Assert.True(Assert.IsType<bool>(slackEnabled));
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Slack.AllowedChannelIds", out var slackCh), "Slack channels missing");
        Assert.Equal(["C100"], ToStringArray(slackCh));
        AssertSecret(secrets, "Slack.BotToken", "xoxb-live");

        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Discord.Enabled", out var discordEnabled), "Discord.Enabled missing");
        Assert.True(Assert.IsType<bool>(discordEnabled));
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Discord.AllowedChannelIds", out var discordCh), "Discord channels missing");
        Assert.Equal(["555000111"], ToStringArray(discordCh));
        AssertSecret(secrets, "Discord.BotToken", "discord-live");
    }

    private static void OpenChannels(ConfigDashboardViewModel dashboardVm)
    {
        dashboardVm.SelectedIndex.Value = dashboardVm.Items
            .Select((item, index) => (item, index))
            .Single(entry => entry.item.Label == "Channels")
            .index;
        dashboardVm.ActivateSelected();
    }

    private static void MoveToAdapter(VirtualInputSource input, ChannelType channelType)
    {
        var adapterIndex = channelType switch
        {
            ChannelType.Slack => 0,
            ChannelType.Discord => 1,
            ChannelType.Mattermost => 2,
            ChannelType.Teams => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(channelType), channelType, null)
        };

        for (var i = 0; i < adapterIndex; i++)
            input.EnqueueKey(ConsoleKey.DownArrow);
    }

    private static void MoveToRotateCredentials(VirtualInputSource input)
    {
        for (var i = 0; i < 4; i++)
            input.EnqueueKey(ConsoleKey.DownArrow);
    }

    private static void TypeCredentials(VirtualInputSource input, ChannelType channelType)
    {
        switch (channelType)
        {
            case ChannelType.Slack:
                input.EnqueueString("xoxb-typed-token");
                input.EnqueueKey(ConsoleKey.Tab);
                input.EnqueueString("xapp-typed-token");
                break;
            case ChannelType.Discord:
                input.EnqueueString("discord-typed-token");
                break;
            case ChannelType.Mattermost:
                input.EnqueueKey(ConsoleKey.A, false, false, true);
                input.EnqueueKey(ConsoleKey.Backspace);
                input.EnqueueString("https://typed-mattermost.example.com");
                input.EnqueueKey(ConsoleKey.Tab);
                input.EnqueueString("mattermost-typed-token");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(channelType), channelType, null);
        }
    }

    private static void TypeFirstTimeSetup(VirtualInputSource input, ChannelType channelType)
    {
        switch (channelType)
        {
            case ChannelType.Slack:
                input.EnqueueString("xoxb-first-time-token");
                input.EnqueueKey(ConsoleKey.Enter);
                input.EnqueueString("xapp-first-time-token");
                input.EnqueueKey(ConsoleKey.Enter);
                input.EnqueueString("C123456");
                input.EnqueueKey(ConsoleKey.Enter);
                SelectSecondOption(input); // Disable DMs.
                SelectSecondOption(input); // Allow anyone in allowed channels.
                break;
            case ChannelType.Discord:
                input.EnqueueString("discord-first-time-token");
                input.EnqueueKey(ConsoleKey.Enter);
                input.EnqueueString("123456789012345678");
                input.EnqueueKey(ConsoleKey.Enter);
                SelectSecondOption(input); // Disable DMs.
                SelectSecondOption(input); // Allow anyone in allowed channels.
                break;
            case ChannelType.Mattermost:
                input.EnqueueString("https://first-time-mattermost.example.com");
                input.EnqueueKey(ConsoleKey.Enter);
                input.EnqueueString("mattermost-first-time-token");
                input.EnqueueKey(ConsoleKey.Enter);
                input.EnqueueString("town-square");
                input.EnqueueKey(ConsoleKey.Enter);
                SelectSecondOption(input); // Disable DMs.
                SelectSecondOption(input); // Allow anyone in allowed channels.
                input.EnqueueKey(ConsoleKey.Enter); // Skip optional callback URL.
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(channelType), channelType, null);
        }
    }

    private static void SelectSecondOption(VirtualInputSource input)
    {
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter);
    }

    private void AssertPersistedCredentials(ChannelType channelType, bool typed)
    {
        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        var secrets = ConfigFileHelper.LoadJsonDict(_paths.SecretsPath);
        switch (channelType)
        {
            case ChannelType.Slack:
                AssertSecret(secrets, "Slack.BotToken", typed ? "xoxb-typed-token" : "xoxb-first-time-token");
                AssertSecret(secrets, "Slack.AppToken", typed ? "xapp-typed-token" : "xapp-first-time-token");
                break;
            case ChannelType.Discord:
                AssertSecret(secrets, "Discord.BotToken", typed ? "discord-typed-token" : "discord-first-time-token");
                break;
            case ChannelType.Mattermost:
                Assert.True(ConfigFileHelper.TryGetPathValue(config, "Mattermost.ServerUrl", out var serverUrl));
                Assert.Equal(typed ? "https://typed-mattermost.example.com" : "https://first-time-mattermost.example.com", serverUrl);
                AssertSecret(secrets, "Mattermost.BotToken", typed ? "mattermost-typed-token" : "mattermost-first-time-token");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(channelType), channelType, null);
        }
    }

    private void AssertFirstTimeSetupPersisted(ChannelsConfigViewModel vm, ChannelType channelType)
    {
        AssertPersistedCredentials(channelType, typed: false);
        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        switch (channelType)
        {
            case ChannelType.Slack:
                var slack = vm.Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack);
                Assert.True(slack.HasPersistedBotToken);
                Assert.True(slack.HasPersistedAppToken);
                Assert.True(ConfigFileHelper.TryGetPathValue(config, "Slack.AllowedChannelIds", out var slackChannelsRaw));
                Assert.Equal(["C123456"], ToStringArray(slackChannelsRaw));
                break;
            case ChannelType.Discord:
                var discord = vm.Step.GetAdapterViewModel<DiscordStepViewModel>(ChannelType.Discord);
                Assert.True(discord.HasPersistedBotToken);
                Assert.True(ConfigFileHelper.TryGetPathValue(config, "Discord.AllowedChannelIds", out var discordChannelsRaw));
                Assert.Equal(["123456789012345678"], ToStringArray(discordChannelsRaw));
                break;
            case ChannelType.Mattermost:
                var mattermost = vm.Step.GetAdapterViewModel<MattermostStepViewModel>(ChannelType.Mattermost);
                Assert.True(mattermost.HasPersistedBotToken);
                Assert.True(ConfigFileHelper.TryGetPathValue(config, "Mattermost.AllowedChannelIds", out var mattermostChannelsRaw));
                Assert.Equal(["town-square"], ToStringArray(mattermostChannelsRaw));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(channelType), channelType, null);
        }
    }

    private void AssertSecret(Dictionary<string, object> secrets, string path, string expected)
    {
        Assert.True(ConfigFileHelper.TryGetPathValue(secrets, path, out var raw));
        Assert.Equal(expected, ConfigFileHelper.DecryptIfEncrypted(_paths, raw?.ToString()));
    }

    private static string[] ToStringArray(object? raw)
        => Assert.IsType<object[]>(raw).Select(static value => value switch
        {
            string text => text,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } element => element.GetString()!,
            _ => throw new InvalidOperationException("Expected string array value.")
        }).ToArray();

    private void WriteEmptyChannelFiles()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            """
            {
              "configVersion": 1
            }
            """);
        File.WriteAllText(_paths.SecretsPath,
            """
            {
              "configVersion": 1
            }
            """);
    }

    private void WriteTeamsChannelFiles()
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Teams": {
                "Enabled": true,
                "TenantId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "ClientId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                "BotId": "cccccccc-cccc-cccc-cccc-cccccccccccc",
                "AllowedTeamIds": ["team-a"],
                "AllowedChannelIds": ["channel-a"]
              }
            }
            """);
        File.WriteAllText(_paths.SecretsPath,
            """
            { "configVersion": 1, "Teams": { "ClientSecret": "teams-test-secret" } }
            """);
    }

    private TerminaApplication CreateHeadlessApp(
        out VirtualInputSource input,
        out ConfigDashboardViewModel dashboardVm,
        out Func<ChannelsConfigViewModel?> getChannelsVm)
        => CreateHeadlessApp(
            out input,
            out dashboardVm,
            out getChannelsVm,
            out _,
            slackProbe: null,
            discordProbe: null,
            mattermostProbe: null);

    private TerminaApplication CreateHeadlessApp(
        out VirtualInputSource input,
        out ConfigDashboardViewModel dashboardVm,
        out Func<ChannelsConfigViewModel?> getChannelsVm,
        out VirtualTerminal terminal,
        FakeSlackProbe? slackProbe = null,
        FakeDiscordProbe? discordProbe = null,
        FakeMattermostProbe? mattermostProbe = null,
        Func<TeamsChannelOptions, ITeamsDirectory>? teamsDirectoryFactory = null,
        int terminalWidth = 120,
        int terminalHeight = 40)
    {
        var terminalInstance = new VirtualTerminal(terminalWidth, terminalHeight);
        terminal = terminalInstance;
        var virtualInput = new VirtualInputSource();
        input = virtualInput;

        var navigationState = new ConfigDashboardNavigationState();
        var tuiNavigation = new TuiNavigation();
        ConfigDashboardViewModel? capturedDashboardVm = null;
        ChannelsConfigViewModel? capturedChannelsVm = null;

        var services = new ServiceCollection();
        services.AddSingleton<IAnsiTerminal>(terminalInstance);
        services.AddSingleton(tuiNavigation);
        services.AddTerminaVirtualInput(virtualInput);
        services.AddTermina("/config", builder =>
        {
            builder.RegisterRoute<ConfigDashboardPage, ConfigDashboardViewModel>(
                "/config",
                _ => new ConfigDashboardPage(),
                _ =>
                {
                    capturedDashboardVm = new ConfigDashboardViewModel(navigationState);
                    return capturedDashboardVm;
                });
            builder.RegisterRoute<ChannelsConfigPage, ChannelsConfigViewModel>(
                "/channels",
                _ => new ChannelsConfigPage(),
                _ =>
                {
                    capturedChannelsVm = new ChannelsConfigViewModel(
                        _paths,
                        slackProbe ?? new FakeSlackProbe(),
                        discordProbe ?? new FakeDiscordProbe(),
                        mattermostProbe ?? new FakeMattermostProbe(),
                        TimeProvider.System,
                        tuiNavigation,
                        teamsDirectoryFactory: teamsDirectoryFactory);
                    return capturedChannelsVm;
                });
        });

        var sp = services.BuildServiceProvider();
        var app = sp.GetRequiredService<TerminaApplication>();
        tuiNavigation.Attach(app);

        dashboardVm = capturedDashboardVm!;
        getChannelsVm = () => capturedChannelsVm;
        return app;
    }
}

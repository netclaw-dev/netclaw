// -----------------------------------------------------------------------
// <copyright file="McpServerLifecycleTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Authentication;
using Netclaw.Cli.Mcp;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Daemon.Mcp;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Cli.Tests.Mcp;

/// <summary>
/// Re-running <c>mcp add</c> must update only the connection definition, and
/// <c>mcp remove</c> must clear everything that is keyed by the server name.
/// </summary>
public sealed class McpServerLifecycleTests : IDisposable
{
    private const string Url = "https://mcp.example.test/mcp";
    private static readonly McpServerName Notion = new("notion");

    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly StringWriter _output = new();

    public McpServerLifecycleTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        _output.Dispose();
        _dir.Dispose();
    }

    [Theory]
    [InlineData("--header", "X-Api-Key: rotated")]
    [InlineData("--client-secret", "rotated-secret")]
    public async Task Add_ExistingServer_UpdatesDefinitionAndLeavesEveryAudienceUntouched(string flag, string value)
    {
        await AddNotionAsync("old-secret");
        ConfigureOperatorState("notion");
        var toolsBefore = ReadConfig()["Tools"]!.ToJsonString();

        var args = new List<string> { "mcp", "add", "--transport", "http", "--client-id", "client", flag, value };
        if (flag != "--client-secret")
            args.AddRange(["--client-secret", "old-secret"]);

        args.AddRange(["notion", "https://changed.example.test/mcp"]);
        Assert.Equal(0, await McpCommand.RunAsync([.. args], _paths, output: _output));

        var config = ReadConfig();
        Assert.Equal("https://changed.example.test/mcp", config["McpServers"]!["notion"]!["Url"]!.GetValue<string>());
        Assert.Equal(toolsBefore, config["Tools"]!.ToJsonString());
        Assert.Contains("Updated MCP server 'notion'", _output.ToString(), StringComparison.Ordinal);
        Assert.Contains("unchanged", _output.ToString(), StringComparison.Ordinal);

        var loaded = McpCommand.LoadMcpServers(_paths)["notion"];
        if (flag == "--client-secret")
            Assert.Equal(value, loaded.OAuthClientSecret?.Value);
        else
            Assert.Equal(value["X-Api-Key: ".Length..], loaded.Headers?["X-Api-Key"].Value);
    }

    [Fact]
    public async Task Add_ExistingServer_KeepsItDisabledAndIgnoresGrantAll()
    {
        await AddNotionAsync(clientSecret: null);
        ConfigureOperatorState("notion");
        Assert.Equal(0, await McpCommand.RunAsync(["mcp", "disable", "notion"], _paths, output: _output));
        var toolsBefore = ReadConfig()["Tools"]!.ToJsonString();

        Assert.Equal(0, await McpCommand.RunAsync(
            ["mcp", "add", "--grant-all", "--transport", "http", "notion", "https://changed.example.test/mcp"],
            _paths,
            output: _output));

        var config = ReadConfig();
        Assert.False(config["McpServers"]!["notion"]!["Enabled"]!.GetValue<bool>());
        Assert.Equal(toolsBefore, config["Tools"]!.ToJsonString());
        Assert.Contains("--grant-all ignored", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_ClearsEverythingKeyedByTheServer_AndReAddStartsClean()
    {
        await AddNotionAsync("client-secret");
        Assert.Equal(0, await McpCommand.RunAsync(
            ["mcp", "add", "--transport", "http", "other", "https://other.example.test/mcp"],
            _paths,
            output: _output));
        ConfigureOperatorState("notion");
        ConfigureOperatorState("other");
        await SeedOAuthTokensAsync(Notion);
        await SeedOAuthTokensAsync(new McpServerName("other"));
        SeedApprovals();

        var otherConfigBefore = OtherServerConfig();
        Assert.True(NewCredentialStore().HasAnyActive(Notion));

        Assert.Equal(0, await McpCommand.RunAsync(["mcp", "remove", "notion"], _paths, output: _output));

        // Nothing keyed by the name remains in either file, the token store or the approval store.
        Assert.DoesNotContain("notion", File.ReadAllText(_paths.NetclawConfigPath), StringComparison.Ordinal);
        Assert.DoesNotContain("notion", File.ReadAllText(_paths.SecretsPath), StringComparison.Ordinal);
        Assert.False(NewCredentialStore().HasAnyActive(Notion));
        var approvals = new ToolApprovalStore(_paths.ToolApprovalsPath).Snapshot();
        Assert.DoesNotContain(approvals.Values.SelectMany(tools => tools.Keys), tool => tool.StartsWith("notion/", StringComparison.Ordinal));

        // The allow-list entries (any case), grants, defaults and overrides are gone from Team
        // and Public, and the other server's are not.
        foreach (var audience in new[] { "Team", "Public" })
        {
            var profile = ReadConfig()["Tools"]!["AudienceProfiles"]![audience]!;
            Assert.Equal(
                ["unrelated", "OTHER", "other"],
                profile["AllowedMcpServers"]!.AsArray().Select(node => node!.GetValue<string>()));
            var overrideKeys = profile["ApprovalPolicy"]!["ToolOverrides"]!.AsObject().Select(pair => pair.Key);
            Assert.Equal(["file_write", "other/fetch", "other__search"], overrideKeys.Order(StringComparer.Ordinal));
        }

        // Other servers and first-party approvals are untouched.
        Assert.Equal(otherConfigBefore, OtherServerConfig());
        Assert.True(NewCredentialStore().HasAnyActive(new McpServerName("other")));
        Assert.Contains("other/search", approvals["team"].Keys);
        Assert.Contains("shell_execute", approvals["personal"].Keys);

        // Adding the name again signs in from scratch and gets the secure defaults.
        Assert.Equal(0, await McpCommand.RunAsync(
            ["mcp", "add", "--transport", "http", "notion", Url],
            _paths,
            output: _output));
        Assert.True(NewCredentialStore().RequiresAuthorization(Notion, Url));
        var team = ReadConfig()["Tools"]!["AudienceProfiles"]!["Team"]!;
        Assert.Empty(team["McpServerToolGrants"]!["notion"]!.AsArray());
        Assert.DoesNotContain("notion", team["AllowedMcpServers"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Equal("Approval", team["ApprovalPolicy"]!["McpServerDefaults"]!["notion"]!.GetValue<string>());
        Assert.DoesNotContain(
            team["ApprovalPolicy"]!["ToolOverrides"]!.AsObject().Select(pair => pair.Key),
            key => key.StartsWith("notion", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Remove_ServerAbsentFromMcpServers_StillClearsLeftoverAudienceEntries()
    {
        await AddNotionAsync(clientSecret: null);
        ConfigureOperatorState("notion");
        var config = ReadConfig();
        config["McpServers"]!.AsObject().Remove("notion");
        File.WriteAllText(_paths.NetclawConfigPath, config.ToJsonString());

        Assert.Equal(0, await McpCommand.RunAsync(["mcp", "remove", "notion"], _paths, output: _output));

        Assert.DoesNotContain("notion", File.ReadAllText(_paths.NetclawConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_NameDifferingOnlyByCase_IsRefusedAndChangesNothing()
    {
        WriteServers("github", "git", "git__x");
        ConfigureOperatorState("github");
        ConfigureOperatorState("git__x");
        SeedApprovals("github/search", "git__x/fetch");
        var before = SnapshotState();

        Assert.Equal(1, await McpCommand.RunAsync(["mcp", "remove", "GITHUB"], _paths, output: _output));

        Assert.Contains("configured server is 'github'", _output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Removed", _output.ToString(), StringComparison.Ordinal);
        Assert.Equal(before, SnapshotState());
    }

    [Fact]
    public async Task Remove_ServerWithCaseVariantSibling_KeepsEntriesTheSiblingUses()
    {
        WriteServers("github", "GITHUB");
        ConfigureOperatorState("github");
        ConfigureOperatorState("GITHUB");

        Assert.Equal(0, await McpCommand.RunAsync(["mcp", "remove", "github"], _paths, output: _output));

        foreach (var audience in new[] { "Team", "Public" })
        {
            var profile = ReadConfig()["Tools"]!["AudienceProfiles"]![audience]!;
            // The runtime reads these allow-list entries as the surviving server too.
            Assert.Contains("github", profile["AllowedMcpServers"]!.AsArray().Select(node => node!.GetValue<string>()));
            Assert.Contains("GITHUB", profile["AllowedMcpServers"]!.AsArray().Select(node => node!.GetValue<string>()));
            Assert.Null(profile["McpServerToolGrants"]!["github"]);
            Assert.NotNull(profile["McpServerToolGrants"]!["GITHUB"]);
            Assert.NotNull(profile["ApprovalPolicy"]!["McpServerDefaults"]!["GITHUB"]);
            Assert.NotNull(profile["ApprovalPolicy"]!["ToolOverrides"]!["GITHUB/fetch"]);
            Assert.Null(profile["ApprovalPolicy"]!["ToolOverrides"]!["github/fetch"]);
        }
    }

    [Theory]
    [InlineData("git", "git__x")]
    [InlineData("a", "a/b")]
    public async Task Remove_ServerWhoseNameStartsAnotherServersName_LeavesTheOtherServersStateIntact(string removed, string survivor)
    {
        WriteServers(removed, survivor);
        ConfigureOperatorState(removed);
        ConfigureOperatorState(survivor);
        SeedApprovals($"{removed}/fetch", $"{survivor}/fetch", $"{survivor}__search");
        var survivorOverrides = OverridesOf(survivor);
        var survivorApprovals = ApprovalsOf(survivor);

        Assert.Equal(0, await McpCommand.RunAsync(["mcp", "remove", removed], _paths, output: _output));

        Assert.Empty(OverridesOf(removed));
        Assert.Empty(ApprovalsOf(removed));
        Assert.Equal(survivorOverrides, OverridesOf(survivor));
        Assert.Equal(survivorApprovals, ApprovalsOf(survivor));
        Assert.NotEmpty(survivorApprovals);
        Assert.Contains(survivor, ReadConfig()["McpServers"]!.AsObject().Select(pair => pair.Key));
    }

    [Fact]
    public async Task Remove_UnconfiguredLeftovers_AreClearedByTheExactNameOnly()
    {
        WriteServers("git__x");
        ConfigureOperatorState("git");
        ConfigureOperatorState("git__x");
        SeedApprovals("git/fetch", "git__x/fetch");
        var survivorOverrides = OverridesOf("git__x");

        Assert.Equal(0, await McpCommand.RunAsync(["mcp", "remove", "git"], _paths, output: _output));

        Assert.Empty(OverridesOf("git"));
        Assert.Empty(ApprovalsOf("git"));
        Assert.Equal(survivorOverrides, OverridesOf("git__x"));
        Assert.NotEmpty(ApprovalsOf("git__x"));
    }

    [Fact]
    public async Task Remove_UnavailableApprovalStore_FailsBeforeChangingAnything()
    {
        await AddNotionAsync("client-secret");
        ConfigureOperatorState("notion");
        File.WriteAllText(_paths.ToolApprovalsPath, "{ not json");
        var before = SnapshotState();

        Assert.Equal(1, await McpCommand.RunAsync(["mcp", "remove", "notion"], _paths, output: _output));

        Assert.Contains("nothing was removed", _output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Removed MCP server", _output.ToString(), StringComparison.Ordinal);
        Assert.Equal(before, SnapshotState());
    }

    [Fact]
    public async Task Add_ExistingServer_KeepsGrantCategoryAndSaysWhatTheCommandLineReplacedAndDropped()
    {
        await AddNotionAsync("old-secret");
        var config = ReadConfig();
        config["McpServers"]!["notion"]!["GrantCategory"] = "custom-category";
        config["McpServers"]!["notion"]!["OAuthScope"] = "read";
        File.WriteAllText(_paths.NetclawConfigPath, config.ToJsonString());
        Assert.Equal(0, await McpCommand.RunAsync(["mcp", "disable", "notion"], _paths, output: _output));
        _output.GetStringBuilder().Clear();

        Assert.Equal(0, await McpCommand.RunAsync(
            ["mcp", "add", "--transport", "http", "notion", "https://changed.example.test/mcp"],
            _paths,
            output: _output));

        var entry = ReadConfig()["McpServers"]!["notion"]!;
        Assert.Equal("custom-category", entry["GrantCategory"]!.GetValue<string>());
        Assert.False(entry["Enabled"]!.GetValue<bool>());
        var text = _output.ToString();
        Assert.Contains("replaced url", text, StringComparison.Ordinal);
        Assert.Contains("dropped OAuth client id, OAuth scope, OAuth client secret", text, StringComparison.Ordinal);
        Assert.Contains("stays disabled", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("True", true)]
    public async Task Add_ExistingServerWithTextualEnabled_ReadsItLikeTheConfigurationBinder(string text, bool expected)
    {
        await AddNotionAsync(clientSecret: null);
        var config = ReadConfig();
        config["McpServers"]!["notion"]!["Enabled"] = text;
        File.WriteAllText(_paths.NetclawConfigPath, config.ToJsonString());

        Assert.Equal(0, await McpCommand.RunAsync(
            ["mcp", "add", "--transport", "http", "notion", Url], _paths, output: _output));

        Assert.Equal(expected, ReadConfig()["McpServers"]!["notion"]!["Enabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Add_ExistingServerWithUnreadableEnabled_FailsWithoutChangingAnything()
    {
        await AddNotionAsync(clientSecret: null);
        var config = ReadConfig();
        config["McpServers"]!["notion"]!["Enabled"] = "maybe";
        File.WriteAllText(_paths.NetclawConfigPath, config.ToJsonString());
        var before = SnapshotState();

        Assert.Equal(1, await McpCommand.RunAsync(
            ["mcp", "add", "--transport", "http", "notion", Url], _paths, output: _output));

        Assert.Contains("not true or false", _output.ToString(), StringComparison.Ordinal);
        Assert.Equal(before, SnapshotState());
    }

    private void WriteServers(params string[] names)
    {
        var servers = new JsonObject();
        foreach (var name in names)
            servers[name] = new JsonObject { ["Transport"] = "http", ["Url"] = Url };

        var config = File.Exists(_paths.NetclawConfigPath) ? ReadConfig().AsObject() : new JsonObject();
        config["McpServers"] = servers;
        if (config["Tools"] is null)
        {
            var profiles = new JsonObject();
            foreach (var audience in new[] { "Personal", "Team", "Public" })
            {
                profiles[audience] = new JsonObject
                {
                    ["McpServerToolGrants"] = new JsonObject(),
                    ["ApprovalPolicy"] = new JsonObject { ["McpServerDefaults"] = new JsonObject() },
                };
            }

            config["Tools"] = new JsonObject { ["AudienceProfiles"] = profiles };
        }

        File.WriteAllText(_paths.NetclawConfigPath, config.ToJsonString());
    }

    private void SeedApprovals(params string[] tools)
    {
        var store = new ToolApprovalStore(_paths.ToolApprovalsPath);
        foreach (var tool in tools)
            store.AddApproval(TrustAudience.Team, tool, ApprovalEntry.CreateNonShell(tool));
    }

    // The two tool-override keys the tests seed for a server, in both spellings.
    private string[] OverridesOf(string server) =>
    [
        .. new[] { "Team", "Public" }.SelectMany(audience =>
            ReadConfig()["Tools"]!["AudienceProfiles"]![audience]!["ApprovalPolicy"]!["ToolOverrides"]!.AsObject()
                .Select(pair => $"{audience}:{pair.Key}={pair.Value}")
                .Where(entry => entry.Contains($":{server}/fetch=", StringComparison.Ordinal)
                                || entry.Contains($":{server}__search=", StringComparison.Ordinal))),
    ];

    private string[] ApprovalsOf(string server) =>
    [
        .. new ToolApprovalStore(_paths.ToolApprovalsPath).Snapshot().SelectMany(audience =>
            audience.Value.Keys
                .Where(tool => tool == $"{server}/fetch" || tool == $"{server}__search")
                .Select(tool => $"{audience.Key}:{tool}")),
    ];

    // Every file the command may touch, byte for byte.
    private string SnapshotState() => string.Join(
        "\n",
        new[] { _paths.NetclawConfigPath, _paths.SecretsPath, _paths.ToolApprovalsPath }
            .Select(path => File.Exists(path) ? File.ReadAllText(path) : "<missing>"));

    private Task AddNotionAsync(string? clientSecret)
    {
        var args = new List<string> { "mcp", "add", "--transport", "http", "--client-id", "client" };
        if (clientSecret is not null)
            args.AddRange(["--client-secret", clientSecret]);

        args.AddRange(["notion", Url]);
        return McpCommand.RunAsync([.. args], _paths, output: _output);
    }

    // The state an operator builds after add: grants, allow-list entries, approval modes and
    // per-tool overrides, with different values for Team and Public.
    private void ConfigureOperatorState(string server)
    {
        var config = ReadConfig();
        var profiles = config["Tools"]!["AudienceProfiles"]!.AsObject();
        foreach (var (audience, grants, mode, overrideMode) in new[]
                 {
                     ("Team", new[] { "search", "fetch" }, "Auto", "Deny"),
                     ("Public", new[] { "search" }, "Approval", "Auto"),
                 })
        {
            var profile = profiles[audience]!.AsObject();
            profile["McpServersMode"] = "Allowlist";
            var allowed = profile["AllowedMcpServers"]?.AsArray() ?? [];
            foreach (var name in new[] { "unrelated", server.ToUpperInvariant(), server })
            {
                if (!allowed.Any(node => node!.GetValue<string>() == name))
                    allowed.Add(name);
            }

            profile["AllowedMcpServers"] = allowed;
            profile["McpServerToolGrants"]![server] = new JsonArray([.. grants.Select(tool => (JsonNode)tool)]);
            var policy = profile["ApprovalPolicy"]!.AsObject();
            policy["McpServerDefaults"]![server] = mode;
            var overrides = policy["ToolOverrides"]?.AsObject() ?? [];
            overrides[$"{server}/fetch"] = overrideMode;
            overrides[$"{server}__search"] = overrideMode;
            overrides["file_write"] = "Deny";
            policy["ToolOverrides"] = overrides;
        }

        File.WriteAllText(_paths.NetclawConfigPath, config.ToJsonString());
    }

    private void SeedApprovals()
    {
        var store = new ToolApprovalStore(_paths.ToolApprovalsPath);
        store.AddApproval(TrustAudience.Team, "notion/fetch", ApprovalEntry.CreateNonShell("notion/fetch"));
        store.AddApproval(TrustAudience.Public, "notion/search", ApprovalEntry.CreateNonShell("notion/search"));
        store.AddApproval(TrustAudience.Team, "other/search", ApprovalEntry.CreateNonShell("other/search"));
        store.AddApproval(
            TrustAudience.Personal,
            "shell_execute",
            ApprovalEntry.CreateTokenPrefix(ApprovalShell.Bash, ["git", "push"]));
    }

    private async Task SeedOAuthTokensAsync(McpServerName server)
    {
        var store = NewCredentialStore();
        var cache = store.CreateTokenCache(
            server,
            Url,
            new McpOAuthClientIdentity("client", clientSecret: null, dynamicClientRegistration: false),
            explicitAuthorization: true);
        await cache.StoreTokensAsync(
            new TokenContainer
            {
                AccessToken = "access-token",
                RefreshToken = "refresh-token",
                TokenType = "Bearer",
                ExpiresIn = 3600,
                ObtainedAt = DateTimeOffset.UtcNow,
            },
            TestContext.Current.CancellationToken);
        store.Publish(cache, TestContext.Current.CancellationToken);
    }

    private McpOAuthCredentialStore NewCredentialStore() => new(
        _paths,
        TimeProvider.System,
        SecretsProtection.CreateProtector(_paths),
        NullLogger<McpOAuthCredentialStore>.Instance);

    private string OtherServerConfig()
    {
        var config = ReadConfig();
        var profiles = config["Tools"]!["AudienceProfiles"]!;
        return string.Join(
            "|",
            config["McpServers"]!["other"]!.ToJsonString(),
            string.Join(
                ",",
                new[] { "Personal", "Team", "Public" }.Select(audience => string.Join(
                    ";",
                    profiles[audience]!["McpServerToolGrants"]?["other"]?.ToJsonString(),
                    profiles[audience]!["ApprovalPolicy"]!["McpServerDefaults"]!["other"]!.ToJsonString(),
                    profiles[audience]!["ApprovalPolicy"]!["ToolOverrides"]?["other/fetch"]?.ToJsonString()))));
    }

    private JsonNode ReadConfig() => JsonNode.Parse(File.ReadAllText(_paths.NetclawConfigPath))!;
}

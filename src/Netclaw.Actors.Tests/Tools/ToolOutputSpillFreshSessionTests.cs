// -----------------------------------------------------------------------
// <copyright file="ToolOutputSpillFreshSessionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Skills;
using Netclaw.Actors.Tests.Memory;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Skills;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// A session gets its workspace folder on first use. Before the fix, only a
/// shell launch created it, so a session with no earlier shell call got an
/// oversized result with no call id and no retained text. These tests run the
/// real dispatcher on a session folder that does not exist.
/// </summary>
public sealed class ToolOutputSpillFreshSessionTests : IDisposable
{
    private const string HiddenRule = "HIDDEN-MIDDLE-RULE-7c41";
    private const string OversizedProbe = "oversized_probe";

    private readonly DisposableTempDir _temp = new();
    private readonly NetclawPaths _paths;
    private readonly string _sessionDirectory;
    private readonly DispatchingToolExecutor _executor;
    private readonly DispatchingToolExecutor _probeOnlyExecutor;
    private readonly int _budget = new SessionTuning().MaxInlineToolResultChars;

    public ToolOutputSpillFreshSessionTests()
    {
        _paths = new NetclawPaths(Path.Combine(_temp.Path, "home"));
        _paths.EnsureDirectoriesExist();
        // The envelope exists (the session log is in it). The workspace does not.
        var envelope = Path.Combine(_temp.Path, "sessions", "fresh-session");
        Directory.CreateDirectory(Path.Combine(envelope, "logs"));
        _sessionDirectory = Path.Combine(envelope, "workspace");

        var skillDirectory = Path.Combine(_paths.SkillsDirectory, "oversized-skill");
        Directory.CreateDirectory(Path.Combine(skillDirectory, "references"));
        File.WriteAllText(
            Path.Combine(skillDirectory, "SKILL.md"),
            "---\nname: oversized-skill\ndescription: A skill that is longer than the inline budget.\n---\n\n"
            + "# Oversized Skill\n\n" + OversizedText());
        File.WriteAllText(Path.Combine(skillDirectory, "references", "long.md"), OversizedText());

        var skills = new SkillRegistry();
        var scan = SkillScanner.Scan(_paths.SkillsDirectory);
        skills.ReplaceAll(scan.AcceptedSkills, scan.Issues);

        var scanner = new NoOpSkillContentScanner();
        var registry = new ToolRegistry();
        registry.RegisterCore(new SkillLoadTool(skills, scanner, new UnavailablePromptLoader()));
        registry.RegisterCore(new SkillReadResourceTool(skills, scanner));
        registry.RegisterCore(new ToolOutputReadTool());
        registry.Register(new FakeNetclawTool("spawn_agent", "accepted"));
        registry.Register(new FakeNetclawTool(OversizedProbe, OversizedText()));

        // Team and Public get the probe and the continuation tool. A second Public
        // profile has the probe only, so the audience profile has an effect.
        var config = new ToolConfig();
        config.AudienceProfiles.Team.AllowedTools = [OversizedProbe, ToolOutputReadTool.ToolName, "spawn_agent"];
        config.AudienceProfiles.Public.AllowedTools = [OversizedProbe, ToolOutputReadTool.ToolName, "spawn_agent"];
        _executor = new DispatchingToolExecutor(registry, TestToolAccessPolicy.Create(config));

        var probeOnly = new ToolConfig();
        probeOnly.AudienceProfiles.Public.AllowedTools = [OversizedProbe];
        _probeOnlyExecutor = new DispatchingToolExecutor(registry, TestToolAccessPolicy.Create(probeOnly));
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Skill_load_in_a_session_with_no_shell_call_gives_a_call_id_for_the_hidden_middle()
    {
        var result = await ExecuteAsync("call-skill-load", "skill_load", ToolInput.Create("Name", "oversized-skill"));

        await AssertHiddenMiddleIsReadableAsync("call-skill-load", result);
    }

    [Fact]
    public async Task Skill_read_resource_in_a_session_with_no_shell_call_gives_a_call_id_for_the_hidden_middle()
    {
        var result = await ExecuteAsync(
            "call-skill-resource",
            "skill_read_resource",
            ToolInput.Create("SkillName", "oversized-skill", "ResourcePath", "references/long.md"));

        await AssertHiddenMiddleIsReadableAsync("call-skill-resource", result);
    }

    [Theory]
    [InlineData(TrustAudience.Personal)]
    [InlineData(TrustAudience.Team)]
    [InlineData(TrustAudience.Public)]
    public async Task Another_tool_in_a_session_with_no_shell_call_gives_a_call_id_for_the_hidden_middle(
        TrustAudience audience)
    {
        var result = await ExecuteAsync("call-probe", OversizedProbe, ToolInput.Empty(), audience);

        await AssertHiddenMiddleIsReadableAsync("call-probe", result, audience);
    }

    [Fact]
    public async Task An_audience_without_the_continuation_tool_cannot_read_the_spill()
    {
        var result = await ExecuteAsync(
            "call-probe", OversizedProbe, ToolInput.Empty(), TrustAudience.Public, _probeOnlyExecutor);
        Assert.Contains("tool_output_read using CallId='call-probe'", result, StringComparison.Ordinal);

        await Assert.ThrowsAsync<ToolAccessDeniedException>(() => ExecuteAsync(
            "call-probe-read",
            ToolOutputReadTool.ToolName,
            ToolInput.Create("CallId", "call-probe", "Start", 0, "Limit", ToolOutputReadTool.MaximumLimit),
            TrustAudience.Public,
            _probeOnlyExecutor));
    }

    [Fact]
    public async Task Retained_spill_hints_child_inspection_only_when_the_caller_can_delegate_and_read()
    {
        var hinted = await ExecuteAsync(
            "call-hinted", OversizedProbe, ToolInput.Empty(), TrustAudience.Team, _executor, canSpawnChild: true);
        var unhinted = await ExecuteAsync(
            "call-unhinted", OversizedProbe, ToolInput.Empty(), TrustAudience.Team, _executor);

        Assert.Contains("tool_output_read using CallId='call-hinted'", hinted, StringComparison.Ordinal);
        Assert.Contains("consider a permitted child profile", hinted, StringComparison.Ordinal);
        Assert.DoesNotContain("consider a permitted child profile", unhinted, StringComparison.Ordinal);
        Assert.True(ToolOutputSpillLocation.TryResolve(_sessionDirectory, "call-hinted", out _, out var spillPath));

        var parentContext = TestToolExecutionContext.CreateBound(
            "signalr/fresh-session",
            _sessionDirectory,
            new TestToolExecutionContextOptions { Audience = TrustAudience.Team });
        var directResult = await ExecuteReadAsync("call-unhinted", parentContext);
        Assert.Contains(HiddenRule, directResult, StringComparison.Ordinal);

        var childStorage = SessionStoragePaths.CreateLegacy(
            _sessionDirectory,
            Path.Combine(_temp.Path, "sessions", "fresh-session", "logs"),
            "parent").ForChild(new SubAgentRunId("child-run"), new SubAgentScopeId("parent/subagent/child"));
        Assert.Equal(_sessionDirectory, childStorage.SessionDirectory);

        var childContext = TestToolExecutionContext.CreateBoundWithStorage(
            "signalr/fresh-session",
            childStorage,
            new TestToolExecutionContextOptions { Audience = TrustAudience.Team });
        var childResult = await ExecuteReadAsync("call-hinted", childContext);

        Assert.Contains(HiddenRule, childResult, StringComparison.Ordinal);
        Assert.True(File.Exists(spillPath));
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public async Task Spill_has_no_child_hint_when_a_required_capability_is_missing_or_denied(
        bool registerSpawn,
        bool registerReader,
        bool allowSpawn,
        bool allowReader)
    {
        var executor = CreateHintExecutor(registerSpawn, registerReader, allowSpawn, allowReader);

        var result = await ExecuteAsync(
            "call-policy", OversizedProbe, ToolInput.Empty(), TrustAudience.Team, executor, canSpawnChild: true);

        Assert.Contains("CallId='call-policy'", result, StringComparison.Ordinal);
        Assert.DoesNotContain("consider a permitted child profile", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Spill_has_no_child_hint_when_the_output_was_not_retained()
    {
        var context = TestToolExecutionContext.CreateUnbound(new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Team,
            SpawnChildActor = SpawnChildActor,
        });
        var result = await ToolOutputSpill.BoundAndSpillAsync(
            new string('x', 400), new string('x', 400), "call-unretained", budget: 100, context.Invocation,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            TestContext.Current.CancellationToken,
            suggestChildInspection: true);

        Assert.Contains("did not keep the full output", result, StringComparison.Ordinal);
        Assert.DoesNotContain("consider a permitted child profile", result, StringComparison.Ordinal);
    }

    private async Task AssertHiddenMiddleIsReadableAsync(
        string callId, string result, TrustAudience audience = TrustAudience.Personal)
    {
        // The inline tail omits the middle. The continuation must recover it.
        Assert.Contains("[output truncated to", result, StringComparison.Ordinal);
        Assert.DoesNotContain(HiddenRule, result, StringComparison.Ordinal);
        Assert.Contains($"tool_output_read using CallId='{callId}'", result, StringComparison.Ordinal);
        Assert.DoesNotContain(_sessionDirectory, result, StringComparison.Ordinal);
        Assert.True(Directory.Exists(_sessionDirectory));

        var found = false;
        for (var start = 0; start < 60_000 && !found; start += ToolOutputReadTool.MaximumLimit - 500)
        {
            var window = await ExecuteAsync(
                $"{callId}-read-{start}",
                ToolOutputReadTool.ToolName,
                ToolInput.Create("CallId", callId, "Start", start, "Limit", ToolOutputReadTool.MaximumLimit),
                audience);
            found = window.Contains(HiddenRule, StringComparison.Ordinal);
            if (window.Contains("complete=true", StringComparison.Ordinal))
                break;
        }

        Assert.True(found, "tool_output_read did not return the text that the inline window removed.");
    }

    private Task<string> ExecuteAsync(
        string callId,
        string toolName,
        IDictionary<string, object?> arguments,
        TrustAudience audience = TrustAudience.Personal,
        DispatchingToolExecutor? executor = null,
        bool canSpawnChild = false)
    {
        var callArguments = new Dictionary<string, object?>(arguments, StringComparer.Ordinal)
        {
            ["_rationale"] = "Verify the spill of a session with no workspace folder."
        };
        var context = TestToolExecutionContext.CreateBound(
            "signalr/fresh-session",
            _sessionDirectory,
            new TestToolExecutionContextOptions
            {
                Audience = audience,
                SpawnChildActor = canSpawnChild ? SpawnChildActor : null,
            });
        return (executor ?? _executor).ExecuteAsync(
            new FunctionCallContent(callId, toolName, callArguments),
            context,
            TestContext.Current.CancellationToken);
    }

    private DispatchingToolExecutor CreateHintExecutor(
        bool registerSpawn,
        bool registerReader,
        bool allowSpawn,
        bool allowReader)
    {
        var registry = new ToolRegistry();
        registry.Register(new FakeNetclawTool(OversizedProbe, OversizedText()));
        if (registerSpawn)
            registry.Register(new FakeNetclawTool("spawn_agent", "accepted"));
        if (registerReader)
            registry.RegisterCore(new ToolOutputReadTool());

        var config = new ToolConfig();
        var tools = new List<string> { OversizedProbe };
        if (allowSpawn && registerSpawn)
            tools.Add("spawn_agent");
        if (allowReader && registerReader)
            tools.Add(ToolOutputReadTool.ToolName);
        config.AudienceProfiles.Team.AllowedTools = tools;
        return new DispatchingToolExecutor(registry, TestToolAccessPolicy.Create(config));
    }

    private static Task<object> SpawnChildActor(object request, string task, CancellationToken ct)
        => Task.FromResult<object>(new object());

    private Task<string> ExecuteReadAsync(string callId, ToolExecutionContext context)
    {
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["CallId"] = callId,
            ["Start"] = _budget,
            ["Limit"] = ToolOutputReadTool.MaximumLimit,
            ["_rationale"] = "Read the retained output within this session scope.",
        };
        return _executor.ExecuteAsync(
            new FunctionCallContent($"read-{callId}", ToolOutputReadTool.ToolName, arguments),
            context,
            TestContext.Current.CancellationToken);
    }

    // Three times the budget, with one marker in the middle third.
    private string OversizedText()
    {
        var filler = string.Concat(Enumerable.Repeat("This line is ordinary skill text.\n", _budget / 34 + 1));
        return filler + $"\n{HiddenRule}\n\n" + filler + filler;
    }

    private sealed class UnavailablePromptLoader : IMcpPromptSkillLoader
    {
        public ValueTask<McpPromptSkillLoadResult> LoadAsync(
            McpPromptSkillSource source,
            IReadOnlyDictionary<string, string>? arguments,
            ToolInvocationContext context,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(McpPromptSkillLoadResult.Failed("Prompt loading is unavailable in this test."));
    }
}

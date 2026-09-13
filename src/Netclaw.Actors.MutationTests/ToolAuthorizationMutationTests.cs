// -----------------------------------------------------------------------
// <copyright file="ToolAuthorizationMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.MutationTests;

public sealed class ToolAuthorizationMutationTests : IDisposable
{
    private readonly NetclawPaths _paths = new(Path.Combine(
        Path.GetTempPath(), "netclaw-authorization-mutations", Guid.NewGuid().ToString("N")));
    private readonly SessionStoragePaths _storage;

    public ToolAuthorizationMutationTests()
    {
        _paths.EnsureDirectoriesExist();
        _storage = SessionStoragePaths.CreateVersion2(
            new SessionStorageEnvelopeRoot(Path.Combine(_paths.SessionsDirectory, "current")));
        Directory.CreateDirectory(_storage.SessionDirectory.Value);
    }

    [Theory]
    [InlineData(TrustAudience.Public, ToolApprovalMode.Auto, false, "mcp_server_not_allowed_for_audience_profile")]
    [InlineData(TrustAudience.Public, ToolApprovalMode.Auto, true, "mcp_tool_not_allowed_for_audience_profile")]
    [InlineData(TrustAudience.Public, ToolApprovalMode.Approval, false, "mcp_server_not_allowed_for_audience_profile")]
    [InlineData(TrustAudience.Public, ToolApprovalMode.Approval, true, "mcp_tool_not_allowed_for_audience_profile")]
    [InlineData(TrustAudience.Team, ToolApprovalMode.Auto, false, "mcp_server_not_allowed_for_audience_profile")]
    [InlineData(TrustAudience.Team, ToolApprovalMode.Auto, true, "mcp_tool_not_allowed_for_audience_profile")]
    [InlineData(TrustAudience.Team, ToolApprovalMode.Approval, false, "mcp_server_not_allowed_for_audience_profile")]
    [InlineData(TrustAudience.Team, ToolApprovalMode.Approval, true, "mcp_tool_not_allowed_for_audience_profile")]
    public async Task Mcp_grant_denial_prevents_dispatch_despite_approval(
        TrustAudience audience, ToolApprovalMode mode, bool allowServer, string reason)
    {
        var calls = 0;
        var tool = new McpToolAdapter(
            AIFunctionFactory.Create(() => { calls++; return "mutation-probe"; }, "search_memories"),
            "memorizer", "search_memories");
        var config = CreateConfig(mode);
        var profile = audience == TrustAudience.Public
            ? config.AudienceProfiles.Public
            : config.AudienceProfiles.Team;
        profile.McpServersMode = ToolProfileMode.Allowlist;
        profile.AllowedMcpServers = ["memorizer"];
        profile.McpServerToolGrants = new() { ["memorizer"] = ["search_memories"] };
        var executor = CreateExecutor(tool, config, new ShellCommandPolicy());
        var call = CreateCall(tool.Name, new());
        var context = CreateContext(audience);

        await SeedApprovalAsync(executor, call, context, mode);
        if (mode == ToolApprovalMode.Approval)
        {
            Assert.Contains("mutation-probe", await executor.ExecuteAsync(call, context, CancellationToken.None));
            Assert.Equal(1, calls);
        }

        // Retain valid approval evidence while the audience loses one grant.
        if (allowServer)
            profile.McpServerToolGrants["memorizer"] = ["get"];
        else
            profile.AllowedMcpServers = [];

        var deniedContext = CreateContext(audience);
        deniedContext.Approval.SeedOneTimeApproval(
            context.Approval.OneTimeApprovedToolName ?? tool.Name,
            context.Approval.OneTimeApprovedPatterns);
        var denied = await Assert.ThrowsAsync<ToolAccessDeniedException>(() =>
            executor.ExecuteAsync(call, deniedContext, CancellationToken.None));

        Assert.Equal(reason, denied.DenyReason);
        Assert.Equal(mode == ToolApprovalMode.Approval ? 1 : 0, calls);

        // Auto cases reach the forbidden invocation before the positive control under an inverted guard.
        if (mode == ToolApprovalMode.Auto)
        {
            profile.AllowedMcpServers = ["memorizer"];
            profile.McpServerToolGrants["memorizer"] = ["search_memories"];
            Assert.Contains("mutation-probe", await executor.ExecuteAsync(call, context, CancellationToken.None));
            Assert.Equal(1, calls);
        }
    }

    [Theory]
    [InlineData(ToolApprovalMode.Auto)]
    [InlineData(ToolApprovalMode.Approval)]
    public async Task Shell_hard_denial_prevents_dispatch_despite_approval(ToolApprovalMode mode)
    {
        var tool = new ShellProbeTool();
        var config = CreateConfig(mode);
        var call = CreateCall(tool.Name, new() { ["Command"] = "echo mutation-probe" });
        var context = CreateContext(TrustAudience.Personal);
        var permitted = CreateExecutor(tool, config, new ShellCommandPolicy());

        await SeedApprovalAsync(permitted, call, context, mode);
        if (mode == ToolApprovalMode.Approval)
        {
            Assert.Equal("mutation-probe", await permitted.ExecuteAsync(call, context, CancellationToken.None));
            Assert.Equal(1, tool.Calls);
        }

        var restricted = CreateExecutor(tool, config, new ShellCommandPolicy(["echo mutation-probe"]));
        var deniedContext = CreateContext(TrustAudience.Personal);
        deniedContext.Approval.SeedOneTimeApproval(
            context.Approval.OneTimeApprovedToolName ?? tool.Name,
            context.Approval.OneTimeApprovedPatterns);
        var denied = await Assert.ThrowsAsync<ToolAccessDeniedException>(() =>
            restricted.ExecuteAsync(call, deniedContext, CancellationToken.None));

        Assert.Equal("hard_deny_custom_deny", denied.DenyReason);
        Assert.Equal(mode == ToolApprovalMode.Approval ? 1 : 0, tool.Calls);

        if (mode == ToolApprovalMode.Auto)
        {
            Assert.Equal("mutation-probe", await permitted.ExecuteAsync(call, context, CancellationToken.None));
            Assert.Equal(1, tool.Calls);
        }
    }

    public void Dispose() => Directory.Delete(_paths.BasePath, recursive: true);

    private static ToolConfig CreateConfig(ToolApprovalMode mode)
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        foreach (var profile in new[]
                 { config.AudienceProfiles.Public, config.AudienceProfiles.Team, config.AudienceProfiles.Personal })
        {
            profile.ApprovalPolicy = new ToolApprovalConfig
            {
                DefaultMode = mode,
                ToolOverrides = new() { ["shell_execute"] = mode }
            };
        }

        return config;
    }

    private DispatchingToolExecutor CreateExecutor(
        INetclawTool tool, ToolConfig config, ShellCommandPolicy commandPolicy)
    {
        var registry = new ToolRegistry();
        registry.Register(tool);
        var policy = new ToolAccessPolicy(
            _paths, config,
            new EffectivePolicyDefaults(DeploymentPosture.Personal, TrustAudience.Personal,
                ShellExecutionMode.HostAllowed, UsedStrictFallback: false),
            commandPolicy, new ToolPathPolicy([]));
        return new DispatchingToolExecutor(registry, policy);
    }

    private ToolExecutionContext CreateContext(TrustAudience audience) => new(
        new ToolRunScope
        {
            Session = new ToolSessionScope.Bound("signalr/mutation", _storage),
            Audience = audience,
            Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(audience),
            InlineOutputBudget = InlineOutputBudget.Default,
            InteractiveApproval = new InteractiveApprovalCapability.Available(new UnexpectedApprovalBridge())
        },
        ToolExecutionTimeout.Default);

    private static FunctionCallContent CreateCall(string name, Dictionary<string, object?> arguments)
    {
        arguments["_rationale"] = "Verify the authorization boundary.";
        return new FunctionCallContent("mutation-call", name, arguments);
    }

    private static async Task SeedApprovalAsync(
        DispatchingToolExecutor executor, FunctionCallContent call, ToolExecutionContext context, ToolApprovalMode mode)
    {
        if (mode != ToolApprovalMode.Approval)
            return;

        var decision = await executor.EvaluateAuthorizationAsync(call, context, CancellationToken.None);
        Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, decision.Outcome);
        Assert.NotNull(decision.ApprovalContext);
        context.Approval.SeedOneTimeApproval(
            decision.ApprovalContext.ToolName, OneTimeApprovalKeys.Create(decision.ApprovalContext));
    }

    // The real dispatcher and shell policy use this probe. No mutant can start a host process.
    private sealed class ShellProbeTool : INetclawTool
    {
        private readonly AIFunction _function = AIFunctionFactory.Create((string Command) => Command, "shell_execute");
        public int Calls { get; private set; }
        public string Name => "shell_execute";
        public LlmFacingToolName LlmFacingName => LlmFacingToolName.FromCanonical(Name);
        public string Description => "Count authorized calls.";
        public string GrantCategory => "shell";
        public JsonElement ParameterSchema => _function.JsonSchema;
        public AITool ToAITool() => _function;

        public Task<string> ExecuteAsync(
            IDictionary<string, object?>? arguments, ToolInvocationContext context, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult("mutation-probe");
        }
    }

    private sealed class UnexpectedApprovalBridge : IParentApprovalBridge
    {
        public Task<ParentApprovalDecision> RequestApprovalAsync(
            ToolCallId callId, string toolName, string displayText, IReadOnlyList<string> patterns,
            IReadOnlyList<string> candidateVerbs, IReadOnlyList<ParentApprovalCandidate> candidates,
            string? cwd, IReadOnlyList<ParentApprovalOption> options, bool isMessy, CancellationToken ct) =>
            throw new InvalidOperationException("The dispatcher must not request user approval through the bridge.");
    }
}

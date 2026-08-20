// -----------------------------------------------------------------------
// <copyright file="ToolFrictionReplayTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tests.Sessions.Pipelines;
using Netclaw.Actors.Tests.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Tests;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using ShellSyntaxTree;
using Xunit;
using RecordingChatClient = Netclaw.Actors.Tests.Sessions.FakeChatClient;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Tools;

public sealed class ToolFrictionReplayTests(ITestOutputHelper output) : TestKit(output: output)
{
    private const string FixtureFile = "tool-friction-fixtures.json";

    protected override void ConfigureAkka(Akka.Hosting.AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    [Theory]
    [InlineData("TF01")]
    [InlineData("TF02")]
    [InlineData("TF04")]
    [InlineData("TF05")]
    [InlineData("TF06")]
    [InlineData("TF07")]
    public async Task Sanitized_friction_case_replays_through_real_tool_boundaries(string caseId)
    {
        var policyCase = LoadCase(caseId);
        using var temp = new DisposableTempDir();
        var setup = await CreateScenarioAsync(temp.Path, policyCase, TestContext.Current.CancellationToken);
        var runtime = CreateRuntime(setup.DeniedPath);

        Assert.Equal(
            policyCase.ExpectedToolSequence,
            setup.Calls.Select(static call => call.Name));

        var current = new WorkingContext
        {
            ProjectDirectory = setup.ProjectDirectory,
            RecentFiles = ImmutableList.Create(setup.SeedRecentFile)
        };

        if (policyCase.ExpectedContextEffect == "CoreOnlyChildCatalog")
        {
            await AssertChildCatalogAsync(runtime, setup);
            AssertContextEffect(policyCase.ExpectedContextEffect, setup, current);
            return;
        }

        for (var index = 0; index < setup.Calls.Count; index++)
        {
            var call = setup.Calls[index];
            var registration = runtime.Registry.GetRegistrationByToolName(call.Name);
            Assert.NotNull(registration);
            Assert.Equal(call.Name, registration.Tool.Name);

            var authorization = await runtime.Executor.EvaluateAuthorizationAsync(
                call,
                CreateContext(setup, current),
                TestContext.Current.CancellationToken);
            Assert.Equal(
                policyCase.ExpectedApprovalRequired,
                authorization.Outcome == ToolAuthorizationOutcome.RequiresApproval);
            Assert.Equal(ToolAuthorizationOutcome.Allowed, authorization.Outcome);

            var completed = await ExecuteAsync(
                runtime.Executor,
                setup,
                current,
                call,
                $"{caseId.ToLowerInvariant()}-{index}");
            var receipt = Assert.Single(completed.ToolReceipts).Value;
            Assert.Equal(ParseOutcome(policyCase.ExpectedOutcome), receipt.Category);
            current = WorkingContextUpdater.UpdateFromToolReceipts(
                current,
                completed.ToolResults,
                completed.ToolReceipts);
        }

        if (setup.Fallback is { } fallback)
        {
            var fallbackDecision = await runtime.Executor.EvaluateAuthorizationAsync(
                fallback,
                CreateContext(setup, current),
                TestContext.Current.CancellationToken);
            Assert.Equal(
                policyCase.FallbackApprovalRequired,
                fallbackDecision.Outcome == ToolAuthorizationOutcome.RequiresApproval);
            Assert.Equal(ToolAuthorizationOutcome.RequiresApproval, fallbackDecision.Outcome);
        }
        else
        {
            Assert.False(policyCase.FallbackApprovalRequired);
        }

        AssertContextEffect(policyCase.ExpectedContextEffect, setup, current);
    }

    [Fact]
    public async Task Pipeline_emits_only_the_bounded_outcome_category()
    {
        using var temp = new DisposableTempDir();
        var policyCase = LoadCase("TF01");
        var setup = await CreateScenarioAsync(temp.Path, policyCase, TestContext.Current.CancellationToken);
        var runtime = CreateRuntime(setup.DeniedPath);
        var current = new WorkingContext { ProjectDirectory = setup.ProjectDirectory };

        await EventFilter.Info(message: "Tool outcome category=Success").ExpectAsync(1, async () =>
        {
            _ = await ExecuteAsync(runtime.Executor, setup, current, setup.Calls[0], "outcome-log");
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task<ToolExecutionCompleted> ExecuteAsync(
        DispatchingToolExecutor executor,
        ScenarioSetup setup,
        WorkingContext current,
        FunctionCallContent call,
        string suffix)
    {
        var sessionId = new SessionId($"signalr/tool-friction-{suffix}");
        var probe = CreateTestProbe($"tool-friction-{suffix}");
        var pipeline = new SessionToolPipelineTestFixture(executor, [call], sessionId, probe.Ref)
            .WithTurnContext(InteractiveTurnContext(sessionId))
            .InSessionDirectory(setup.SessionDirectory)
            .InProject(setup.ProjectDirectory, current.RecentFiles)
            .WithLogger(Logging.GetLogger(Sys, typeof(ToolFrictionReplayTests)))
            .ExecuteAsync(TestContext.Current.CancellationToken);

        var completed = await probe.ExpectMsgAsync<ToolExecutionCompleted>(
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);
        await pipeline.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        return completed;
    }

    private static TurnContext InteractiveTurnContext(SessionId sessionId) => new()
    {
        SessionId = sessionId,
        TurnId = new TurnId("tool-friction-turn"),
        Audience = TrustAudience.Personal,
        Boundary = TrustBoundary.Personal,
        ChannelType = ChannelType.SignalR,
        RequesterSenderId = new SenderId("synthetic-operator"),
        RequesterPrincipal = PrincipalClassification.Operator,
        Provenance = new SourceProvenance(
            TransportAuthenticity.Verified,
            PayloadTaint.Trusted),
        SupportsInteractiveApproval = true
    };

    private static ToolExecutionContext CreateContext(ScenarioSetup setup, WorkingContext current)
        => TestToolExecutionContext.CreateBound(
            "signalr/tool-friction-context",
            setup.SessionDirectory,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                Boundary = TrustBoundary.Personal,
                ChannelType = ChannelType.SignalR.ToWireValue(),
                ProjectDirectory = current.ProjectDirectory,
                RecentFiles = current.RecentFiles
            });

    private static RuntimeSetup CreateRuntime(string deniedPath)
    {
        var environment = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux);
        var commandPolicy = new ShellCommandPolicy(environment);
        var authorizationPathPolicy = new ToolPathPolicy(environment, []);
        var toolPathPolicy = new ToolPathPolicy(environment, [deniedPath]);
        var config = new ToolConfig
        {
            ShellMode = ShellExecutionMode.HostAllowed,
            AudienceProfiles = ToolAudienceProfileDefaults.CreateProfilesForPosture(DeploymentPosture.Personal)
        };
        var accessPolicy = new ToolAccessPolicy(
            config,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            commandPolicy,
            authorizationPathPolicy);
        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(
            config,
            new NetclawPaths(),
            toolPathPolicy,
            commandPolicy,
            toolAccessPolicy: accessPolicy);
        return new RuntimeSetup(registry, new DispatchingToolExecutor(registry, accessPolicy), accessPolicy);
    }

    private async Task AssertChildCatalogAsync(RuntimeSetup runtime, ScenarioSetup setup)
    {
        const string attachToolName = "attach_file";
        var client = new RecordingChatClient();
        client.PlannedResponses.Enqueue([setup.Calls[0]]);
        client.PlannedResponses.Enqueue([setup.Calls[1]]);
        client.PlannedResponses.Enqueue([new TextContent("Catalog replay complete.")]);

        var registrations = runtime.Registry.GetAllRegistrations();
        var coreNames = runtime.Registry.GetCoreRegistrations()
            .Select(static registration => registration.Tool.Name)
            .ToHashSet(StringComparer.Ordinal);
        var definition = new SubAgentDefinition
        {
            Name = new AgentName("fixture-agent"),
            SystemPrompt = "Replay one sanitized catalog case.",
            Tools = registrations.Select(static registration => registration.Tool).ToList()
        };
        var actor = Sys.ActorOf(SubAgentActor.CreatePropsWithProjectInstructionProvider(
            definition,
            client,
            runtime.AccessPolicy,
            NullSystemPromptProvider.Instance,
            coreToolNames: coreNames));

        var result = await actor.Ask<SubAgentProtocol.SubAgentResult>(
            new SubAgentProtocol.RunSubAgent
            {
                Scope = SubAgentTestScope.Create(
                    scopeId: "signalr/tool-friction/subagent/fixture-agent/run",
                    sessionDirectory: setup.SessionDirectory,
                    projectDirectory: setup.ProjectDirectory),
                Task = "Find and load the deferred attachment tool.",
                Timeout = TimeSpan.FromSeconds(5)
            },
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(3, client.ReceivedToolNames.Count);
        var expectedCore = coreNames.OrderBy(static name => name, StringComparer.Ordinal).ToList();
        Assert.Equal(expectedCore, client.ReceivedToolNames[0].OrderBy(static name => name, StringComparer.Ordinal));
        Assert.Equal(expectedCore, client.ReceivedToolNames[1].OrderBy(static name => name, StringComparer.Ordinal));
        Assert.DoesNotContain(attachToolName, client.ReceivedToolNames[0]);
        Assert.DoesNotContain(attachToolName, client.ReceivedToolNames[1]);
        Assert.Contains(attachToolName, client.ReceivedToolNames[2]);
        Assert.Contains(
            attachToolName,
            GetToolResult(client.ReceivedMessages[1], setup.Calls[0].CallId),
            StringComparison.Ordinal);
        Assert.Equal(
            attachToolName,
            GetToolResult(client.ReceivedMessages[2], setup.Calls[1].CallId));
    }

    private static string GetToolResult(IReadOnlyList<ChatMessage> messages, string callId)
    {
        var result = messages
            .SelectMany(static message => message.Contents.OfType<FunctionResultContent>())
            .Single(content => content.CallId == callId)
            .Result;
        return Assert.IsType<string>(result);
    }

    private static async Task<ScenarioSetup> CreateScenarioAsync(
        string root,
        ToolFrictionCase policyCase,
        CancellationToken cancellationToken)
    {
        var project = Path.Join(root, "project");
        var session = Path.Join(root, "sessions", "current");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(session);
        var seed = Path.Join(project, "seed.txt");
        var denied = Path.Join(project, "blocked.txt");
        await File.WriteAllTextAsync(seed, "seed", cancellationToken);

        return policyCase.Id switch
        {
            "TF01" => await RecursiveSearchAsync(project, session, seed, denied, cancellationToken),
            "TF02" => await ComposedReadAsync(project, session, seed, denied, cancellationToken),
            "TF04" => await ImageMetadataAsync(project, session, seed, denied, cancellationToken),
            "TF05" => await SpillContinuationAsync(project, session, seed, denied, cancellationToken),
            "TF06" => FailedFileActivity(project, session, seed, denied),
            "TF07" => SubagentCatalogExposure(project, session, seed, denied),
            _ => throw new InvalidOperationException($"Unsupported fixture case: {policyCase.Id}")
        };
    }

    private static async Task<ScenarioSetup> RecursiveSearchAsync(
        string project,
        string session,
        string seed,
        string denied,
        CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(
            Path.Join(project, "notes.txt"),
            "fixture-marker",
            cancellationToken);
        return Setup(
            project,
            session,
            seed,
            denied,
            [Call("search", FileSearchTool.ToolName, "Root", ".", "Query", "fixture-marker", "Mode", "content")],
            PythonFallback("search-fallback", project, "print('recursive search')"));
    }

    private static async Task<ScenarioSetup> ComposedReadAsync(
        string project,
        string session,
        string seed,
        string denied,
        CancellationToken cancellationToken)
    {
        var first = Path.Join(project, "first.txt");
        var second = Path.Join(project, "second.txt");
        await File.WriteAllTextAsync(first, "first", cancellationToken);
        await File.WriteAllTextAsync(second, "second", cancellationToken);
        return Setup(
            project,
            session,
            seed,
            denied,
            [
                Call("read-first", FileReadTool.ToolName, "Path", "first.txt"),
                Call("read-second", FileReadTool.ToolName, "Path", "second.txt")
            ],
            PythonFallback("batch-fallback", project, "print(open('first.txt').read()); print(open('second.txt').read())"),
            [first, second]);
    }

    private static async Task<ScenarioSetup> ImageMetadataAsync(
        string project,
        string session,
        string seed,
        string denied,
        CancellationToken cancellationToken)
    {
        var path = Path.Join(project, "image.png");
        await File.WriteAllBytesAsync(path, PngHeader(3, 2), cancellationToken);
        return Setup(
            project,
            session,
            seed,
            denied,
            [Call("image", FileReadTool.ToolName, "Path", "image.png")],
            PythonFallback("image-fallback", project, "print('inspect image metadata')"),
            [path]);
    }

    private static async Task<ScenarioSetup> SpillContinuationAsync(
        string project,
        string session,
        string seed,
        string denied,
        CancellationToken cancellationToken)
    {
        const string callId = "fixture-spill";
        Assert.True(ToolOutputSpillLocation.TryResolve(session, callId, out var directory, out var path));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, new string('x', 256), cancellationToken);
        return Setup(
            project,
            session,
            seed,
            denied,
            [Call("spill", ToolOutputReadTool.ToolName, "CallId", callId, "Start", 0, "Limit", 128)],
            PythonFallback("spill-fallback", project, "print('continue retained output')"));
    }

    private static ScenarioSetup FailedFileActivity(
        string project,
        string session,
        string seed,
        string denied)
        => Setup(
            project,
            session,
            seed,
            denied,
            [Call("denied-write", FileWriteTool.ToolName, "Path", "blocked.txt", "Content", "must not write")],
            fallback: null);

    private static ScenarioSetup SubagentCatalogExposure(
        string project,
        string session,
        string seed,
        string denied)
        => Setup(
            project,
            session,
            seed,
            denied,
            [
                Call("search-tools", "search_tools", "Query", "attach local file"),
                Call("load-tool", "load_tool", "Name", "attach_file")
            ],
            fallback: null);

    private static ScenarioSetup Setup(
        string project,
        string session,
        string seed,
        string denied,
        IReadOnlyList<FunctionCallContent> calls,
        FunctionCallContent? fallback,
        IReadOnlyList<string>? expectedFiles = null)
        => new(
            project,
            session,
            seed,
            denied,
            calls,
            fallback,
            expectedFiles ?? []);

    private static FunctionCallContent PythonFallback(string id, string workingDirectory, string body)
        => Call(
            id,
            ShellTool.ToolName,
            "Command",
            $"python -c \"{body}\"",
            "WorkingDirectory",
            workingDirectory);

    private static FunctionCallContent Call(string id, string name, params object?[] values)
    {
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["_rationale"] = "Replay one sanitized tool-friction case."
        };
        for (var index = 0; index < values.Length; index += 2)
            arguments.Add((string)values[index]!, values[index + 1]);
        return new FunctionCallContent($"fixture-{id}", name, arguments);
    }

    private static void AssertContextEffect(
        string expectedEffect,
        ScenarioSetup setup,
        WorkingContext current)
    {
        Assert.Equal(setup.ProjectDirectory, current.ProjectDirectory);
        switch (expectedEffect)
        {
            case "RecordOneCanonicalFile":
            case "RecordTwoCanonicalFiles":
                Assert.Equal(setup.ExpectedFiles.Count + 1, current.RecentFiles.Count);
                Assert.All(setup.ExpectedFiles, path => Assert.Contains(path, current.RecentFiles));
                Assert.Contains(setup.SeedRecentFile, current.RecentFiles);
                break;
            case "NoContextChangeRequired":
            case "PreserveRecentFiles":
            case "CoreOnlyChildCatalog":
                Assert.Equal([setup.SeedRecentFile], current.RecentFiles);
                break;
            default:
                throw new InvalidOperationException($"Unsupported context effect: {expectedEffect}");
        }
    }

    private static ToolInvocationOutcomeCategory ParseOutcome(string outcome)
        => outcome switch
        {
            "success" => ToolInvocationOutcomeCategory.Success,
            "access_denied" => ToolInvocationOutcomeCategory.AccessDenied,
            _ => throw new InvalidOperationException($"Unsupported outcome: {outcome}")
        };

    private static ToolFrictionCase LoadCase(string caseId)
    {
        var path = Path.Join(AppContext.BaseDirectory, "ToolFrictionEvidence", FixtureFile);
        var catalog = JsonSerializer.Deserialize(
                          File.ReadAllBytes(path),
                          ToolFrictionEvidenceJsonContext.Default.ToolFrictionFixtureCatalog)
                      ?? throw new InvalidOperationException("Tool-friction evidence must deserialize.");
        return Assert.Single(catalog.Cases, item => item.Id == caseId);
    }

    private static byte[] PngHeader(int width, int height) =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
        (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height
    ];

    private sealed record RuntimeSetup(
        ToolRegistry Registry,
        DispatchingToolExecutor Executor,
        ToolAccessPolicy AccessPolicy);

    private sealed record ScenarioSetup(
        string ProjectDirectory,
        string SessionDirectory,
        string SeedRecentFile,
        string DeniedPath,
        IReadOnlyList<FunctionCallContent> Calls,
        FunctionCallContent? Fallback,
        IReadOnlyList<string> ExpectedFiles);
}

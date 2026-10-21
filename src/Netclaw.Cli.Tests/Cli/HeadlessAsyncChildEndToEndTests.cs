// -----------------------------------------------------------------------
// <copyright file="HeadlessAsyncChildEndToEndTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Netclaw.Daemon;
using Netclaw.Daemon.Gateway;
using Netclaw.Daemon.Security;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Cli.Tests.Cli;

/// <summary>
/// Drives <c>netclaw chat -p</c> through a turn that calls <c>spawn_agent</c>. <c>spawn_agent</c> returns
/// <c>accepted</c> at once, and the child result arrives in a later parent turn. The run must stay until that
/// turn ends, and it must end loudly when it cannot. The models are scripts. The session actor, the child
/// run, the SignalR hub, the daemon client, and the headless channel are the real ones. The contract is in the
/// <c>netclaw-cli</c> spec and in SPEC-004 ("Waiting for child runs").
/// </summary>
/// <remarks>
/// No test waits on a delay. A script gates the child model or the parent model on a task. A driver waits on
/// a stdout line.
/// </remarks>
[Collection(HeadlessProcessStateCollection.Name)]
public sealed class HeadlessAsyncChildEndToEndTests : IAsyncDisposable
{
    private const string ChildText = "CHILD-OUTPUT-42";
    private const string StartedText = "I started the child.";
    private const string FinalText = "The child reported: CHILD-OUTPUT-42.";

    // The channel writes each line with WriteLine, so the line ending is "\r\n" on Windows. Multiline mode
    // matches "$" before "\n" only, so each pattern accepts an optional "\r" before it.
    private static readonly Regex WaitingLine = new(@"^\[subagent:waiting\] 1 open: (?<run>\S+)\r?$", RegexOptions.Multiline);
    private static readonly Regex OpenRunsLine = new(@"^\[headless:open-runs\] session=(?<session>\S+) runs=(?<runs>\S+)\r?$", RegexOptions.Multiline);

    private readonly DisposableTempDir _directory = new();
    private readonly TaskCompletionSource _childGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _childDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _turnOneText = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebApplication? _app;
    private DaemonClient? _client;

    public async ValueTask DisposeAsync()
    {
        // A test that fails early must not leave the child model parked.
        _childGate.TrySetResult();
        if (_client is not null)
            await _client.DisposeAsync();
        if (_app is not null)
            await _app.DisposeAsync();
        _directory.Dispose();
    }

    // ----- Success -----

    [Fact]
    public async Task Text_run_waits_for_the_child_and_prints_its_result()
    {
        // Planted bug: the run ends at turn 1 with the status text, so the child result never reaches stdout.
        var model = new ParentModel(startedText: StartedText, finalText: FinalText);

        var run = await RunAsync(model, json: false, drive: async drive =>
        {
            // The child model waits until the CLI shows that it waits. The child finishes after turn 1.
            if (await drive.StdoutShowsAsync("[subagent:waiting]"))
                _childGate.TrySetResult();
        });

        Assert.Equal(0, run.ExitCode);
        Assert.Empty(run.TurnFailedLines);
        Assert.Contains(StartedText, run.Stdout, StringComparison.Ordinal);
        var waiting = WaitingLine.Match(run.Stdout);
        Assert.True(waiting.Success, "Stdout has no [subagent:waiting] line.");
        Assert.Contains("[subagent:start] child-a", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("[subagent:done] child-a (completed", run.Stdout, StringComparison.Ordinal);
        // The notice names result.md. The parent reads it with file_read, so the child text reaches stdout as tool output.
        // The result is the multi-line text of result.md, so it runs from its marker to the final reply.
        Assert.Single(
            run.Stdout.Split(['\r', '\n']), line => line.StartsWith("[tool:result] file_read", StringComparison.Ordinal));
        Assert.Contains(FinalText, run.Stdout, StringComparison.Ordinal);
        var resultStart = run.Stdout.IndexOf("[tool:result] file_read", StringComparison.Ordinal);
        var resultText = run.Stdout[resultStart..run.Stdout.IndexOf(FinalText, resultStart, StringComparison.Ordinal)];
        Assert.Contains(ChildText, resultText, StringComparison.Ordinal);
        Assert.Contains(waiting.Groups["run"].Value, resultText, StringComparison.Ordinal);
        Assert.True(
            run.Stdout.IndexOf(StartedText, StringComparison.Ordinal)
            < run.Stdout.IndexOf("[subagent:waiting]", StringComparison.Ordinal));
        Assert.True(
            run.Stdout.IndexOf("[subagent:waiting]", StringComparison.Ordinal)
            < run.Stdout.IndexOf(FinalText, StringComparison.Ordinal));
        Assert.Equal(1, CountOf(run.SessionLog, "SESSION_ENDED"));
        Assert.Equal(2, CountOf(run.SessionLog, "TURN_COMPLETED:"));
        Assert.Equal(1, CountOf(run.SessionLog, "WAITING_FOR_CHILDREN:"));
        Assert.Equal(4, model.CallCount);
    }

    [Fact]
    public async Task Json_run_gives_one_envelope_that_holds_both_turns()
    {
        var model = new ParentModel(startedText: StartedText, finalText: FinalText);
        // JSON mode prints no waiting line. The child finishes once the parent has answered in turn 1.
        model.AfterTurnOneText = _turnOneText;

        var run = await RunAsync(model, json: true, drive: async _ =>
        {
            await _turnOneText.Task;
            _childGate.TrySetResult();
        });

        Assert.Equal(0, run.ExitCode);
        Assert.DoesNotContain("[subagent:", run.Stdout, StringComparison.Ordinal);
        using var envelope = Assert.Single(Envelopes(run));
        var root = envelope.RootElement;
        Assert.Equal(FinalText, root.GetProperty("response").GetString());
        var turns = root.GetProperty("turnResponses").EnumerateArray().ToArray();
        Assert.Equal(2, turns.Length);
        Assert.Equal(StartedText, turns[0].GetProperty("text").GetString());
        Assert.Equal(FinalText, turns[1].GetProperty("text").GetString());
        var toolNames = root.GetProperty("toolCalls").EnumerateArray()
            .Select(call => call.GetProperty("toolName").GetString())
            .ToArray();
        Assert.Contains("spawn_agent", toolNames);
        Assert.Contains("file_read", toolNames);
        Assert.False(root.TryGetProperty("errorCategory", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_does_not_end_at_turn_one_when_the_child_finishes_inside_turn_one(bool json)
    {
        // The race: the child finishes before the parent ends turn 1, and the session starts turn 2 right
        // after TurnCompleted of turn 1. A client that counts started minus completed children exits at
        // turn 1 and loses the child result.
        // The child starts only after the second parent call began. That call has then already chosen its
        // history, so the session cannot fold the child result into turn 1. The call returns after the child
        // is done, so the result is ready, or nearly ready, when turn 1 ends.
        var model = new ParentModel(startedText: StartedText, finalText: FinalText)
        {
            SecondCallWaitsFor = _childDone.Task,
            OnSecondCall = () => _childGate.TrySetResult()
        };

        var run = await RunAsync(model, json);

        Assert.Equal(0, run.ExitCode);
        if (json)
        {
            using var envelope = Assert.Single(Envelopes(run));
            Assert.Equal(FinalText, envelope.RootElement.GetProperty("response").GetString());
            Assert.Equal(2, envelope.RootElement.GetProperty("turnResponses").GetArrayLength());
        }
        else
        {
            Assert.Contains(FinalText, run.Stdout, StringComparison.Ordinal);
            Assert.Contains(ChildText, run.Stdout, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_run_with_no_child_ends_at_its_first_turn_as_before(bool json)
    {
        // Regression guard: no tool call, one turn, no waiting line, one entry in turnResponses.
        var model = new ParentModel(startedText: "Done.", finalText: "unused") { SpawnChild = false };

        var run = await RunAsync(model, json);

        Assert.Equal(0, run.ExitCode);
        Assert.DoesNotContain("[subagent:waiting]", run.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("WAITING_FOR_CHILDREN", run.SessionLog, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(run.SessionLog, "SESSION_ENDED"));
        Assert.Equal(1, model.CallCount);
        if (!json)
        {
            Assert.Contains("Done.", run.Stdout, StringComparison.Ordinal);
            return;
        }

        using var envelope = Assert.Single(Envelopes(run));
        Assert.Equal("Done.", envelope.RootElement.GetProperty("response").GetString());
        Assert.Single(envelope.RootElement.GetProperty("turnResponses").EnumerateArray());
        Assert.False(envelope.RootElement.TryGetProperty("toolCalls", out _));
    }

    // ----- Failure and stop paths -----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_turn_with_an_open_child_ends_the_run_at_once(bool json)
    {
        // Planted bug: the run waits for the child after the failed turn, or it omits the open-runs line.
        var model = new ParentModel(startedText: StartedText, finalText: FinalText)
        {
            SecondCallFails = new HttpRequestException("Service Unavailable", null, HttpStatusCode.ServiceUnavailable)
        };

        var run = await RunAsync(model, json);

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(["[turn:failed] category=infra kind=ProviderFailure"], run.TurnFailedLines);
        var open = Assert.Single(OpenRunsLine.Matches(run.Stderr));
        Assert.Matches(@"^[A-Za-z0-9_-]+$", open.Groups["runs"].Value);
        Assert.Contains($"Headless session started: {open.Groups["session"].Value}", run.SessionLog, StringComparison.Ordinal);
        Assert.DoesNotContain("[subagent:waiting]", run.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(FinalText, run.Stdout, StringComparison.Ordinal);
        if (json)
        {
            using var envelope = Assert.Single(Envelopes(run));
            Assert.Equal("infra", envelope.RootElement.GetProperty("errorCategory").GetString());
        }
    }

    [Fact]
    public async Task A_signal_during_the_wait_ends_the_run_and_names_the_open_run_and_the_session()
    {
        using var stopping = new CancellationTokenSource();
        var model = new ParentModel(startedText: StartedText, finalText: FinalText);

        var run = await RunAsync(model, json: false, drive: async drive =>
        {
            if (await drive.StdoutShowsAsync("[subagent:waiting]"))
                await stopping.CancelAsync();
        }, stopping);

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(["[turn:failed] category=infra kind=Cancelled"], run.TurnFailedLines);
        var waiting = WaitingLine.Match(run.Stdout);
        Assert.True(waiting.Success, "Stdout has no [subagent:waiting] line.");
        var open = Assert.Single(OpenRunsLine.Matches(run.Stderr));
        Assert.Equal(waiting.Groups["run"].Value, open.Groups["runs"].Value);
        Assert.Contains($"Headless session started: {open.Groups["session"].Value}", run.SessionLog, StringComparison.Ordinal);
        // The run never reports success with only a status line.
        Assert.DoesNotContain(FinalText, run.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_signal_during_the_wait_in_json_mode_writes_no_envelope()
    {
        using var stopping = new CancellationTokenSource();
        var model = new ParentModel(startedText: StartedText, finalText: FinalText) { AfterTurnOneText = _turnOneText };

        var run = await RunAsync(model, json: true, drive: async _ =>
        {
            // The parent answered in turn 1. The child model still waits, so the run is in the wait or
            // about to be. Either way the signal ends it.
            await _turnOneText.Task;
            await stopping.CancelAsync();
        }, stopping);

        Assert.Equal(1, run.ExitCode);
        Assert.Empty(Envelopes(run));
        Assert.Equal(["[turn:failed] category=infra kind=Cancelled"], run.TurnFailedLines);
    }

    [Fact]
    public async Task A_daemon_restart_during_the_wait_ends_the_run_loudly()
    {
        // Planted bugs: the hub does not forward SessionDeactivated, or the client reads it as an unknown
        // output. The run then hangs until an outside timeout kills it.
        var model = new ParentModel(startedText: StartedText, finalText: FinalText);

        var run = await RunAsync(model, json: false, drive: async drive =>
        {
            if (!await drive.StdoutShowsAsync("[subagent:waiting]"))
                return;

            var manager = _app!.Services.GetRequiredService<IRequiredActor<SessionManagerActorKey>>();
            var sessionManager = await manager.GetAsync(TestContext.Current.CancellationToken);
            await sessionManager.Ask<CommandAck>(
                new PrepareForDaemonRestart(new SessionId(drive.SessionId()), "test-restart"),
                TimeSpan.FromSeconds(20),
                TestContext.Current.CancellationToken);
        });

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(["[turn:failed] category=infra kind=ChannelFailure"], run.TurnFailedLines);
        Assert.Contains("[headless:error]", run.Stderr, StringComparison.Ordinal);
        var open = Assert.Single(OpenRunsLine.Matches(run.Stderr));
        Assert.Contains($"Headless session started: {open.Groups["session"].Value}", run.SessionLog, StringComparison.Ordinal);
        Assert.DoesNotContain(FinalText, run.Stdout, StringComparison.Ordinal);
    }

    // ----- Setup -----

    private async Task<HeadlessRun> RunAsync(
        ParentModel parent,
        bool json,
        Func<HeadlessDrive, Task>? drive = null,
        CancellationTokenSource? stop = null)
    {
        var paths = new NetclawPaths(_directory.Path);
        paths.EnsureDirectoriesExist();
        var child = new ChildModel(ChildText, _childGate.Task, _childDone);
        var models = new RoleModels(parent, child);

        var registry = new ToolRegistry();
        registry.RegisterCore(AIFunctionFactory.Create(() => "probe result", "child_probe"), "builtin");
        var profiles = new SubAgentDefinitionRegistry();
        profiles.Register(new SubAgentProfile
        {
            Name = "child-a",
            Description = "child-a",
            SystemPrompt = "child-a",
            ModelRole = ModelRole.Fallback,
            ToolNames = ["child_probe"],
            EmitStructuredFindings = false,
            Visibility = SubAgentVisibility.UserFacing
        });
        var policy = new ToolAccessPolicy(paths, new ToolConfig(),
            new EffectivePolicyDefaults(DeploymentPosture.Personal, TrustAudience.Personal,
                ShellExecutionMode.HostAllowed, UsedStrictFallback: false),
            new ShellCommandPolicy(), new ToolPathPolicy([]));
        var prompt = new StaticSystemPromptProvider("You are a test assistant with subagents.");
        var spawner = new SubAgentSpawner(models, registry, policy, null, prompt,
            new FixedWorkingContext(), NullLogger<SubAgentSpawner>.Instance, new SessionConfig());
        registry.RegisterCore(new SpawnAgentTool(profiles, spawner, paths));
        // The spawner refuses a start when the requester cannot read the result file.
        registry.RegisterCore(new FileReadTool(new ToolConfig(), paths, new ToolPathPolicy([])));
        var storage = new TestSessionStorageResolver(paths);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSessionHubSignalR();
        builder.Services.AddSingleton(new DaemonConfig());
        builder.Services.AddAuthentication(LoopbackAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, LoopbackAuthenticationHandler>(
                LoopbackAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton<IOperationalNotificationSink>(NullNotificationSink.Instance);
        builder.Services.AddSingleton<ISessionStorageResolver>(storage);
        builder.Services.AddSingleton(new SessionConfig
        {
            Tuning = new SessionTuning { SnapshotInterval = 5, TitleGenerationInterval = 0 }
        });
        builder.Services.AddSingleton(new ModelCapabilities { ModelId = "scripted-model", ContextWindowTokens = 128_000 });
        builder.Services.AddSingleton(new SessionServices(
            models,
            prompt,
            [],
            new FixedWorkingContext(),
            TimeProvider.System,
            paths,
            storage));
        builder.Services.AddSingleton(new SessionToolServices(
            new DispatchingToolExecutor(registry, policy, null, NullLogger<DispatchingToolExecutor>.Instance),
            registry, null, null, null, null, profiles, spawner, null));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ClaimsPrincipalMapper>();
        builder.Services.AddSingleton<SessionIngressGate>();
        builder.Services.AddSingleton<SessionRegistry>();
        builder.Services.AddSingleton<SessionPipeline>();
        builder.Services.AddSingleton<ISessionPipeline>(sp => sp.GetRequiredService<SessionPipeline>());
        builder.Services.AddAkka("headless-async-child", akka => akka
            .WithInMemoryJournal()
            .WithInMemorySnapshotStore()
            .WithNetclawSerialization()
            .WithSessionManager()
            .WithSignalRGateway());

        var app = builder.Build();
        _app = app;
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            await next(context);
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHub<SessionHub>("/hub/session");
        await app.StartAsync(TestContext.Current.CancellationToken);

        _client = InMemorySignalRClientFactory.Create(app);
        return await HeadlessRunHarness.RunAsync(
            _client,
            _directory,
            json,
            stop?.Token ?? TestContext.Current.CancellationToken,
            drive: drive);
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal);
             at >= 0;
             at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    // Every stdout line that is a JSON envelope. The in-process actor system writes its own lines to the same
    // console, so a line counts only when it parses and holds a session id.
    private static List<JsonDocument> Envelopes(HeadlessRun run)
    {
        var found = new List<JsonDocument>();
        foreach (var line in run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()))
        {
            if (!line.StartsWith('{') || !line.EndsWith('}'))
                continue;
            var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("sessionId", out _))
                found.Add(document);
            else
                document.Dispose();
        }

        return found;
    }

    private sealed class FixedWorkingContext : IWorkingContextSnapshotProvider
    {
        public Task<WorkingContextSnapshot> CreateAsync(
            WorkingContext context, TrustAudience audience, CancellationToken cancellationToken)
            => Task.FromResult(new WorkingContextSnapshot
            {
                WorkingContext = context,
                Git = new GitWorkingContextInspection.Skipped()
            });
    }

    private sealed class RoleModels(IChatClient parent, IChatClient child) : IChatClientProvider
    {
        private readonly IChatClient _sidecar = new ReplyModel("sidecar reply");

        public IChatClient GetClient(ModelRole role) => role switch
        {
            ModelRole.Main => parent,
            ModelRole.Fallback => child,
            _ => _sidecar
        };
    }
}

/// <summary>
/// The parent model. Call 1 starts a child run through <c>spawn_agent</c> (unless <see cref="SpawnChild"/> is
/// false). Call 2 is the reply after the acceptance. Call 3 is the reply after the child result arrives.
/// </summary>
internal sealed class ParentModel(string startedText, string finalText) : IChatClient
{
    private int _calls;

    public int CallCount => Volatile.Read(ref _calls);

    public bool SpawnChild { get; init; } = true;

    /// <summary>The second call waits for this task. It lets a child finish inside turn 1.</summary>
    public Task? SecondCallWaitsFor { get; init; }

    /// <summary>The second call throws this exception, so turn 1 fails while the child run is open.</summary>
    public Exception? SecondCallFails { get; init; }

    /// <summary>Runs when the second call starts, before it waits.</summary>
    public Action? OnSecondCall { get; init; }

    /// <summary>Completed when the second call returns its text, before turn 1 completes.</summary>
    public TaskCompletionSource? AfterTurnOneText { get; set; }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var call = Interlocked.Increment(ref _calls);
        IReadOnlyList<AIContent> contents;
        if (!SpawnChild)
        {
            contents = [new TextContent(startedText)];
        }
        else if (call == 1)
        {
            contents =
            [
                new FunctionCallContent("spawn-1", "spawn_agent", new Dictionary<string, object?>
                {
                    ["agent"] = "child-a",
                    ["task"] = "Compute the answer.",
                    ["_rationale"] = "Start a child run."
                })
            ];
        }
        else if (call == 2)
        {
            OnSecondCall?.Invoke();
            if (SecondCallWaitsFor is not null)
                await SecondCallWaitsFor.WaitAsync(cancellationToken);
            if (SecondCallFails is not null)
                throw SecondCallFails;
            contents = [new TextContent(startedText)];
            AfterTurnOneText?.TrySetResult();
        }
        else if (ResultFileToRead(messages) is { } resultFile)
        {
            // The notice turn reads the result file that the notice names.
            contents =
            [
                new FunctionCallContent("read-result", "file_read", new Dictionary<string, object?>
                {
                    ["path"] = resultFile,
                    ["_rationale"] = "Read the child result."
                })
            ];
        }
        else
        {
            contents = [new TextContent(finalText)];
        }

        return new ChatResponse(new ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, contents.ToList()))
        {
            FinishReason = ChatFinishReason.Stop
        };
    }

    private static string? ResultFileToRead(IEnumerable<ChatMessage> messages)
    {
        var list = messages.ToList();
        if (list.Any(message => message.Contents.OfType<FunctionResultContent>().Any(result => result.CallId == "read-result")))
            return null;
        var notice = list.LastOrDefault(message => message.Role == Microsoft.Extensions.AI.ChatRole.User
                                                   && message.Text.Contains("result_file=", StringComparison.Ordinal));
        return notice is null ? null : Regex.Match(notice.Text, @"result_file=(\S+)").Groups[1].Value;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>The child model. It waits for the gate, then replies with its text and marks itself done.</summary>
internal sealed class ChildModel(string text, Task gate, TaskCompletionSource done) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        done.TrySetResult();
        return new ChatResponse(new ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, text))
        {
            FinishReason = ChatFinishReason.Stop
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>A model that always answers with one text.</summary>
internal sealed class ReplyModel(string text) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, text))
        {
            FinishReason = ChatFinishReason.Stop
        });

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

// -----------------------------------------------------------------------
// <copyright file="HeadlessTurnErrorCategoryEndToEndTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Akka.Hosting;
using Akka.Persistence.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Netclaw.Daemon;
using Netclaw.Daemon.Gateway;
using Netclaw.Daemon.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

/// <summary>
/// Drives <c>netclaw chat -p</c> into each way a turn can fail. The model is a script. The session
/// actor, the SignalR hub, the daemon client, and the headless channel are the real ones. The tests
/// assert what a shell script sees. The contract is in <see cref="HeadlessTurnErrorCategoryTests"/>.
/// </summary>
/// <remarks>
/// The eval case <c>tool_cycle_terminal</c> needs the "ignored" rows to report <c>model</c>. Before this
/// contract the product reported <c>ErrorCategory.ProviderFailure</c> for them, the same value as a
/// provider outage.
/// </remarks>
[Collection(HeadlessProcessStateCollection.Name)]
public sealed class HeadlessTurnErrorCategoryEndToEndTests : IAsyncDisposable
{
    private readonly DisposableTempDir _directory = new();
    private WebApplication? _app;
    private DaemonClient? _client;

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
        if (_app is not null)
            await _app.DisposeAsync();
        _directory.Dispose();
    }

    // Model failures. The runtime stops the tool work. The model keeps asking for the same tools.
    // Planted bug: the product reports ProviderFailure for these, so the harness sees infra.

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tool_call_limit_that_the_model_ignores_is_a_model_failure(bool json)
    {
        // Each call differs, so the cycle guard stays quiet. The limit of 3 forces the text-only turn.
        var model = new ScriptedModel(call => ToolCall(call, query: $"query-{call.Number}"));

        var run = await RunAsync(model, json, maxToolIterations: 3);

        AssertFailed(run, json, "model", "ModelBehavior");
        Assert.Contains("required text only", run.Stderr, StringComparison.Ordinal);
        Assert.Equal(4, model.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cycle_stop_that_the_model_ignores_is_a_model_failure(bool json)
    {
        // The eval case tool_cycle_terminal. The same call repeats, so the runtime corrects once and
        // then stops the cycle. The limit of 20 is far away, so only the cycle stop ends the turn.
        var model = new ScriptedModel(call => ToolCall(call, query: "same"));

        var run = await RunAsync(model, json, maxToolIterations: 20);

        AssertFailed(run, json, "model", "ModelBehavior");
        Assert.Contains("Netclaw stopped this run", run.Stderr, StringComparison.Ordinal);
        // Two executed rounds, one corrected round, and the repeat that ends the turn. The runtime asks
        // the model nothing after the repeat.
        Assert.Equal(4, model.CallCount);
    }

    // Repeated empty replies count against the model only when the stream finished on its own terms:
    // finish reason Stop or Length. Planted bug: the finish reason is ignored, so a broken stream blames
    // the model.

    [Theory]
    [InlineData("stop", false)]
    [InlineData("stop", true)]
    [InlineData("length", false)]
    [InlineData("length", true)]
    public async Task Repeated_empty_replies_before_any_tool_are_a_model_failure(string finishReason, bool json)
    {
        var model = new ScriptedModel(_ => [new TextReasoningContent("still thinking")], new ChatFinishReason(finishReason));

        var run = await RunAsync(model, json);

        AssertFailed(run, json, "model", "ModelBehavior");
    }

    [Theory]
    [InlineData("stop", false)]
    [InlineData("stop", true)]
    [InlineData("length", false)]
    [InlineData("length", true)]
    public async Task Repeated_empty_replies_after_a_tool_are_a_model_failure(string finishReason, bool json)
    {
        var model = new ScriptedModel(
            call => call.Number == 1 ? ToolCall(call, query: "once") : [],
            new ChatFinishReason(finishReason));

        var run = await RunAsync(model, json);

        AssertFailed(run, json, "model", "ModelBehavior");
    }

    // The stream ended with no finish reason: EOF, or a proxy cut the connection. The backend failed. The
    // model did not get a fair turn, so the failure is infra. The same stream fault feeds the thinking-only
    // and the empty case.

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repeated_empty_replies_without_a_finish_reason_are_an_infra_failure(bool json)
    {
        var model = new ScriptedModel(_ => []);

        var run = await RunAsync(model, json);

        AssertFailed(run, json, "infra", "StreamFailure");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repeated_thinking_only_replies_without_a_finish_reason_are_an_infra_failure(bool json)
    {
        var model = new ScriptedModel(_ => [new TextReasoningContent("still thinking")]);

        var run = await RunAsync(model, json);

        AssertFailed(run, json, "infra", "StreamFailure");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repeated_empty_replies_after_a_tool_without_a_finish_reason_are_an_infra_failure(bool json)
    {
        var model = new ScriptedModel(call => call.Number == 1 ? ToolCall(call, query: "once") : []);

        var run = await RunAsync(model, json);

        AssertFailed(run, json, "infra", "StreamFailure");
    }

    // A content filter blocked the reply. The provider applied its own policy. That is not evidence about
    // the model. The kind is the implementer's choice, so the test pins the category only.

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repeated_empty_replies_from_a_content_filter_are_an_infra_failure(bool json)
    {
        var model = new ScriptedModel(_ => [], ChatFinishReason.ContentFilter);

        var run = await RunAsync(model, json);

        AssertFailed(run, json, "infra", kind: null);
    }

    // Infrastructure failures. Planted bug: the category follows the loudest word in the message, or it
    // is model for every failed turn.

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provider_503_is_an_infra_failure(bool json)
    {
        var model = new ScriptedModel(_ => throw new HttpRequestException(
            "Service Unavailable", null, HttpStatusCode.ServiceUnavailable));

        var run = await RunAsync(model, json);

        AssertFailed(run, json, "infra", "ProviderFailure");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provider_connection_refused_is_an_infra_failure(bool json)
    {
        var model = new ScriptedModel(_ => throw new HttpRequestException(
            "Connection refused", new SocketException((int)SocketError.ConnectionRefused)));

        var run = await RunAsync(model, json);

        AssertFailed(run, json, "infra", "ProviderFailure");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Model_call_timeout_is_an_infra_failure(bool json)
    {
        var model = new ScriptedModel(_ => throw new TimeoutException("The model call timed out."));

        var run = await RunAsync(model, json);

        AssertFailed(run, json, "infra", "Timeout");
    }

    // Controls. The model obeys, or it recovers, and the turn completes. Planted bug: a runtime stop
    // marks the run as failed even when the model stops calling tools.

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cycle_stop_that_the_model_obeys_is_not_a_failure(bool json)
    {
        // The model obeys the first runtime intervention that it can see: the tools are gone, or the
        // runtime replaced the result of its last call.
        var model = new ScriptedModel(call => call.ToolsOffered && !call.RuntimeIntervened
            ? ToolCall(call, query: "same")
            : Text("Done."));

        var run = await RunAsync(model, json, maxToolIterations: 20);

        AssertSucceeded(run, json);
        // Proof that the run reached the cycle stop: two executed rounds, one corrected round, and the answer.
        Assert.Equal(4, model.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tool_call_limit_that_the_model_obeys_is_not_a_failure(bool json)
    {
        var model = new ScriptedModel(call => call.ToolsOffered ? ToolCall(call, query: $"query-{call.Number}") : Text("Done."));

        var run = await RunAsync(model, json, maxToolIterations: 3);

        AssertSucceeded(run, json);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task One_empty_reply_then_an_answer_is_not_a_failure(bool json)
    {
        var model = new ScriptedModel(call => call.Number == 1 ? [] : Text("Done."));

        var run = await RunAsync(model, json);

        AssertSucceeded(run, json);
    }

    // A null kind means the test pins the category only.
    private static void AssertFailed(HeadlessRun run, bool json, string category, string? kind)
    {
        Assert.Equal(1, run.ExitCode);
        var tokenLine = Assert.Single(run.TurnFailedLines);
        Assert.StartsWith($"[turn:failed] category={category} kind=", tokenLine, StringComparison.Ordinal);
        Assert.DoesNotContain("[turn:failed]", run.Stdout, StringComparison.Ordinal);
        var logRecord = Assert.Single(run.TurnFailedLogRecords);
        Assert.Contains($"TURN_FAILED: category={category} kind=", logRecord, StringComparison.Ordinal);

        if (kind is not null)
        {
            Assert.Equal($"[turn:failed] category={category} kind={kind}", tokenLine);
            Assert.EndsWith($"TURN_FAILED: category={category} kind={kind}", logRecord, StringComparison.Ordinal);
        }

        if (!json)
            return;

        using var envelope = run.Envelope;
        Assert.NotNull(envelope);
        Assert.Equal(category, envelope.RootElement.GetProperty("errorCategory").GetString());
        var envelopeKind = envelope.RootElement.GetProperty("errorKind").GetString();
        Assert.Equal(tokenLine.Split("kind=")[1], envelopeKind);
        if (kind is not null)
            Assert.Equal(kind, envelopeKind);
    }

    private static void AssertSucceeded(HeadlessRun run, bool json)
    {
        Assert.Equal(0, run.ExitCode);
        Assert.Empty(run.TurnFailedLines);
        Assert.Empty(run.TurnFailedLogRecords);
        Assert.DoesNotContain("[error]", run.Stderr, StringComparison.Ordinal);

        if (!json)
            return;

        using var envelope = run.Envelope;
        Assert.NotNull(envelope);
        Assert.Equal("Done.", envelope.RootElement.GetProperty("response").GetString());
        Assert.False(envelope.RootElement.TryGetProperty("errorCategory", out _));
        Assert.False(envelope.RootElement.TryGetProperty("errorKind", out _));
    }

    private static IReadOnlyList<AIContent> Text(string text) => [new TextContent(text)];

    // Call ids are unique, as a real provider makes them. Arguments decide whether the cycle guard
    // sees a repeat.
    private static IReadOnlyList<AIContent> ToolCall(ModelCall call, string query) =>
    [
        new FunctionCallContent(
            $"call-{call.Number}",
            "web_search",
            new Dictionary<string, object?> { ["query"] = query })
    ];

    private async Task<HeadlessRun> RunAsync(ScriptedModel model, bool json, int maxToolIterations = 25)
    {
        var paths = new NetclawPaths(_directory.Path);
        paths.EnsureDirectoriesExist();
        var registry = new ToolRegistry();
        registry.RegisterCore(AIFunctionFactory.Create(() => ScriptedModel.FixedToolResult, "web_search"), "web_search");
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
        builder.Services.AddSingleton<ISessionStorageResolver>(storage);
        builder.Services.AddSingleton(new SessionConfig
        {
            MaxToolIterationsPerTurn = maxToolIterations,
            Tuning = new SessionTuning { SnapshotInterval = 5, TitleGenerationInterval = 0 }
        });
        builder.Services.AddSingleton(new ModelCapabilities { ModelId = "scripted-model", ContextWindowTokens = 128_000 });
        builder.Services.AddSingleton(new SessionServices(
            new SingleClientProvider(model),
            new StaticSystemPromptProvider("You are a test assistant with tools."),
            [],
            new FixedWorkingContext(),
            TimeProvider.System,
            paths,
            storage));
        builder.Services.AddSingleton(new SessionToolServices(new FixedToolExecutor(), registry, null, null, null));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ClaimsPrincipalMapper>();
        builder.Services.AddSingleton<SessionIngressGate>();
        builder.Services.AddSingleton<SessionRegistry>();
        builder.Services.AddSingleton<SessionPipeline>();
        builder.Services.AddSingleton<ISessionPipeline>(sp => sp.GetRequiredService<SessionPipeline>());
        builder.Services.AddAkka("headless-error-category", akka => akka
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
        return await HeadlessRunHarness.RunAsync(_client, _directory, json, TestContext.Current.CancellationToken);
    }

    private sealed class FixedToolExecutor : IToolExecutor
    {
        public Task<string> ExecuteAsync(
            FunctionCallContent toolCall, ToolExecutionContext context, CancellationToken ct = default)
            => Task.FromResult(ScriptedModel.FixedToolResult);
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
}

/// <summary>
/// One call to the scripted model. <see cref="RuntimeIntervened"/> is true when the last tool result
/// is not the text that the fixed tool returns, so the runtime replaced it (for example, a cycle correction).
/// </summary>
internal sealed record ModelCall(int Number, bool ToolsOffered, bool RuntimeIntervened);

/// <summary>
/// A model that follows a script. It ignores the tool list unless the script reads
/// <see cref="ModelCall.ToolsOffered"/>. The script throws to fail the call. The throw is synchronous,
/// so the failure reaches the session actor before the send is acknowledged. Every reply carries
/// <c>finishReason</c>. A null value models a stream that ended with no finish reason.
/// </summary>
internal sealed class ScriptedModel(
    Func<ModelCall, IReadOnlyList<AIContent>> script,
    ChatFinishReason? finishReason = null) : IChatClient
{
    /// <summary>The text that the fixed test tool returns.</summary>
    public const string FixedToolResult = "search result";

    private int _callCount;

    public int CallCount => Volatile.Read(ref _callCount);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(Next(messages, options));

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => Replay(Next(messages, options));

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private ChatResponse Next(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var lastToolResult = messages
            .SelectMany(message => message.Contents.OfType<FunctionResultContent>())
            .LastOrDefault();
        var call = new ModelCall(
            Interlocked.Increment(ref _callCount),
            options?.Tools is { Count: > 0 },
            lastToolResult is not null && lastToolResult.Result?.ToString() != FixedToolResult);
        return new ChatResponse(new ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, script(call).ToList()))
        {
            FinishReason = finishReason
        };
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> Replay(ChatResponse response)
    {
        foreach (var update in response.ToChatResponseUpdates())
            yield return update;
        await Task.CompletedTask;
    }
}

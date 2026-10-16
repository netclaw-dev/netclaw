// -----------------------------------------------------------------------
// <copyright file="HeadlessTurnErrorCategoryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using Netclaw.Actors.Protocol;
using Netclaw.Cli.Daemon;
using Netclaw.Tests.Utilities;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Cli.Tests.Cli;

/// <summary>
/// Pins the error category of a failed <c>netclaw chat -p</c> run, so that the eval harness can tell
/// a model failure from an infrastructure failure. The consumer is a shell script that reads stdout,
/// stderr, the exit code, and the session log. The contract:
/// <list type="bullet">
/// <item>A failed run exits 1. A run that did not fail exits 0 and prints no category.</item>
/// <item>Stderr holds exactly one line <c>[turn:failed] category=&lt;model|infra&gt; kind=&lt;Kind&gt;</c>.
/// The existing <c>[error]</c> line stays.</item>
/// <item>With <c>--json</c>, the envelope on stdout adds <c>errorCategory</c> and <c>errorKind</c> for a
/// failed turn. A turn that did not fail has neither key.</item>
/// <item>The session log holds one <c>TURN_FAILED: category=... kind=...</c> record before
/// <c>TURN_COMPLETED</c>.</item>
/// <item><c>kind</c> is the <c>ErrorCategory</c> name that the failing <c>ErrorOutput</c> carries.
/// Only <c>ModelBehavior</c> maps to <c>model</c>. Every other kind, and every failure that has no
/// <c>ErrorOutput</c>, maps to <c>infra</c>. A harness must never score an unknown failure as a model
/// failure.</item>
/// <item>The failing <c>ErrorOutput</c> is the last one before the failed <c>TurnCompleted</c>. An earlier
/// error that the turn survived does not decide the category.</item>
/// <item>Every terminal path sets the exit code first, then stops the host.</item>
/// </list>
/// These tests feed the channel through the daemon transport seam. The end-to-end test class drives the
/// real session actor into each path.
/// </summary>
[Collection(HeadlessProcessStateCollection.Name)]
public sealed class HeadlessTurnErrorCategoryTests : IAsyncDisposable
{
    private const string SessionId = "fake/session";

    private readonly DisposableTempDir _directory = new();
    private readonly FakeDaemonHubTransport _transport = new();
    private readonly DaemonClient _client;

    public HeadlessTurnErrorCategoryTests() => _client = new("http://127.0.0.1:1", _transport);

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        _directory.Dispose();
    }

    public static TheoryData<string, string, bool> KindCases()
    {
        // Planted bugs, one per row: a model failure reported as infra (ModelBehavior), and an infra
        // failure reported as model (every other kind, e.g. a provider 503 as ProviderFailure).
        var data = new TheoryData<string, string, bool>();
        foreach (var json in new[] { false, true })
        {
            data.Add("ModelBehavior", "model", json);
            data.Add("ProviderFailure", "infra", json);
            data.Add("StreamFailure", "infra", json);
            data.Add("Timeout", "infra", json);
            data.Add("ToolFailure", "infra", json);
            data.Add("InputCompatibility", "infra", json);
            data.Add("Unknown", "infra", json);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(KindCases))]
    public async Task Failed_turn_reports_the_category_of_its_error(string kind, string category, bool json)
    {
        Respond(Error(kind, "The turn failed."), Completed(TurnOutcome.Failed));

        var run = await HeadlessRunHarness.RunAsync(_client, _directory, json, TestContext.Current.CancellationToken);

        Assert.Equal(1, run.ExitCode);
        Assert.Equal([$"[turn:failed] category={category} kind={kind}"], run.TurnFailedLines);
        // The category is additive. The message line stays for people and for older parsers.
        Assert.Contains("[error] The turn failed.", run.Stderr, StringComparison.Ordinal);
        // Stdout is the response. A token there would corrupt the response and the JSON document.
        Assert.DoesNotContain("[turn:failed]", run.Stdout, StringComparison.Ordinal);
        var record = Assert.Single(run.TurnFailedLogRecords);
        Assert.EndsWith($"TURN_FAILED: category={category} kind={kind}", record, StringComparison.Ordinal);
        Assert.True(
            run.SessionLog.IndexOf("TURN_FAILED:", StringComparison.Ordinal)
            < run.SessionLog.IndexOf("TURN_COMPLETED:", StringComparison.Ordinal));

        if (json)
        {
            using var envelope = run.Envelope;
            Assert.NotNull(envelope);
            Assert.Equal(category, envelope.RootElement.GetProperty("errorCategory").GetString());
            Assert.Equal(kind, envelope.RootElement.GetProperty("errorKind").GetString());
        }
    }

    [Theory]
    [InlineData("Timeout", "ModelBehavior", "model")]
    [InlineData("ProviderFailure", "ModelBehavior", "model")]
    [InlineData("ModelBehavior", "Timeout", "infra")]
    [InlineData("ModelBehavior", "ProviderFailure", "infra")]
    public async Task The_error_that_failed_the_turn_decides_not_an_earlier_error(
        string earlierKind, string failingKind, string category)
    {
        // Planted bugs: the first error wins, and a model error anywhere in the turn wins.
        Respond(
            Error(earlierKind, "An error that the turn survived."),
            Error(failingKind, "The error that failed the turn."),
            Completed(TurnOutcome.Failed));

        var run = await HeadlessRunHarness.RunAsync(
            _client, _directory, json: false, TestContext.Current.CancellationToken);

        Assert.Equal([$"[turn:failed] category={category} kind={failingKind}"], run.TurnFailedLines);
        Assert.Equal(1, run.ExitCode);
    }

    [Fact]
    public async Task Failed_turn_without_an_error_is_infra_and_never_model()
    {
        // Planted bug: a failure that nobody classified is scored as a model failure, or prints no category.
        Respond(Completed(TurnOutcome.Failed));

        var run = await HeadlessRunHarness.RunAsync(
            _client, _directory, json: false, TestContext.Current.CancellationToken);

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(["[turn:failed] category=infra kind=Unknown"], run.TurnFailedLines);
    }

    [Theory]
    [InlineData("Timeout", false)]
    [InlineData("Timeout", true)]
    [InlineData("ProviderFailure", false)]
    [InlineData("ProviderFailure", true)]
    [InlineData("ModelBehavior", false)]
    [InlineData("ModelBehavior", true)]
    public async Task Turn_that_completed_has_no_category_even_after_an_error(string kind, bool json)
    {
        // Planted bug: any ErrorOutput marks the run as failed. A compaction timeout and a context
        // overflow recovery both emit an ErrorOutput, and the turn still completes.
        Respond(Error(kind, "The session continues."), Completed(TurnOutcome.Completed));

        var run = await HeadlessRunHarness.RunAsync(_client, _directory, json, TestContext.Current.CancellationToken);

        Assert.Equal(0, run.ExitCode);
        Assert.Empty(run.TurnFailedLines);
        Assert.Empty(run.TurnFailedLogRecords);
        if (json)
        {
            using var envelope = run.Envelope;
            Assert.NotNull(envelope);
            Assert.False(envelope.RootElement.TryGetProperty("errorCategory", out _));
            Assert.False(envelope.RootElement.TryGetProperty("errorKind", out _));
        }
    }

    [Fact]
    public async Task Connection_failure_is_infra_and_exits_1()
    {
        // Planted bug: a channel failure prints no category, or exits 0.
        _transport.StartHook = _ => Task.FromException(
            new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized));

        var run = await HeadlessRunHarness.RunAsync(
            _client, _directory, json: true, TestContext.Current.CancellationToken);

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(["[turn:failed] category=infra kind=ChannelFailure"], run.TurnFailedLines);
        Assert.Contains("[headless:error]", run.Stderr, StringComparison.Ordinal);
        Assert.Equal(string.Empty, run.Stdout);
        Assert.Equal(0, _transport.EnsureSessionCalls);
    }

    [Fact]
    public async Task Over_limit_prompt_is_infra_and_does_not_connect()
    {
        var run = await HeadlessRunHarness.RunAsync(
            _client, _directory, json: false, TestContext.Current.CancellationToken, prompt: new string('a', 600_000));

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(["[turn:failed] category=infra kind=ChannelFailure"], run.TurnFailedLines);
        Assert.Contains("Message is too large to send", run.Stderr, StringComparison.Ordinal);
        Assert.Equal(0, _transport.StartAttempts);
    }

    [Fact]
    public async Task Cancelled_run_is_infra_and_exits_1()
    {
        // Planted bug: a cancelled run exits 0 with no output, so a script reads it as a pass. The run
        // must also stop the host, because the cancel path has no other end signal.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var run = await HeadlessRunHarness.RunAsync(_client, _directory, json: false, cancelled.Token);

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(["[turn:failed] category=infra kind=Cancelled"], run.TurnFailedLines);
        Assert.Contains("CANCELLED", run.FailureLog, StringComparison.Ordinal);
    }

    private void Respond(params SessionOutputDto[] outputs)
        => _transport.VoidInvokeHook = (method, _, _) =>
        {
            if (method == "SendMessage")
            {
                foreach (var output in outputs)
                    _transport.PushOutput(output);
            }

            return Task.CompletedTask;
        };

    // The kind travels on the wire as a name. A name that this build does not know must still be a valid
    // input, so the DTO carries the string and not the enum.
    private static SessionOutputDto Error(string kind, string message)
        => SessionOutputDtoMapper.ToDto(new ErrorOutput
        {
            SessionId = new SessionId(SessionId),
            Message = message,
            Category = ErrorCategory.Unknown
        }) with
        {
            ErrorCategory = kind
        };

    private static SessionOutputDto Completed(TurnOutcome outcome)
        => SessionOutputDtoMapper.ToDto(new TurnCompleted
        {
            SessionId = new SessionId(SessionId),
            TurnNumber = new TurnNumber(1),
            Outcome = outcome
        });
}

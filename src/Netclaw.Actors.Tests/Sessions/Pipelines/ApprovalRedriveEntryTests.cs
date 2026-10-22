// -----------------------------------------------------------------------
// <copyright file="ApprovalRedriveEntryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Netclaw.Tools.Authorization.Consent;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions.Pipelines;

/// <summary>
/// durable-child-consent-wait, task 2.2 (M11). A characterization of how the session pipeline applies one entry of
/// an approval re-drive plan to a call, before the move to the shared plan-entry helper. The parent pipeline and,
/// from PR 1a, a resumed child use that helper. An approved entry seeds the one-time consent for that call only. A
/// refused entry never dispatches and returns the text of its refusal kind. Only a denial adds the managed temporary
/// hint. Each entry keeps the recorded authorization attempt ID.
/// </summary>
public sealed class ApprovalRedriveEntryTests(ITestOutputHelper output) : TestKit(output: output)
{
    private const string DeniedText =
        "Tool access denied: approval_denied_by_user (shell_execute requires interactive approval and the user declined it). "
        + "The user declined this action. Do not reach the same goal by another tool or route. "
        + "Tell the user what you wanted to do, and ask how to continue.";

    private const string ManagedTemporaryHint =
        "Hint: Use the managed temporary directory '/home/user/.netclaw/sessions/example/tmp' for disposable artifacts. "
        + "The shared platform temporary root is not a trusted root for this session.";

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
    }

    [Theory]
    [InlineData("Denied", true, DeniedText + "\n" + ManagedTemporaryHint)]
    [InlineData("Denied", false, DeniedText)]
    [InlineData("TimedOut", true, "Tool access denied: approval_timed_out")]
    [InlineData("PromptUnavailable", true,
        "Tool access denied: approval_prompt_unavailable "
        + "(Netclaw could not show the approval prompt in this channel. The call did not run.)")]
    public async Task A_refused_entry_returns_the_text_of_its_kind_and_never_dispatches(
        string kind,
        bool hasDenialDirectory,
        string expectedText)
    {
        // Arrange
        var executor = new RecordingExecutor();
        var attemptId = AuthorizationAttemptId.New();
        var call = new FunctionCallContent("call-refused", "shell_execute",
            new Dictionary<string, object?> { ["command"] = "rm -rf /tmp/build" });
        var denialDirectories = hasDenialDirectory
            ? new Dictionary<string, string> { ["call-refused"] = "/home/user/.netclaw/sessions/example/tmp" }
            : new Dictionary<string, string>();

        // Act
        var result = Assert.Single(await RunAsync(executor, [call], fixture => fixture.RedrivingApprovals(
            new Dictionary<string, IReadOnlyList<string>>(),
            new Dictionary<string, RefusalKind> { ["call-refused"] = Enum.Parse<RefusalKind>(kind) },
            denialDirectories,
            new Dictionary<string, AuthorizationAttemptId> { ["call-refused"] = attemptId })));

        // Assert
        Assert.Empty(executor.Calls);
        Assert.Equal(expectedText, result.Message.Content);
        Assert.Equal(new ToolCallId("call-refused"), result.Message.ToolCallId);
        Assert.Equal(SessionProtocol.ToolResultOutput.AccessDeniedFailureCode, result.FailureCode);
        Assert.IsType<ToolInvocationReceipt.AuthorizationDenied>(result.Receipt);
        Assert.Equal(attemptId, result.AuthorizationAttemptId);
    }

    [Fact]
    public async Task An_approved_entry_seeds_the_one_time_consent_for_its_call_only()
    {
        // Arrange: two calls of one tool. Only the first call has an approved entry.
        var executor = new RecordingExecutor();
        var attemptId = AuthorizationAttemptId.New();
        FunctionCallContent[] calls =
        [
            new("call-approved", "sample_tool", new Dictionary<string, object?> { ["path"] = "a" }),
            new("call-sibling", "sample_tool", new Dictionary<string, object?> { ["path"] = "b" })
        ];

        // Act
        var results = await RunAsync(executor, calls, fixture => fixture.RedrivingApprovals(
            new Dictionary<string, IReadOnlyList<string>> { ["call-approved"] = ["key-1", "key-2"] },
            new Dictionary<string, RefusalKind>(),
            new Dictionary<string, string>(),
            new Dictionary<string, AuthorizationAttemptId> { ["call-approved"] = attemptId }));

        // Assert: each call ran once. The consent names the tool of the call and the recorded keys.
        Assert.Equal(2, results.Count);
        Assert.Equal(["call-approved", "call-sibling"], executor.Calls.Keys.Order(StringComparer.Ordinal));
        var approved = executor.Calls["call-approved"];
        Assert.Equal(new OneTimeConsent("sample_tool", ["key-1", "key-2"]), approved.Consent);
        Assert.Equal(attemptId, approved.AttemptId);
        var sibling = executor.Calls["call-sibling"];
        Assert.Null(sibling.Consent);
        Assert.NotEqual(attemptId, sibling.AttemptId);
    }

    private async Task<IReadOnlyList<ToolCallResult>> RunAsync(
        IToolExecutor executor,
        IReadOnlyList<FunctionCallContent> calls,
        Func<SessionToolPipelineTestFixture, SessionToolPipelineTestFixture> redrive)
    {
        var probe = CreateTestProbe();
        var sessionId = new SessionId("D1/approval-redrive-entry");
        var fixture = redrive(new SessionToolPipelineTestFixture(executor, calls, sessionId, probe.Ref)
            .WithTurnContext(InteractiveTurnContext(sessionId)));

        var pipelineTask = fixture.ExecuteAsync(TestContext.Current.CancellationToken);
        var results = await StreamedToolReplyProbe.ExpectAsync(
            probe, resultCount: calls.Count, cancellationToken: TestContext.Current.CancellationToken);
        await pipelineTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        return results;
    }

    private static TurnContext InteractiveTurnContext(SessionId sessionId) => new()
    {
        SessionId = sessionId,
        TurnId = new TurnId("test-turn"),
        Audience = TrustAudience.Personal,
        Boundary = TrustBoundary.Personal,
        ChannelType = ChannelType.SignalR,
        RequesterSenderId = new SenderId("local-user"),
        RequesterPrincipal = PrincipalClassification.Operator,
        Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted),
        SupportsInteractiveApproval = true
    };

    // Records the consent and the attempt ID that each dispatched call carries into the executor.
    private sealed class RecordingExecutor : IToolExecutor
    {
        public ConcurrentDictionary<string, (OneTimeConsent? Consent, AuthorizationAttemptId AttemptId)> Calls { get; }
            = new(StringComparer.Ordinal);

        public Task AuthorizeAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<string> ExecuteAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            Assert.True(Calls.TryAdd(toolCall.CallId, (context.Approval.OneTimeConsent, context.Approval.AuthorizationAttemptId)),
                $"Call {toolCall.CallId} ran twice.");
            return Task.FromResult($"executed:{toolCall.CallId}");
        }
    }
}

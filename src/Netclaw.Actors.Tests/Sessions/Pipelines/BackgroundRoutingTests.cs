// -----------------------------------------------------------------------
// <copyright file="BackgroundRoutingTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Jobs.BackgroundJobProtocol;

namespace Netclaw.Actors.Tests.Sessions.Pipelines;

public sealed class BackgroundRoutingTests(ITestOutputHelper output) : TestKit(output: output)
{
    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider) { }

    [Fact]
    public async Task TimeoutAlone_DoesNotRouteToBackground()
    {
        var executor = new EchoExecutor();
        var probe = CreateTestProbe("pipeline-probe");
        var jobManagerProbe = CreateTestProbe("job-manager");

        var toolCalls = new List<FunctionCallContent>
        {
            new("call-1", "shell_execute", new Dictionary<string, object?>
            {
                ["command"] = "echo test",
                ["_timeout_seconds"] = 30,
                ["_rationale"] = "test timeout alone"
            })
        };

        await new SessionToolPipelineTestFixture(
                executor, toolCalls, new SessionId("test/timeout-only"), probe.Ref)
            .WithBackgroundJobs(jobManagerProbe.Ref)
            .ExecuteAsync(TestContext.Current.CancellationToken);

        var completed = await probe.ExpectMsgAsync<ToolExecutionCompleted>(
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(completed.ToolResults);
        Assert.Equal("echo:echo test", completed.ToolResults[0].Content);

        await jobManagerProbe.ExpectNoMsgAsync(
            TimeSpan.FromMilliseconds(200),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExplicitBackground_RoutesShellToBackgroundManager()
    {
        var executor = new EchoExecutor();
        var probe = CreateTestProbe("pipeline-probe");
        var jobManagerProbe = CreateTestProbe("job-manager");

        var fakeJobManager = Sys.ActorOf(Props.Create(() =>
            new FakeJobManager(jobManagerProbe.Ref)));

        var toolCalls = new List<FunctionCallContent>
        {
            new("call-bg", "shell_execute", new Dictionary<string, object?>
            {
                ["command"] = "sleep 600",
                ["_background"] = true,
                ["_rationale"] = "long running build"
            })
        };

        await new SessionToolPipelineTestFixture(
                executor, toolCalls, new SessionId("test/background"), probe.Ref)
            .From(TestMessageSource())
            .WithBackgroundJobs(fakeJobManager)
            .ExecuteAsync(TestContext.Current.CancellationToken);

        var completed = await probe.ExpectMsgAsync<ToolExecutionCompleted>(
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(completed.ToolResults);
        Assert.Contains("Background job", completed.ToolResults[0].Content);
        Assert.Contains("check_background_job", completed.ToolResults[0].Content);

        var received = await jobManagerProbe.ExpectMsgAsync<StartBackgroundJob>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("sleep 600", received.Command);
        Assert.Equal("long running build", received.Rationale);
    }

    [Fact]
    public async Task ExplicitBackground_HonorsRequestedTimeout()
    {
        // The agent's requested timeout is honored on the background path too —
        // it is not clamped to a ceiling (the agent owns that judgement).
        var executor = new EchoExecutor();
        var probe = CreateTestProbe("pipeline-probe-bg-timeout");
        var jobManagerProbe = CreateTestProbe("job-manager-bg-timeout");
        var fakeJobManager = Sys.ActorOf(Props.Create(() => new FakeJobManager(jobManagerProbe.Ref)));

        var toolCalls = new List<FunctionCallContent>
        {
            new("call-bg-timeout", "shell_execute", new Dictionary<string, object?>
            {
                ["command"] = "sleep 1200",
                ["_background"] = true,
                ["_timeout_seconds"] = 1800,
                ["_rationale"] = "long job"
            })
        };

        await new SessionToolPipelineTestFixture(
                executor, toolCalls, new SessionId("test/background-timeout"), probe.Ref)
            .From(TestMessageSource())
            .WithBackgroundJobs(fakeJobManager)
            .ExecuteAsync(TestContext.Current.CancellationToken);

        await probe.ExpectMsgAsync<ToolExecutionCompleted>(
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);

        var received = await jobManagerProbe.ExpectMsgAsync<StartBackgroundJob>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1800, received.TimeoutSeconds);
    }

    [Fact]
    public async Task ExplicitBackground_OmittedTimeout_ArmsNoKillTimer()
    {
        // A background job is a detached process with no completion expectation:
        // without an explicit _timeout_seconds the job gets NO kill timer
        // (TimeoutSeconds = 0) — the synchronous default must not be substituted.
        var executor = new EchoExecutor();
        var probe = CreateTestProbe("pipeline-probe-bg-notimer");
        var jobManagerProbe = CreateTestProbe("job-manager-bg-notimer");
        var fakeJobManager = Sys.ActorOf(Props.Create(() => new FakeJobManager(jobManagerProbe.Ref)));

        var toolCalls = new List<FunctionCallContent>
        {
            new("call-bg-notimer", "shell_execute", new Dictionary<string, object?>
            {
                ["command"] = "jekyll serve",
                ["_background"] = true,
                ["_rationale"] = "dev server"
            })
        };

        await new SessionToolPipelineTestFixture(
                executor, toolCalls, new SessionId("test/background-notimer"), probe.Ref)
            .From(TestMessageSource())
            .WithBackgroundJobs(fakeJobManager)
            .ExecuteAsync(TestContext.Current.CancellationToken);

        await probe.ExpectMsgAsync<ToolExecutionCompleted>(
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);

        var received = await jobManagerProbe.ExpectMsgAsync<StartBackgroundJob>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, received.TimeoutSeconds);
    }

    [Fact]
    public async Task ExplicitBackground_SubmitAckIncludesOutputLogPath()
    {
        var executor = new EchoExecutor();
        var probe = CreateTestProbe("pipeline-probe-bg-logpath");
        var jobManagerProbe = CreateTestProbe("job-manager-bg-logpath");
        var fakeJobManager = Sys.ActorOf(Props.Create(() => new FakeJobManager(jobManagerProbe.Ref)));

        var toolCalls = new List<FunctionCallContent>
        {
            new("call-bg-logpath", "shell_execute", new Dictionary<string, object?>
            {
                ["command"] = "npm run dev",
                ["_background"] = true,
                ["_rationale"] = "dev server"
            })
        };

        await new SessionToolPipelineTestFixture(
                executor, toolCalls, new SessionId("test/background-logpath"), probe.Ref)
            .From(TestMessageSource())
            .WithBackgroundJobs(fakeJobManager)
            .ExecuteAsync(TestContext.Current.CancellationToken);

        var completed = await probe.ExpectMsgAsync<ToolExecutionCompleted>(
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(completed.ToolResults);
        // The ACK steers the agent at the streaming log so it can monitor a
        // long-running job without an extra status round-trip.
        Assert.Contains("output.log", completed.ToolResults[0].Content);
    }

    [Fact]
    public async Task ExplicitBackground_PreservesWorkingDirectory()
    {
        var executor = new EchoExecutor();
        var probe = CreateTestProbe("pipeline-probe-workingdir");
        var jobManagerProbe = CreateTestProbe("job-manager-workingdir");
        var fakeJobManager = Sys.ActorOf(Props.Create(() => new FakeJobManager(jobManagerProbe.Ref)));

        var toolCalls = new List<FunctionCallContent>
        {
            new("call-bg-dir", "shell_execute", new Dictionary<string, object?>
            {
                ["command"] = "dotnet test",
                ["working_directory"] = "/tmp/project",
                ["_background"] = true,
                ["_rationale"] = "run tests in repo"
            })
        };

        await new SessionToolPipelineTestFixture(
                executor, toolCalls, new SessionId("test/background-dir"), probe.Ref)
            .From(TestMessageSource())
            .WithBackgroundJobs(fakeJobManager)
            .ExecuteAsync(TestContext.Current.CancellationToken);

        await probe.ExpectMsgAsync<ToolExecutionCompleted>(
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);

        var received = await jobManagerProbe.ExpectMsgAsync<StartBackgroundJob>(
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("/tmp/project", received.WorkingDirectory);
    }

    [Fact]
    public async Task ExplicitBackground_DeniedByAuthorization_DoesNotRouteToBackground()
    {
        var executor = new DenyingExecutor();
        var probe = CreateTestProbe("pipeline-probe-denied");
        var jobManagerProbe = CreateTestProbe("job-manager-denied");

        var toolCalls = new List<FunctionCallContent>
        {
            new("call-bg-denied", "shell_execute", new Dictionary<string, object?>
            {
                ["command"] = "rm -rf /",
                ["_background"] = true,
                ["_rationale"] = "definitely should fail"
            })
        };

        await new SessionToolPipelineTestFixture(
                executor, toolCalls, new SessionId("test/background-denied"), probe.Ref)
            .WithBackgroundJobs(jobManagerProbe.Ref)
            .ExecuteAsync(TestContext.Current.CancellationToken);

        var completed = await probe.ExpectMsgAsync<ToolExecutionCompleted>(
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("Tool access denied", completed.ToolResults[0].Content);

        await jobManagerProbe.ExpectNoMsgAsync(
            TimeSpan.FromMilliseconds(200),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NonShellToolWithBackground_ExecutesSynchronously()
    {
        var executor = new EchoExecutor();
        var probe = CreateTestProbe("pipeline-probe");
        var jobManagerProbe = CreateTestProbe("job-manager");

        var toolCalls = new List<FunctionCallContent>
        {
            new("call-nonsync", "web_search", new Dictionary<string, object?>
            {
                ["query"] = "test query",
                ["_background"] = true,
                ["_rationale"] = "search in background"
            })
        };

        await new SessionToolPipelineTestFixture(
                executor, toolCalls, new SessionId("test/nonshell-bg"), probe.Ref)
            .WithBackgroundJobs(jobManagerProbe.Ref)
            .ExecuteAsync(TestContext.Current.CancellationToken);

        var completed = await probe.ExpectMsgAsync<ToolExecutionCompleted>(
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(completed.ToolResults);
        Assert.Equal("echo:test query", completed.ToolResults[0].Content);

        await jobManagerProbe.ExpectNoMsgAsync(
            TimeSpan.FromMilliseconds(200),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExplicitBackground_WithoutManager_ExecutesSynchronously()
    {
        var executor = new EchoExecutor();
        var probe = CreateTestProbe("pipeline-no-background-manager");
        var toolCalls = new List<FunctionCallContent>
        {
            new("call-bg-no-manager", "shell_execute", new Dictionary<string, object?>
            {
                ["command"] = "echo fallback",
                ["_background"] = true
            })
        };

        await new SessionToolPipelineTestFixture(
                executor, toolCalls, new SessionId("test/background-no-manager"), probe.Ref)
            .From(TestMessageSource())
            .ExecuteAsync(TestContext.Current.CancellationToken);

        var completed = await probe.ExpectMsgAsync<ToolExecutionCompleted>(
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("echo:echo fallback", Assert.Single(completed.ToolResults).Content);
    }

    [Fact]
    public async Task BackgroundManagerFailure_ReturnsSubmissionErrorWithoutSynchronousRetry()
    {
        var executor = new EchoExecutor();
        var probe = CreateTestProbe("pipeline-failing-background-manager");
        var manager = Sys.ActorOf(Props.Create(() => new FailingJobManager()));
        var toolCalls = new List<FunctionCallContent>
        {
            new("call-bg-failure", "shell_execute", new Dictionary<string, object?>
            {
                ["command"] = "long-running-command",
                ["_background"] = true
            })
        };

        await new SessionToolPipelineTestFixture(
                executor, toolCalls, new SessionId("test/background-failure"), probe.Ref)
            .From(TestMessageSource())
            .WithBackgroundJobs(manager)
            .ExecuteAsync(TestContext.Current.CancellationToken);

        var completed = await probe.ExpectMsgAsync<ToolExecutionCompleted>(
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("Error submitting background job", Assert.Single(completed.ToolResults).Content);
    }

    // Background-job submission now requires a trust context — source cannot be null.
    // This factory produces a minimal Personal-audience source for tests that route
    // to the background job manager and don't need to assert on trust-context values.
    private static MessageSource TestMessageSource() => new()
    {
        ChannelType = ChannelType.Tui,
        SenderId = new SenderId("test-user"),
        Audience = TrustAudience.Personal,
        Boundary = TrustBoundary.Personal,
        Principal = PrincipalClassification.TrustedInternal,
        Provenance = new SourceProvenance(TransportAuthenticity.LocalProcess, PayloadTaint.Trusted)
    };

    private sealed class EchoExecutor : IToolExecutor
    {
        public Task AuthorizeAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<string> ExecuteAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
        {
            var firstArg = toolCall.Arguments?.Values.FirstOrDefault()?.ToString() ?? "no-args";
            return Task.FromResult($"echo:{firstArg}");
        }
    }

    private sealed class DenyingExecutor : IToolExecutor
    {
        public Task AuthorizeAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
            => Task.FromException(new ToolAccessDeniedException("shell_disabled"));

        public Task<string> ExecuteAsync(FunctionCallContent toolCall, ToolExecutionContext? context = null, CancellationToken ct = default)
            => Task.FromResult("should-not-run");
    }

    private sealed class FakeJobManager : ReceiveActor
    {
        private int _counter;

        public FakeJobManager(IActorRef probe)
        {
            Receive<StartBackgroundJob>(cmd =>
            {
                probe.Forward(cmd);
                _counter++;
                Sender.Tell(new BackgroundJobStarted(
                    new BackgroundJobId($"fake-{_counter:D4}"),
                    $"/tmp/jobs/fake-{_counter:D4}/output.log"));
            });
        }
    }

    private sealed class FailingJobManager : ReceiveActor
    {
        public FailingJobManager()
        {
            Receive<StartBackgroundJob>(_ =>
                Sender.Tell(new Status.Failure(new InvalidOperationException("dispatch failed"))));
        }
    }
}

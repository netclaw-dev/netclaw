// -----------------------------------------------------------------------
// <copyright file="ToolCallLogExportTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Akka.Actor;
using Akka.Event;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Tests.Utilities;
using OpenTelemetry;
using OpenTelemetry.Logs;
using Xunit;
using AkkaLogging = Akka.Event.Logging;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Netclaw.Daemon.Tests.Configuration;

/// <summary>
/// Issue #2589: the tool call args line must reach the OTLP log exporter with
/// its fields. The line goes from the Akka logging adapter to the
/// Microsoft.Extensions.Logging bridge (AddLoggerFactory) and then to the
/// OpenTelemetry logger. A capture processor takes the place of the network
/// exporter, so this test does not use the network.
/// </summary>
public sealed class ToolCallLogExportTests
{
    private const string InvokingPrefix = "Invoking tool [";
    private const string StartedPrefix = "Tool authorization attempt started";

    // The decided template. The 0.26.0 line had the first three fields, and
    // dashboards can depend on the Args attribute name.
    private const string ArgsTemplate =
        "Invoking tool [{ToolName}] (call={CallId}) args={Args} rationale={Rationale} " +
        "authorizationAttemptId={AuthorizationAttemptId} sessionId={SessionId}";

    [Fact]
    public async Task Tool_call_args_line_reaches_the_otel_exporter_with_its_fields()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new DisposableTempDir();
        var capture = new CapturingLogProcessor(InvokingPrefix);
        using var host = new HostBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(MelLogLevel.Information);
                // These options copy the log options in
                // TelemetryRegistrationExtensions.AddNetclawTelemetry. Keep them the same.
                logging.AddOpenTelemetry(options =>
                {
                    options.IncludeFormattedMessage = true;
                    options.IncludeScopes = true;
                    options.ParseStateValues = true;
                    options.AddProcessor(capture);
                });
            })
            .ConfigureServices(services => services.AddAkka(
                "otel-tool-args",
                (builder, _) => builder.WithNetclawActorLogging(MelLogLevel.Information)))
            .Build();
        await host.StartAsync(cancellationToken);
        try
        {
            var system = host.Services.GetRequiredService<ActorSystem>();
            var logger = AkkaLogging.GetLogger(system, typeof(SessionToolExecutionPipeline));
            var sessionId = new SessionId("D1/otel-tool-args");
            var call = new FunctionCallContent("call-otel", "shell_execute", new Dictionary<string, object?>
            {
                ["command"] = "ls -la /srv/app",
                ["_rationale"] = "List the app files for the export test."
            });

            await RunPipelineAsync(logger, sessionId, call, temp.Path);
            CapturedRecord invoked;
            try
            {
                invoked = await capture.Matched.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            catch (TimeoutException)
            {
                Assert.Fail(
                    $"The exporter got no record with \"{InvokingPrefix}\". It got: " +
                    string.Join(" | ", capture.All.Select(static record => record.Body)));
                throw;
            }


            // The Akka bridge passes the template and its values to the MEL logger.
            // With ParseStateValues, the exporter sends each named value as its own
            // attribute. FormattedMessage also holds the text, after an Akka prefix.
            var started = Assert.Single(capture.All, record =>
                (record.FormattedMessage ?? string.Empty).Contains(StartedPrefix, StringComparison.Ordinal));
            Assert.Contains("ls -la /srv/app", invoked.Attributes["Args"], StringComparison.Ordinal);
            Assert.Equal(
                "List the app files for the export test.",
                invoked.Attributes["Rationale"]);
            Assert.StartsWith("auth-", invoked.Attributes["AuthorizationAttemptId"], StringComparison.Ordinal);
            Assert.Equal(
                started.Attributes["AuthorizationAttemptId"],
                invoked.Attributes["AuthorizationAttemptId"]);
            Assert.Equal(sessionId.Value, invoked.Attributes["SessionId"]);
            Assert.Equal("shell_execute", invoked.Attributes["ToolName"]);
            Assert.Equal("call-otel", invoked.Attributes["CallId"]);
            Assert.Equal(ArgsTemplate, invoked.Attributes["{OriginalFormat}"]);
            Assert.Contains("args={", invoked.FormattedMessage, StringComparison.Ordinal);
        }
        finally
        {
            await host.StopAsync(cancellationToken);
        }
    }

    private static async Task RunPipelineAsync(
        ILoggingAdapter logger,
        SessionId sessionId,
        FunctionCallContent call,
        string sessionDirectory)
    {
        var turnContext = TurnContext.FromMessageSource(sessionId, new TurnId("otel-tool-args-turn"), null);
        var environment = new SessionToolRunEnvironment
        {
            Storage = SessionStoragePaths.CreateLegacy(
                sessionDirectory,
                Path.Combine(sessionDirectory, "logs"),
                "otel-tool-args", ManagedTemporaryLayout.InsideSessionStorage.Instance),
            InlineOutputBudget = new InlineOutputBudget(4096),
            ModelInputModalities = ModelModality.Text,
            SpawnChildActor = static (_, _, _) => Task.FromResult<object>(new object())
        };
        var batch = new SessionToolBatch(turnContext, environment)
        {
            ToolCalls = [call],
            DefaultTimeout = new ToolExecutionTimeout(TimeSpan.FromSeconds(5)),
            ReplyTo = ActorRefs.Nobody,
            EmitSubAgentOutput = static _ => { },
            ApprovalRequests = new ToolApprovalRequests(
                new ApprovalChannel(),
                static _ => { },
                new ToolExecutionTimeout(Timeout.InfiniteTimeSpan)),
            BackgroundJobs = new BackgroundJobDispatch.Unavailable(),
            CancellationToken = TestContext.Current.CancellationToken
        };

        await new SessionToolExecutionPipeline(new OkExecutor(), TimeProvider.System, logger)
            .ExecuteAsync(batch)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    private sealed class OkExecutor : IToolExecutor
    {
        public Task<string> ExecuteAsync(
            FunctionCallContent toolCall,
            ToolExecutionContext context,
            CancellationToken ct = default)
            => Task.FromResult("ok");
    }

    private sealed record CapturedRecord(
        string? FormattedMessage,
        string? Body,
        IReadOnlyDictionary<string, string?> Attributes);

    /// <summary>
    /// The SDK reuses a LogRecord after OnEnd returns, so this processor copies
    /// each record before it keeps it.
    /// </summary>
    private sealed class CapturingLogProcessor(string prefix) : BaseProcessor<LogRecord>
    {
        private readonly ConcurrentQueue<CapturedRecord> _all = new();
        private readonly TaskCompletionSource<CapturedRecord> _matched =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CapturedRecord> Matched => _matched.Task;

        public IReadOnlyList<CapturedRecord> All => _all.ToArray();

        public override void OnEnd(LogRecord data)
        {
            var attributes = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (data.Attributes is { } list)
            {
                foreach (var pair in list)
                    attributes[pair.Key] = pair.Value?.ToString();
            }

            var record = new CapturedRecord(data.FormattedMessage, data.Body, attributes);
            _all.Enqueue(record);
            if ((record.FormattedMessage ?? record.Body ?? string.Empty).Contains(prefix, StringComparison.Ordinal))
                _matched.TrySetResult(record);
        }
    }
}

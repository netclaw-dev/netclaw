using System.Text;
using Akka.Actor;
using Akka.Event;
using Akka.Streams;
using Akka.Streams.Dsl;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;

namespace Netclaw.Actors.Reminders;

/// <summary>
/// Short-lived child actor handling a single reminder execution.
/// Creates a session pipeline, sends reminder instructions, collects output,
/// and reports success/failure to <see cref="ReminderManagerActor"/>.
/// </summary>
internal sealed class ReminderExecutionActor : ReceiveActor
{
    private readonly Guid _executionId;
    private readonly ReminderDefinition _definition;
    private readonly SessionPipeline _pipeline;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggingAdapter _log;

    private readonly StringBuilder _buffer = new();
    private bool _sawTextDelta;
    private bool _completed;

    private ActorMaterializer? _materializer;
    private MaterializedSession? _session;

    public static Props CreateProps(
        Guid executionId,
        ReminderDefinition definition,
        SessionPipeline pipeline,
        ReminderConfig config,
        TimeProvider timeProvider) =>
        Props.Create(() => new ReminderExecutionActor(executionId, definition, pipeline, config, timeProvider));

    private ReminderExecutionActor(
        Guid executionId,
        ReminderDefinition definition,
        SessionPipeline pipeline,
        ReminderConfig config,
        TimeProvider timeProvider)
    {
        _executionId = executionId;
        _definition = definition;
        _pipeline = pipeline;
        _timeProvider = timeProvider;
        _log = Context.GetLogger();

        Context.SetReceiveTimeout(TimeSpan.FromSeconds(config.ExecutionTimeoutSeconds));

        Receive<ExecutionOutput>(HandleOutput);
        Receive<ExecutionStarted>(_ => { });
        Receive<ReceiveTimeout>(_ =>
        {
            _log.Warning("Reminder execution timed out: '{0}'", _definition.Title);
            ReportAndStop(false, "Execution timed out");
        });
    }

    protected override void PreStart()
    {
        Self.Tell(new ExecutionStarted());
        RunTask(InitializeAsync);
    }

    private async Task InitializeAsync()
    {
        try
        {
            var sessionId = !string.IsNullOrWhiteSpace(_definition.SessionId)
                ? new SessionId(_definition.SessionId)
                : new SessionId($"reminder/{_definition.Id}/{_timeProvider.GetUtcNow().ToUnixTimeMilliseconds()}");

            _log.Info("Starting reminder execution for '{0}' with session '{1}'",
                _definition.Title, sessionId.Value);

            _materializer = Context.Materializer(namePrefix: "reminder-exec");

            var materialized = await _pipeline.CreateAsync(sessionId, new SessionPipelineOptions
            {
                ChannelType = "reminder",
                Filter = OutputFilter.Text
            }, materializer: _materializer);

            var self = Self;

            var inputQueue = Source.Queue<ChannelInput>(8, OverflowStrategy.Backpressure)
                .ToMaterialized(materialized.Input, Keep.Left)
                .Run(_materializer);

            materialized.Output
                .To(Sink.ForEach<SessionOutput>(output => self.Tell(new ExecutionOutput(output))))
                .Run(_materializer);

            _session = materialized;

            var prompt = BuildPrompt(_definition);

            await inputQueue.OfferAsync(new ChannelInput
            {
                SenderId = "reminder-system",
                ChannelId = _definition.ReportToChannel,
                Contents = [new TextContent(prompt)],
                ReceivedAt = _timeProvider.GetUtcNow()
            });

            inputQueue.Complete();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to initialize reminder execution for '{0}'", _definition.Title);
            ReportAndStop(false, ex.Message);
        }
    }

    private static string BuildPrompt(ReminderDefinition definition)
    {
        return
            $"{definition.Instructions}\n\n" +
            "Notification instructions:\n" +
            definition.NotifyInstructions;
    }

    private void HandleOutput(ExecutionOutput wrapper)
    {
        switch (wrapper.Output)
        {
            case TextDeltaOutput delta:
                _buffer.Append(delta.Delta);
                _sawTextDelta = true;
                break;

            case TextOutput text:
                if (!_sawTextDelta)
                    _buffer.Append(text.Text);
                break;

            case TurnCompleted:
                var result = _buffer.ToString().Trim();
                _log.Info("Reminder '{0}' execution completed, output length: {1}",
                    _definition.Title, result.Length);

                if (string.IsNullOrWhiteSpace(result))
                    result = $"(Reminder '{_definition.Title}' executed but produced no output)";

                _log.Info("Reminder output for '{0}': {1}",
                    _definition.Title, result.Length > 200 ? result[..200] + "..." : result);

                ReportAndStop(true);
                break;

            case ErrorOutput err:
                _log.Warning("Reminder '{0}' error output: {1}", _definition.Title, err.Message);
                ReportAndStop(false, err.Message);
                break;
        }
    }

    private void ReportAndStop(bool success, string? errorMessage = null)
    {
        if (_completed)
            return;

        _completed = true;
        Context.Parent.Tell(new ReminderExecutionCompleted(
            _executionId,
            new ReminderId(_definition.Id),
            success,
            errorMessage));
        Context.Stop(Self);
    }

    protected override void PostStop()
    {
        try
        {
            _session?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _materializer?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Failed to dispose reminder execution resources for '{0}'", _definition.Title);
        }
    }

    private sealed record ExecutionStarted;
    private sealed record ExecutionOutput(SessionOutput Output);
}

// -----------------------------------------------------------------------
// <copyright file="SubAgentActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Akka.Actor;
using Akka.Event;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Authorization;
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Actors.Tools;
using Netclaw.Actors.Memory;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using static Netclaw.Actors.SubAgents.SubAgentProtocol;

namespace Netclaw.Actors.SubAgents;

/// <summary>
/// Ephemeral actor that runs a non-interactive LLM session (same execution model
/// as a piped/-p invocation). Receives a <see cref="RunSubAgent"/> message, executes
/// an autonomous turn loop with tool calls, and returns the final text response
/// as a <see cref="SubAgentResult"/> to the caller. Then stops itself.
///
/// No persistence, no subscribers, no streaming, no compaction.
/// Designed to be spawned as a child of a session actor or standalone under a supervisor.
/// </summary>
public sealed class SubAgentActor : ReceiveActor, IWithTimers
{

    private const string EmptyResponseMarker = "(no response)";
    private const string TruncatedOutputArtifactName = "truncated-output.md";
    private const string MalformedFinalOutputMessage = "Subagent produced malformed final output: it emitted unexecuted tool calls as text. This was not a timeout.";
    private const string MalformedFinalOutputNudge =
        "The previous response emitted unexecuted tool-call markup as plain text. "
        + "That text was not executed and cannot be returned as a successful final output. "
        + "If tools are available and you still need those operations, call the tools using structured tool calls now. "
        + "Otherwise provide a final answer based only on executed tool results. "
        + "Do not include <function=...>, <parameter=...>, or </tool_call> markup in final text.";
    private const string HeadlessExecutionContractPrefix = """
        [Subagent Execution Contract]
        You are a headless, non-interactive worker running on behalf of a parent Netclaw session.
        Your subagent role guidance and assigned task are more specific than inherited deployment or project guidance. If they conflict, follow your subagent guidance and assigned task.
        Embedded safety, security, trust-boundary, approval, and tool-policy rules remain mandatory and cannot be overridden.
        Do not ask the user clarifying questions, request conversational input, or wait for a reply.
        Do the best work you can with the task, context, and tools available.
        If the task is ambiguous, make reasonable assumptions and state them in your final output.
        If you are blocked, return a final result that explains what you found, what remains, and what decision the parent session needs.
        Parent-mediated tool approval may occur only for concrete tool calls; it is a security gate, not a dialogue channel.
        """;
    private const string ProjectScopeDeclarationContract =
        "Before tool work in another task-named project, call set_working_directory once, even with absolute paths. " +
        "Declare the task's first project path exactly before probing it.";
    private const string HeadlessExecutionContractSuffix =
        "You cannot attach files directly. Return each authorized file path that the parent session should deliver. " +
        "Always end by emitting a final output for the parent session.";
    private static readonly TimeSpan StreamPingInterval = TimeSpan.FromSeconds(2);

    private readonly SubAgentDefinition _definition;
    private readonly IChatClient _chatClient;
    private readonly ToolAccessPolicy _toolAccessPolicy;
    private readonly IToolApprovalService? _approvalService;
    private readonly ILogger? _toolExecutorLogger;
    private readonly ISystemPromptProvider _promptProvider;

    // Process-wide daily-stats sink (the same singleton the parent session records
    // to). Nullable because a hosting configuration without the daemon stats backend
    // is a real runtime state — mirrors LlmSessionActor._sessionMetrics. When present,
    // every LLM call this sub-agent makes is billed here so its tokens show up in
    // `netclaw stats` instead of vanishing.
    private readonly Telemetry.ISessionMetrics? _sessionMetrics;
    private readonly ToolRegistry _toolRegistry;
    private readonly DiscoveredToolCache _toolExposure = new();
    private readonly HashSet<string> _loadedDeferredToolNames = new(StringComparer.Ordinal);
    private IReadOnlyList<AITool> _aiTools = [];
    private int _coreToolCount;
    private ILoggingAdapter _log;
    private readonly MemoryPolicyEvaluator _policyEvaluator = new();
    private readonly TurnStateTracker _turnState = new();
    private PreparedToolCycleBatch? _activeCycleBatch;
    // The repeat nudge for the active batch. The child adds it after the batch results.
    private string? _activeRepeatNudge;

    // Stopwatch tracking the total wall-clock duration of the sub-agent run.
    // Used for the summary log on completion (ProcessingWatchdog is only a
    // timer scheduler — it doesn't track elapsed time itself).
    private readonly Stopwatch _runStopwatch = Stopwatch.StartNew();

    // Cumulative token usage across every LLM call this sub-agent makes. Summed for
    // the completion summary log; per-call usage is also recorded to _sessionMetrics
    // as each call returns (see RecordUsage).
    private long _runInputTokens;
    private long _runOutputTokens;

    // Conversation state (not persisted — ephemeral)
    private readonly List<AiChatMessage> _history = [];
    // Every message after the system message, in the serializable form that a park carries (M12).
    private readonly List<SerializableChatMessage> _transcript = [];
    private readonly ManagedTemporaryCorrectionState _managedTemporaryCorrections = new();
    private long _llmCallId;
    private ChildRunExecution? _ownedRun;
    private int _checkpointRound;
    // running-child-drain-park design D2: after ParkForDrain the child starts no model call and no tool batch. A batch
    // that runs ends first, so its results enter the park.
    private bool _parkForDrain;
    private bool _toolBatchRuns;
    private IActorRef _replyTo = ActorRefs.Nobody;
    private CancellationTokenSource? _executionCts;

    // Cancelled only by a stop of the run (SubAgentCancelled, Complete, PostStop), never by the watchdog. A tool
    // cancellation under this token is not a tool error, so it ends the batch instead of a tool result.
    private CancellationTokenSource? _externalCts;

    private IParentApprovalBridge? _approvalBridge;
    private ChannelWriter<ToolActivityUpdate>? _activitySink;

    // Parent session id (scopeId with the "/subagent/..." suffix stripped) and the full
    // sub-session scope id. Carried on the sub-agent's ChatOptions so its LLM-pipeline lines
    // group under the parent in OTEL (SessionId) while the file-logger partitions them into the
    // sub-agent's own session.log (SubSessionId) — matching the enriched logger's own context.
    private string? _parentSessionId;
    private string? _subSessionId;
    private ChildFileActivityTracker _fileActivity = new(WorkingContext.Empty);
    private string? _projectInstructions;

    // Default wait-for-first-delta budget when the spawn message carries none
    // (direct/test callers). Mirrors SessionConfig.PrefillTimeout so an unset
    // prefill never collapses to the tighter inter-delta budget — that collapse
    // would silently reintroduce the cold-prefill timeout this watchdog exists
    // to prevent.
    private static readonly TimeSpan DefaultPrefillBudget = TimeSpan.FromSeconds(1800);

    // Two-phase inactivity watchdog, shared with the main session path
    // (LlmSessionActor). The watchdog starts each LLM call on the generous
    // _prefillBudget (content-free keepalives refresh it so a slow cold prefill
    // is not mistaken for a hang) and promotes to the tighter _interDeltaBudget
    // on the first substantive delta. _anyContentStreamed tracks whether the
    // current call has produced real output yet. _noProgressBudget is the
    // keepalive-immune ceiling reset only by real tokens — it bounds a backend
    // that heartbeats forever without producing output (null = unbounded).
    private readonly ProcessingWatchdog _watchdog = new();
    private TimeSpan _interDeltaBudget;
    private TimeSpan _prefillBudget;
    private TimeSpan? _noProgressBudget;
    private bool _anyContentStreamed;

    public ITimerScheduler Timers { get; set; } = null!;
    private CancellationTokenRegistration _externalCancellationRegistration;
    private ToolExecutionContext? _toolExecutionContext;
    private ToolExecutionContext ToolExecutionContext
        => _toolExecutionContext
           ?? throw new InvalidOperationException("Sub-agent tool context is unavailable before a run is admitted.");
    private bool _malformedFinalOutputRepairAttempted;

    public SubAgentActor(
        SubAgentDefinition definition,
        IChatClient chatClient,
        ToolAccessPolicy toolAccessPolicy,
        IToolApprovalService? approvalService = null,
        Telemetry.ISessionMetrics? sessionMetrics = null)
        : this(
            definition,
            chatClient,
            toolAccessPolicy,
            NullSystemPromptProvider.Instance,
            approvalService,
            sessionMetrics,
            coreToolNames: null,
            toolExecutorLogger: null)
    {
    }

    private SubAgentActor(
        SubAgentDefinition definition,
        IChatClient chatClient,
        ToolAccessPolicy toolAccessPolicy,
        ISystemPromptProvider promptProvider,
        IToolApprovalService? approvalService,
        Telemetry.ISessionMetrics? sessionMetrics,
        IReadOnlySet<string>? coreToolNames,
        ILogger? toolExecutorLogger)
    {
        // A sub-agent must run under a fully-wired access policy — never a
        // degraded default that drops the deny-list / protected-path checks.
        // Callers (SubAgentSpawner) inject the session's real policy; the
        // non-nullable parameter enforces it.
        _definition = definition;
        _chatClient = chatClient;
        _sessionMetrics = sessionMetrics;
        _toolAccessPolicy = toolAccessPolicy;
        _promptProvider = promptProvider;
        _approvalService = approvalService;
        _toolExecutorLogger = toolExecutorLogger;
        _projectInstructions = definition.ProjectInstructions;
        _log = Context.GetLogger();

        _toolRegistry = new ToolRegistry();
        var hasSearchTools = false;
        var searchToolsIsCore = false;
        var hasLoadTool = false;
        var loadToolIsCore = false;
        foreach (var tool in definition.Tools)
        {
            if (!SubAgentToolPolicy.IsAllowedForSubAgent(tool.Name))
                continue;

            var isCore = coreToolNames is null || coreToolNames.Contains(tool.Name);
            if (tool is SearchToolsTool)
            {
                hasSearchTools = true;
                searchToolsIsCore = isCore;
                continue;
            }

            if (tool is LoadToolTool)
            {
                hasLoadTool = true;
                loadToolIsCore = isCore;
                continue;
            }

            RegisterTool(tool, isCore);
        }

        if (hasSearchTools)
            RegisterTool(new SearchToolsTool(_toolRegistry, _toolAccessPolicy), searchToolsIsCore);
        if (hasLoadTool)
            RegisterTool(new LoadToolTool(_toolRegistry, _toolAccessPolicy), loadToolIsCore);

        Become(Idle);

        void RegisterTool(INetclawTool tool, bool isCore)
        {
            if (isCore)
                _toolRegistry.RegisterCore(tool);
            else
                _toolRegistry.Register(tool);
        }
    }

    public static Props CreateProps(
        SubAgentDefinition definition,
        IChatClient chatClient,
        ToolAccessPolicy toolAccessPolicy,
        IToolApprovalService? approvalService = null,
        Telemetry.ISessionMetrics? sessionMetrics = null)
    {
        return Props.Create(() => new SubAgentActor(
            definition,
            chatClient,
            toolAccessPolicy,
            approvalService,
            sessionMetrics));
    }

    internal static Props CreatePropsWithProjectInstructionProvider(
        SubAgentDefinition definition,
        IChatClient chatClient,
        ToolAccessPolicy toolAccessPolicy,
        ISystemPromptProvider promptProvider,
        IToolApprovalService? approvalService = null,
        Telemetry.ISessionMetrics? sessionMetrics = null,
        IReadOnlySet<string>? coreToolNames = null,
        ILogger? toolExecutorLogger = null)
    {
        ArgumentNullException.ThrowIfNull(promptProvider);
        return Props.CreateBy(new SubAgentActorProducer(
            definition,
            chatClient,
            toolAccessPolicy,
            promptProvider,
            approvalService,
            sessionMetrics,
            coreToolNames,
            toolExecutorLogger));
    }

    private sealed class SubAgentActorProducer(
        SubAgentDefinition definition,
        IChatClient chatClient,
        ToolAccessPolicy toolAccessPolicy,
        ISystemPromptProvider promptProvider,
        IToolApprovalService? approvalService,
        Telemetry.ISessionMetrics? sessionMetrics,
        IReadOnlySet<string>? coreToolNames,
        ILogger? toolExecutorLogger) : IIndirectActorProducer
    {
        public Type ActorType => typeof(SubAgentActor);

        public ActorBase Produce()
            => new SubAgentActor(
                definition,
                chatClient,
                toolAccessPolicy,
                promptProvider,
                approvalService,
                    sessionMetrics,
                coreToolNames,
                toolExecutorLogger);

        public void Release(ActorBase actor)
        {
        }
    }

    /// <summary>
    /// Final cleanup if the actor is stopped externally (parent supervision,
    /// <c>PoisonPill</c>) without going through <see cref="Complete"/>. Cancels
    /// both CTSes so any in-flight approval wait running on the thread pool
    /// unblocks promptly, then replies once to any outstanding ask. <see cref="Complete"/>
    /// already nulls the CTSes, so the null-conditional access is intentional.
    /// </summary>
    protected override void PostStop()
    {
        _executionCts?.Cancel();
        _externalCts?.Cancel();
        if (!_completed)
        {
            _completed = true;
            _replyTo.Tell(new SubAgentResult
            {
                Completion = new ChildRunCompletion.Failed(SubAgentOutcomeReason.ActorStopped),
                Output = "Subagent stopped before completion.",
                AgentName = _definition.Name,
                Findings = [],
                FindingsCount = 0
            });
        }
        _executionCts?.Dispose();
        _externalCts?.Dispose();
        _externalCancellationRegistration.Dispose();
        _toolExposure.EvictAll();
        _loadedDeferredToolNames.Clear();
        base.PostStop();
    }

    private void Idle()
    {
        Receive<RunSubAgent>(msg =>
        {
            _replyTo = Sender;
            _ownedRun = msg.OwnedRun;

            _approvalBridge = msg.Scope.Authority.InteractiveApproval is InteractiveApprovalCapability.Available available
                ? available.Bridge
                : null;
            var scopeId = msg.Scope.ScopeId.Value;
            var subAgentAudience = msg.Scope.Authority.Audience;
            _toolExecutionContext = new ToolExecutionContext(
                msg.Scope.Authority,
                ToolExecutionTimeout.Default);
            _fileActivity = new ChildFileActivityTracker(msg.Scope.InitialWorkingSnapshot.WorkingContext);
            var coreTools = ResolveCoreAiTools();
            _toolExposure.SeedBaseTools(coreTools);
            _aiTools = _toolExposure.AvailableTools;
            _coreToolCount = coreTools.Count;
            _executionCts = new CancellationTokenSource();
            _externalCts = new CancellationTokenSource();
            var self = Self; // Capture before callback — Self requires active actor context
            _externalCancellationRegistration = msg.Cancellation.Register(() => self.Tell(SubAgentCancelled.Instance));

            // Enrich the logger so every sub-agent log line correlates back to the
            // parent session (SessionId) and to this specific sub-agent run
            // (SubSessionId), and is plainly attributable to the sub-agent. scopeId is
            // "{parentSessionId}/subagent/{name}/{runId}"; NormalizeSessionId strips the
            // "/subagent/..." suffix to recover the parent. SessionId matches the key the
            // session/channel actors already tag their loggers with, so sub-agent and
            // parent logs share one filterable attribute (so OTEL groups them under the parent);
            // SubSessionId isolates a single run and is what the file-logger partitions on, so the
            // sub-agent's lines land in its OWN session.log rather than the parent's.
            var parentSessionId = SubAgentSessionScope.NormalizeSessionId(scopeId);
            _parentSessionId = parentSessionId;
            _subSessionId = scopeId;
            var enrichedLog = Context.GetLogger();
            if (!string.IsNullOrWhiteSpace(parentSessionId))
                enrichedLog = enrichedLog.WithContext(NetclawLogProperties.SessionId, parentSessionId);
            if (!string.IsNullOrWhiteSpace(scopeId))
                enrichedLog = enrichedLog.WithContext(NetclawLogProperties.SubSessionId, scopeId);
            _log = enrichedLog;

            // The run is bounded by a two-phase inactivity watchdog re-armed on
            // every progress event (LLM response, tool batch, streaming delta,
            // keepalive) plus a keepalive-immune no-progress deadline. A sub-agent
            // making real progress is never killed; a stalled one (no bytes) or a
            // wedged one (keepalives but no tokens) is. FireLlmCall arms both. An
            // unset prefill defaults to the session budget rather than collapsing
            // to the inter-delta budget; an unset no-progress deadline is
            // unbounded (only direct/test callers leave it unset).
            _activitySink = msg.ActivitySink;
            _interDeltaBudget = msg.Timeout;
            _prefillBudget = msg.PrefillTimeout > TimeSpan.Zero ? msg.PrefillTimeout : DefaultPrefillBudget;
            _noProgressBudget = msg.NoProgressTimeout > TimeSpan.Zero ? msg.NoProgressTimeout : null;

            // Build initial conversation: system prompt (from file, verbatim) + task as user message.
            // If the caller supplied runtime context, prefix it onto the user message so the
            // system prompt stays reproducible across invocations.
            _history.Add(new AiChatMessage(
                Microsoft.Extensions.AI.ChatRole.System,
                BuildSystemPrompt(
                    _definition,
                    _projectInstructions,
                    CanDeclareProjectScope())));
            _log.Info("SubAgent [{AgentName}] starting (tools={ToolCount}, prefill={Prefill}, interDelta={InterDelta}, noProgress={NoProgress}, resume={Resume})",
                _definition.Name, _aiTools.Count, _prefillBudget, _interDeltaBudget,
                _noProgressBudget?.ToString() ?? "unbounded", msg.Resume is not null);
            LogToolExposure();
            // Become first: a resume that ends the run at once hands over, and that behavior must win.
            Become(Processing);
            if (msg.Resume is { } resume)
                ResumeFromPark(resume);
            else
            {
                AddMessage(new SerializableChatMessage
                {
                    Role = Protocol.ChatRole.User,
                    Content = BuildUserMessage(msg.RuntimeContext,
                        BuildModelContext(msg.Scope.InitialWorkingSnapshot, subAgentAudience, ToolExecutionContext.SessionStorage),
                        msg.Task)
                });
                FireLlmCall();
            }
        });
    }

    private void Processing()
    {
        Receive<ChildCheckpointCommitted>(_ => FireLlmCall());
        Receive<ChildTerminalCommitted>(_ => Context.Stop(Self));
        Receive<ParkForDrain>(_ =>
        {
            _parkForDrain = true;
            // A model call has no outside effect, so the park cancels it, and the resumed child makes the call again.
            if (!_toolBatchRuns)
                ParkForDrainNow();
        });
        Receive<LlmResponseReceived>(msg =>
        {
            if (msg.CallId != _llmCallId)
                return;
            // The streaming call finished; re-baseline to the inter-delta budget for
            // the synchronous processing that follows (tool dispatch or completion).
            RestartWatchdog(_interDeltaBudget);
            var response = msg.Response;

            // Record this call's token usage before branching so EVERY call is billed —
            // tool-call turns, retries, and repair turns
            // all flow through here exactly once. Mirrors the main session, which records
            // its own per-call usage; without this the sub-agent's tokens never reach the
            // daily-stats pipeline and `netclaw stats` under-counts by the whole sub-run.
            RecordUsage(response.Usage);

            var lastMessage = response.Messages[^1];
            var analysis = LlmResponseClassifier.Analyze(lastMessage);

            _log.Info(
                $"SubAgent [{_definition.Name}] LLM response callId={msg.CallId} kind={analysis.Kind} "
                + $"text={analysis.TextChars}ch thinking={analysis.ThinkingChars}ch "
                + $"toolCalls={analysis.ToolCalls.Count} finishReason={response.FinishReason?.ToString() ?? "null"} "
                + $"streamUpdates={msg.StreamUpdateCount} emptyUpdates={msg.EmptyStreamUpdateCount} "
                + $"streamText={msg.StreamTextDeltaCount}/{msg.StreamTextChars}ch "
                + $"streamThinking={msg.StreamThinkingDeltaCount}/{msg.StreamThinkingChars}ch "
                + $"streamToolCalls={msg.StreamToolCallDeltaCount} "
                + $"textPreview={PreviewForLog(ExtractText(lastMessage), 300)} "
                + $"toolCallPreview={FormatToolCallPreview(analysis.ToolCalls)}");

            var toolCalls = analysis.ToolCalls;
            if (toolCalls.Count > 0)
            {
                HandleToolCalls(lastMessage, toolCalls);
                return;
            }

            if (analysis.Kind is LlmResponseKind.ThinkingOnly or LlmResponseKind.Empty)
            {
                var truncated = response.FinishReason == ChatFinishReason.Length;
                switch (_turnState.EvaluateEmptyResponse(analysis.Kind, truncated))
                {
                    case EmptyResponseAction.Retry retry:
                        _log.Warning(
                            "SubAgent [{AgentName}] produced {Kind} response ({ThinkingChars} reasoning chars, truncated={Truncated}) — retrying with nudge",
                            _definition.Name,
                            analysis.Kind,
                            analysis.ThinkingChars,
                            truncated);
                        AddSystemNudge(retry.NudgeText);
                        FireLlmCall();
                        return;
                    case EmptyResponseAction.Fail fail:
                        _log.Warning(
                            "SubAgent [{AgentName}] produced repeated {Kind} responses — reporting failure",
                            _definition.Name,
                            analysis.Kind);
                        Complete(false, fail.ErrorMessage, SubAgentRunOutcome.Failed, SubAgentOutcomeReason.EmptyFinalResponse);
                        return;
                }
            }

            // A reply that ended on the output limit is cut off. Its text is a stub,
            // not a result, so it is never a success. A retry would meet the same limit.
            if (response.FinishReason == ChatFinishReason.Length)
            {
                var stub = ExtractText(lastMessage);
                _log.Warning(
                    "SubAgent [{AgentName}] reply was truncated (finishReason=length, text={TextChars}ch, thinking={ThinkingChars}ch, toolCalls=0) — reporting failure",
                    _definition.Name,
                    analysis.TextChars,
                    analysis.ThinkingChars);
                var savedAt = SaveTruncatedOutput(stub);
                Complete(
                    false,
                    "Subagent output was truncated: the model reached its output limit before it finished, "
                    + "and it made no tool call. The text is cut off and is not a result. "
                    + (savedAt is null ? "" : $"The full cut-off text is saved at {savedAt}. ")
                    + $"Cut-off text: {PreviewRedacted(stub, 300)}",
                    SubAgentRunOutcome.Failed,
                    SubAgentOutcomeReason.OutputTruncated);
                return;
            }

            // Final text response — we're done.
            var text = ExtractText(lastMessage);
            if (string.IsNullOrWhiteSpace(text) || text == EmptyResponseMarker)
            {
                // LLM returned neither tool calls nor usable text. Surface as failure
                // so the parent session sees a real error instead of fabricating
                // "subagent still working" from an empty tool result.
                _log.Warning(
                    "SubAgent [{AgentName}] LLM returned empty text after non-empty analysis — reporting as failure",
                    _definition.Name);
                Complete(false, "Subagent returned an empty final response.", SubAgentRunOutcome.Failed, SubAgentOutcomeReason.EmptyFinalResponse);
                return;
            }

            if (ContainsUnexecutedToolCallMarkup(text))
            {
                if (!_malformedFinalOutputRepairAttempted)
                {
                    _malformedFinalOutputRepairAttempted = true;
                    _log.Warning(
                        "SubAgent [{AgentName}] emitted unexecuted tool-call markup as final text; retrying with repair nudge",
                        _definition.Name);
                    AddSystemNudge(MalformedFinalOutputNudge);
                    FireLlmCall();
                    return;
                }

                _log.Warning(
                    "SubAgent [{AgentName}] repeatedly emitted unexecuted tool-call markup as final text; reporting failure",
                    _definition.Name);
                Complete(false, MalformedFinalOutputMessage, SubAgentRunOutcome.Failed, SubAgentOutcomeReason.MalformedFinalOutput);
                return;
            }

            Complete(
                true,
                text,
                SubAgentRunOutcome.Completed);
        });

        Receive<ToolExecutionCompleted>(msg =>
        {
            if (msg.CallId != _llmCallId)
                return;
            _toolBatchRuns = false;
            RecordProgress("processing tool results");
            // H3: a parked call runs later in the directories of this batch, before a finished call changes them.
            var batchDirectories = new ChildParkScopeDirectories(ToolExecutionContext.RunScope.ProjectDirectory,
                ToolExecutionContext.RunScope.InheritedCwd);
            var parked = msg.Results.Where(static result => result.Parked is not null).ToArray();

            // A parked batch records no loop observation (cut plan): the resumed child observes only its own batches.
            if (_activeCycleBatch is { } cycleBatch && parked.Length == 0)
            {
                var cycleResults = msg.Results.ToDictionary(
                    result => result.Message.ToolCallId is { } callId
                        ? callId.Value
                        : throw new InvalidOperationException("A tool cycle result requires a call identity."),
                    result => new ToolCycleResult(
                        result.Receipt?.Category ?? ToolInvocationOutcomeCategory.Success,
                        result.Message.Content ?? string.Empty)
                    {
                        PendingJob = result.Receipt is ToolInvocationReceipt.PendingBackgroundJob,
                        Synthetic = result.Receipt is ToolInvocationReceipt.Correction
                            { RemediationCode: ToolRemediationCode.BreakToolCycle }
                    },
                    StringComparer.Ordinal);
                _turnState.ObserveCompleted(ToolCycleSignatureFactory.Complete(cycleBatch, cycleResults));
            }
            _activeCycleBatch = null;

            // Append tool results as MEAI messages and log each result. A parked call has no result yet.
            foreach (var result in msg.Results.Where(static result => result.Parked is null))
            {
                var message = result.Message;
                AddMessage(message);
                if (message.ToolCallId is { } callId)
                {
                    var receipt = result.Receipt;
                    _fileActivity.Apply(receipt);
                    TryApplyProjectDirectory(message.Name, receipt);
                    _log.Info(
                        "SubAgent authorization attempt result authorizationAttemptId={AuthorizationAttemptId} " +
                        "sessionId={SessionId} subSessionId={SubSessionId} callId={CallId} " +
                        "outcomeCategory={OutcomeCategory} remediationCode={RemediationCode}",
                        result.AuthorizationAttemptId.Value,
                        _parentSessionId,
                        _subSessionId,
                        callId.Value,
                        receipt?.Category.ToString(),
                        (receipt as ToolInvocationReceipt.Correction)?.RemediationCode.ToString());
                    if (result.ExposureRequest is { } exposureRequest)
                        TryActivateDiscoveredTool(exposureRequest.ToolName.Value);
                }
                if (message.Name is "load_tool" && message.Content is not null)
                    TryActivateDiscoveredTool(message.Content.Trim());
            }

            foreach (var result in msg.Results)
            {
                if (result.ManagedTemporaryCorrectionUpdate is { } change)
                    _managedTemporaryCorrections.Apply(change);
            }

            // The nudges come after all results, so a park drops them (cut plan: no deferred media, no repeat nudge).
            if (parked.Length > 0)
            {
                Park(parked.Select(static result => result.Parked!).ToArray(), batchDirectories);
                return;
            }

            AddModelInputMediaNudge(msg.Results
                .SelectMany(result => result.ModelInputMediaReferences)
                .ToArray());
            if (_activeRepeatNudge is { } repeatNudge)
            {
                // The latest repeat nudge replaces the earlier one, so equal nudges do not pile up.
                const string earlierNudge = $"[system: {ToolCycleMessages.RepeatNudgeStart}";
                _history.RemoveAll(static message => message.Role == Microsoft.Extensions.AI.ChatRole.User
                    && message.Text.StartsWith(earlierNudge, StringComparison.Ordinal));
                _transcript.RemoveAll(static message => message.Role == Protocol.ChatRole.User
                    && message.Content.StartsWith(earlierNudge, StringComparison.Ordinal));
                AddSystemNudge(repeatNudge);
                _activeRepeatNudge = null;
            }

            _turnState.RecordToolCompletion(msg.Results.Length);

            _log.Debug("SubAgent [{AgentName}] tool iteration {Count}, continuing",
                _definition.Name, _turnState.ToolIterationCount);
            FireLlmCall();
        });

        Receive<ChildToolExecutionFailed>(msg =>
        {
            if (msg.CallId != _llmCallId)
                return;
            _toolBatchRuns = false;
            _activeCycleBatch = null;
            _activeRepeatNudge = null;
            _log.Error(msg.Cause, "SubAgent [{AgentName}] tool execution failed", _definition.Name);
            Complete(false, $"Tool execution failed: {msg.Cause.Message}", SubAgentRunOutcome.Failed, SubAgentOutcomeReason.ToolExecutionFailed);
        });

        Receive<LlmCallFailed>(msg =>
        {
            if (msg.CallId != _llmCallId)
                return;
            _toolExposure.EvictAll();
            _loadedDeferredToolNames.Clear();
            _log.Error(msg.Cause, "SubAgent [{AgentName}] LLM call failed callId={CallId}", _definition.Name, msg.CallId);
            Complete(false, $"LLM call failed: {msg.Cause.Message}", SubAgentRunOutcome.Failed, SubAgentOutcomeReason.LlmCallFailed);
        });

        Receive<SubAgentCancelled>(_ =>
        {
            _executionCts?.Cancel();
            _externalCts?.Cancel();
            _log.Warning("SubAgent [{AgentName}] cancelled by parent", _definition.Name);
            Complete(false, "Subagent cancelled by parent", SubAgentRunOutcome.Failed, SubAgentOutcomeReason.CancelledByParent);
        });

        Receive<ProcessingWatchdogExpired>(msg =>
        {
            // Ignore a stale expiry from a watchdog operation that has since been
            // re-armed. Only Start bumps the operation id; a keepalive/promote
            // reschedules the same id, and Stop clears the operation name.
            if (!_watchdog.IsCurrent(msg))
                return;

            if (msg.NoProgress)
            {
                // The keepalive-immune deadline fired: the backend streamed
                // heartbeats (or nothing) for the full no-progress budget without a
                // single substantive token. Unlike a liveness tick this gets no
                // approval suppression or grace — it is only armed during an active
                // LLM stream and is never refreshed by keepalives, so reaching it
                // means a genuine wedge.
                var noProgress = RequireNoProgressBudget().TotalSeconds;
                _executionCts?.Cancel();
                _watchdog.Stop(Timers);
                _log.Warning(
                    "SubAgent [{AgentName}] timed out: no substantive output for {Budget:F0}s after {Iterations} tool iterations",
                    _definition.Name, noProgress, _turnState.ToolIterationCount);
                Complete(false, $"Subagent timed out: no substantive output for {noProgress:F0}s.", SubAgentRunOutcome.Failed, SubAgentOutcomeReason.NoSubstantiveOutputTimeout);
                return;
            }

            if (msg.ThinkingCap)
            {
                // One model call streamed only thinking for the whole cap. Thinking
                // resets the no-progress deadline, so only this cap stops the call.
                // Like the no-progress deadline, it gets no approval grace.
                var cap = RequireNoProgressBudget().TotalSeconds * ProcessingWatchdog.ThinkingCapMultiplier;
                _executionCts?.Cancel();
                _watchdog.Stop(Timers);
                _log.Warning(
                    "SubAgent [{AgentName}] timed out: one model call streamed only thinking for {Budget:F0}s after {Iterations} tool iterations",
                    _definition.Name, cap, _turnState.ToolIterationCount);
                Complete(false, $"Subagent timed out: one model call streamed only thinking for {cap:F0}s, with no text and no tool call.", SubAgentRunOutcome.Failed, SubAgentOutcomeReason.ThinkingOnlyTimeout);
                return;
            }

            // Report the budget that was actually in force: the prefill budget
            // while still waiting for the first token, else the inter-delta budget.
            var activeBudget = _anyContentStreamed ? _interDeltaBudget : _prefillBudget;
            _executionCts?.Cancel();
            _watchdog.Stop(Timers);
            _log.Warning(
                "SubAgent [{AgentName}] timed out: no activity for {Budget}s ({Phase}) after {Iterations} tool iterations",
                _definition.Name, activeBudget.TotalSeconds,
                _anyContentStreamed ? "inter-delta" : "prefill", _turnState.ToolIterationCount);
            Complete(false, $"Subagent timed out: no activity for {activeBudget.TotalSeconds:F0}s.", SubAgentRunOutcome.Failed, SubAgentOutcomeReason.NoActivityTimeout);
        });

        // Liveness ping from a streaming LLM call — progress, even before the
        // full response message arrives. Keepalives (content-free heartbeats)
        // refresh whichever budget is in force; the first substantive delta
        // promotes from the generous prefill budget to the tighter inter-delta
        // budget. This is the two-phase behavior shared with the main session.
        Receive<SubAgentStreamPing>(msg =>
        {
            if (msg.CallId != _llmCallId)
                return;
            _log.Debug(
                $"SubAgent [{_definition.Name}] LLM stream progress callId={msg.CallId} "
                + $"updates={msg.UpdateCount} emptyUpdates={msg.EmptyUpdateCount} "
                + $"text={msg.TextDeltaCount}/{msg.TextChars}ch "
                + $"thinking={msg.ThinkingDeltaCount}/{msg.ThinkingChars}ch "
                + $"toolCalls={msg.ToolCallDeltaCount} finishReason={msg.FinishReason ?? "null"} "
                + $"activity={msg.Activity}");

            // Same two-phase policy as the main session: keepalives refresh the
            // current budget, the first substantive delta promotes off prefill.
            _anyContentStreamed = _watchdog.OnStreamProgress(
                msg.Activity,
                _anyContentStreamed,
                _prefillBudget,
                _interDeltaBudget,
                Timers);

            // log: false — the SubAgentStreamPing handler above already logs this
            // liveness at Debug; an Info line per streamed delta would flood Seq.
            EmitActivity("the model is responding", log: false);
        });
    }

    // Bill one LLM call's token usage to the shared daily-stats sink and accumulate
    // the run totals for the completion summary log. We record at the source (here in
    // the child) rather than propagating totals up to the parent: the parent's
    // ISessionMetrics is the SAME process-wide singleton, so re-recording there would
    // double-count, and folding sub-agent tokens into the parent's UsageOutput would
    // corrupt its context-window percentage (the sub-agent has its own context window).
    private void RecordUsage(UsageDetails? usage)
    {
        if (usage is null)
            return;

        var input = usage.InputTokenCount ?? 0;
        var output = usage.OutputTokenCount ?? 0;
        _runInputTokens += input;
        _runOutputTokens += output;
        _sessionMetrics?.RecordTokenUsage(input, output);
    }

    private void HandleToolCalls(AiChatMessage assistantMessage, List<FunctionCallContent> toolCalls)
    {
        _turnState.ResetEmptyResponseGuards();

        // The child keeps the provider alias in transient history.
        // Internal dispatch and cycle state use the canonical name.
        _toolRegistry.CanonicalizeToolCalls(toolCalls);
        var executor = CreateExecutor();
        var preparedCycleBatch = ToolCycleSignatureFactory.Prepare(toolCalls, executor);
        var cycleDecision = _turnState.EvaluateBeforeDispatch(preparedCycleBatch);
        if (cycleDecision.Kind != ToolCycleDecisionKind.Execute)
        {
            Logging.GetLogger(Context.System, typeof(TurnStateTracker)).Warning(
                "Subagent tool cycle decision kind={DecisionKind} refusedCalls={RefusedCalls} repeatedCalls={RepeatedCalls}",
                cycleDecision.Kind,
                cycleDecision.RefusedCallIds.Count,
                cycleDecision.RepeatedCallIds.Count);
        }

        if (cycleDecision.RefusedRepeat == RefusedRepeatAction.EndTurn)
        {
            // The child ignored the escalated nudge (Aaron, 18 Oct 2026). The run ends with no dispatch and no
            // model request. It is a successful partial run, so the parent gets the child text and no error.
            Complete(true, LastChildText(assistantMessage), SubAgentRunOutcome.Partial,
                SubAgentOutcomeReason.ToolCycleStopped);
            return;
        }

        // A repeat of a refused call does not stop the child at once. The call is refused again, and the child
        // nudge follows the batch results. The child nudge offers no background job: a child has none.
        _activeRepeatNudge = ToolCycleMessages.ForRefusedRepeat(cycleDecision, toolCalls, childRun: true);

        // Add assistant message (with tool calls) to history
        _history.Add(assistantMessage);
        _transcript.Add(ChatMessageConverter.FromAiMessage(assistantMessage));

        _activeCycleBatch = preparedCycleBatch;
        RunTools(executor, toolCalls, cycleDecision.RefusedCallIds, answers: null);
    }

    private DispatchingToolExecutor CreateExecutor() => _toolExecutorLogger is null
        ? new DispatchingToolExecutor(_toolRegistry, _toolAccessPolicy, _approvalService)
        : DispatchingToolExecutor.CreateWithLogger(_toolRegistry, _toolAccessPolicy, _approvalService, _toolExecutorLogger);

    private void RunTools(DispatchingToolExecutor executor, List<FunctionCallContent> toolCalls,
        IReadOnlySet<string> refusedCallIds, IReadOnlyDictionary<string, ApprovalRedriveEntry>? answers)
    {
        var toolNames = string.Join(", ", toolCalls.Select(tc => tc.Name));
        RecordProgress($"running tools: {toolNames}");
        _log.Info("SubAgent [{AgentName}] calling tools: [{ToolNames}]",
            _definition.Name, toolNames);

        _toolBatchRuns = true;
        var self = Self;
        var setWorkingDirectoryTool =
            _toolRegistry.GetByName(SetWorkingDirectoryTool.ToolName) as SetWorkingDirectoryTool;
        var setWorkingDirectoryAvailable = setWorkingDirectoryTool is not null
            && _toolAccessPolicy.IsToolExposed(setWorkingDirectoryTool, ToolExecutionContext.Invocation);

        _ = ExecuteToolsAsync(
            _ownedRun,
            executor,
            toolCalls,
            ToolExecutionContext,
            _executionCts?.Token ?? CancellationToken.None,
            _externalCts?.Token ?? CancellationToken.None,
            self,
            _approvalBridge,
            _managedTemporaryCorrections.Snapshot(),
            setWorkingDirectoryAvailable,
            _log,
            _definition.Name,
            _parentSessionId,
            _subSessionId,
            _llmCallId,
            refusedCallIds,
            answers);
    }

    // Design D4: a resumed child starts from the park. It keeps the round and the loop guard (H1, M10) and runs only
    // the parked calls, each once, with its recorded answer. The gate checks each call again.
    private void ResumeFromPark(ChildRunResume resume)
    {
        var park = resume.Park;
        foreach (var message in park.Transcript)
            AddMessage(message);
        _turnState.RestoreLoopGuard(park.LoopGuard);
        _checkpointRound = park.LoopGuard.ToolIterationCount;
        _fileActivity.Restore(park.FileActivity);
        // The parked calls of the park are the calls of its last assistant message with no result. A consent park has
        // one for each request, and a drain park has none, so the child follows the same rule as the owner.
        var lastAssistant = _history.FindLastIndex(static message => message.Role == Microsoft.Extensions.AI.ChatRole.Assistant);
        var answered = _history.Skip(lastAssistant + 1).SelectMany(static message => message.Contents)
            .OfType<FunctionResultContent>().Select(static result => result.CallId).ToHashSet(StringComparer.Ordinal);
        List<FunctionCallContent> parkedCalls = lastAssistant < 0 ? [] : _history[lastAssistant].Contents.OfType<FunctionCallContent>()
            .Where(call => !answered.Contains(call.CallId)).ToList();
        switch (ChildParkRules.ResumeStart(parkedCalls.Count, resume.Answers.Count))
        {
            case ChildResumeStart.ModelCall:
                FireLlmCall();
                return;
            case ChildResumeStart.ParkedCalls when parkedCalls.All(call => resume.Answers.ContainsKey(call.CallId)):
                break;
            default:
                // The owner checks the same rule before the resume, so this is damage. The run ends loudly, and no
                // parked call runs.
                var damage = $"The resume has {resume.Answers.Count} answer(s) that do not match the {parkedCalls.Count} parked call(s) of the park.";
                _log.Error("SubAgent [{AgentName}] cannot resume: {Damage}", _definition.Name, damage);
                Complete(false, damage, SubAgentRunOutcome.Failed, SubAgentOutcomeReason.ParkDamaged);
                return;
        }
        _toolRegistry.CanonicalizeToolCalls(parkedCalls);
        RunTools(CreateExecutor(), parkedCalls, new HashSet<string>(StringComparer.Ordinal), resume.Answers);
        // Design D2: the parked calls run in the directories of the batch (RunTools took that context). A finished
        // set_working_directory call of the batch applies after them, and a parked one applies when its result comes.
        if (park.FileActivity.ProjectDirectory is { } projectDirectory
            && projectDirectory != park.ScopeDirectories.ProjectDirectory)
            ApplyProjectDirectory(projectDirectory);
    }

    // Design D1: the batch ended and one or more calls need consent. The child hands its state to the owner, waits for
    // ChildTerminalCommitted, and stops. The owner asks the run requester and starts a new incarnation after the answers.
    private void Park(IReadOnlyList<ParentApprovalRequest> requests, ChildParkScopeDirectories directories)
    {
        var owned = _ownedRun ?? throw new InvalidOperationException("Only an owned child run can park.");
        var park = new ChildRunPark
        {
            Transcript = _transcript.ToArray(), LoopGuard = _turnState.ExportLoopGuard(), ScopeDirectories = directories,
            FileActivity = _fileActivity.BuildResult(), SkillOverlay = _definition.SkillOverlay
        };
        EndRun();
        _log.Info("SubAgent [{AgentName}] parked with {Count} consent request(s) after {Iterations} tool iterations",
            _definition.Name, requests.Count, _turnState.ToolIterationCount);
        HandOver(owned, new ChildParkReached(owned.RunId, park, requests));
    }

    // A drain park holds no request. It keeps the current directories, so the resume applies no directory change.
    private void ParkForDrainNow()
        => Park([], new ChildParkScopeDirectories(ToolExecutionContext.RunScope.ProjectDirectory,
            ToolExecutionContext.RunScope.InheritedCwd));

    private void HandOver(ChildRunExecution owned, object message)
    {
        owned.Close();
        Become(() => Receive<ChildTerminalCommitted>(_ => Context.Stop(Self)));
        owned.Owner.Tell(message, Self);
    }

    private void FireLlmCall()
    {
        if (_parkForDrain)
        {
            ParkForDrainNow();
            return;
        }
        if (_ownedRun is { } owned && _turnState.ToolIterationCount > _checkpointRound)
        {
            _checkpointRound = _turnState.ToolIterationCount;
            var checkpoint = new ChildRunCheckpoint(_checkpointRound,
                $"Completed {_checkpointRound} tool rounds.", _fileActivity.BuildResult());
            _ = CommitCheckpointAsync(owned, checkpoint, Self);
            return;
        }
        // Each LLM call starts fresh on the generous prefill budget; the watchdog
        // promotes to the inter-delta budget on the first substantive delta. The
        // no-progress deadline is armed alongside it and only real tokens reset it.
        _anyContentStreamed = false;
        StartLlmCallWatchdog();
        EmitActivity("calling the model");
        var self = Self;
        var client = _chatClient;
        var messages = new List<AiChatMessage>(_history);
        var callId = ++_llmCallId;

        // Carry the parent session id (when known) so the chat-client decorators group this
        // sub-agent's LLM-pipeline lines under the spawning session in OTEL, plus the sub-session
        // id so the file-logger partitions them into the sub-agent's OWN session.log. Direct/test
        // callers leave both unset.
        ChatOptions? options = _parentSessionId is { Length: > 0 } sid
            ? new SessionScopedChatOptions { SessionId = sid, SubSessionId = _subSessionId }
            : null;
        if (_aiTools.Count > 0)
        {
            options ??= new ChatOptions();
            options.Tools = [.. _aiTools];
        }

        _log.Info(
            "SubAgent [{AgentName}] LLM call start callId={CallId} iteration={Iteration} messages={MessageCount} toolsEnabled={ToolsEnabled}",
            _definition.Name,
            callId,
            _turnState.ToolIterationCount,
            messages.Count,
            options?.Tools?.Count > 0);
        var token = _executionCts?.Token ?? CancellationToken.None;
        // Read the scheduler on the actor thread. Context is not valid inside the
        // owned-run continuation, which runs on the thread pool.
        var clock = Context.System.Scheduler;
        if (_ownedRun is { } ownedRun)
            _ = ownedRun.Enter(async () => { await InvokeLlmAsync(client, messages, options, self, clock, callId, token); return true; });
        else
            _ = InvokeLlmAsync(client, messages, options, self, clock, callId, token);
    }

    private static async Task CommitCheckpointAsync(ChildRunExecution owned, ChildRunCheckpoint checkpoint,
        IActorRef self)
    {
        try
        {
            await owned.Owner.Ask<SessionProtocol.CommandAck>(
                new ChildCheckpointReached(owned.RunId, checkpoint, self), Timeout.InfiniteTimeSpan, owned.Token);
            self.Tell(new ChildCheckpointCommitted());
        }
        catch (Exception error) when (!FatalExceptionPolicy.IsFatal(error))
        { self.Tell(SubAgentCancelled.Instance); }
    }

    private IReadOnlyList<AITool> ResolveCoreAiTools()
    {
        var tools = _toolRegistry.GetCoreRegistrations().Select(static registration => registration.Tool);
        return _toolAccessPolicy
            .FilterDiscoverableTools(tools, ToolExecutionContext.Invocation)
            .Select(tool => tool.ToAITool())
            .ToList();
    }

    private bool TryActivateDiscoveredTool(string toolName)
    {
        var registration = _toolRegistry.GetRegistrationByToolName(toolName);
        if (registration is null
            || !SubAgentToolPolicy.IsAllowedForSubAgent(registration.Tool.Name)
            || !_toolAccessPolicy.IsToolExposed(registration.Tool, ToolExecutionContext.Invocation))
        {
            return false;
        }

        var tool = registration.Tool;
        if (_toolRegistry.IsCoreTool(tool.Name))
            return true;

        if (_toolExposure.AddIfMissing(tool.ToAITool()))
        {
            _loadedDeferredToolNames.Add(tool.Name);
            _log.Info(
                "SubAgent deferred tool activated loaded={LoadedCount}",
                _loadedDeferredToolNames.Count);
        }

        return true;
    }

    private void LogToolExposure()
    {
        var visibleCount = _toolAccessPolicy.FilterDiscoverableTools(
            _toolRegistry.GetAllRegistrations().Select(static registration => registration.Tool),
            ToolExecutionContext.Invocation).Count;
        _log.Info(
            "SubAgent tool exposure core={CoreCount} deferredVisible={DeferredVisibleCount} loaded={LoadedCount}",
            _coreToolCount,
            Math.Max(0, visibleCount - _coreToolCount),
            _loadedDeferredToolNames.Count);
    }

    private bool CanDeclareProjectScope() =>
        _aiTools.Any(tool => string.Equals(
            tool.Name,
            SetWorkingDirectoryTool.ToolName,
            StringComparison.Ordinal));

    private bool _completed;

    private void Complete(
        bool success,
        string output,
        SubAgentRunOutcome? outcome = null,
        SubAgentOutcomeReason? outcomeReason = null)
    {
        // Idempotent guard: a stale ProcessingWatchdogExpired or other handler can
        // land after Complete has already run (Tell from a thread-pool finally
        // races mailbox-thread Stop). The second call would send a duplicate
        // SubAgentResult and log a misleading "completed" line. The first
        // caller wins; subsequent calls are no-ops.
        if (!EndRun())
            return;

        var resolvedOutcome = outcome ?? (success ? SubAgentRunOutcome.Completed : SubAgentRunOutcome.Failed);

        _log.Info("SubAgent [{AgentName}] completed (success={Success}, outcome={Outcome}, reason={Reason}, output={OutputLength} chars, iterations={Iterations})",
            _definition.Name, success, resolvedOutcome, outcomeReason?.Value ?? "-", output.Length, _turnState.ToolIterationCount);

        // Log cumulative stats for observability — total LLM calls, tool usage, tokens.
        // This gives operators a single summary line for sub-agent cost/duration analysis.
        // (success is already on the "completed" line above; omitted here to stay within
        // ILoggingAdapter's 6-argument ceiling.)
        _log.Info(
            "SubAgent [{AgentName}] summary: totalToolCalls={TotalToolCalls}, "
            + "iterations={Iterations}, inputTokens={InputTokens}, outputTokens={OutputTokens}, "
            + "duration={Duration}s",
            _definition.Name, _turnState.ToolCallCount,
            _turnState.ToolIterationCount,
            _runInputTokens, _runOutputTokens,
            _runStopwatch.Elapsed.TotalSeconds);

        var findings = success && _definition.EmitStructuredFindings
            ? BuildFindings(output, ToolExecutionContext.SessionId, resolvedOutcome, outcomeReason)
            : [];

        var workingContextResult = BuildWorkingContextResult(success);
        var result = new SubAgentResult
        {
            Completion = ChildRunCompletion.FromReportedOutcome(resolvedOutcome, outcomeReason, workingContextResult),
            Output = output,
            AgentName = _definition.Name,
            Findings = findings,
            FindingsCount = findings.Count,
        };
        if (_ownedRun is { } owned)
            HandOver(owned, new ChildTerminalReturned(owned.RunId, result));
        else
        {
            _replyTo.Tell(result);
            Context.Stop(Self);
        }
    }

    private bool EndRun()
    {
        if (_completed)
            return false;
        _completed = true;
        _managedTemporaryCorrections.Clear();
        _executionCts?.Cancel();
        _externalCts?.Cancel();
        _watchdog.Stop(Timers);
        _externalCancellationRegistration.Dispose();
        _executionCts?.Dispose();
        _externalCts?.Dispose();
        _executionCts = null;
        _externalCts = null;
        return true;
    }

    /// <summary>
    /// Arm the watchdog for a fresh LLM streaming call: the generous prefill
    /// liveness budget plus the keepalive-immune no-progress deadline that bounds
    /// a backend streaming heartbeats without producing real tokens.
    /// </summary>
    private void StartLlmCallWatchdog()
        => _watchdog.Start(ProcessingWatchdog.LlmCall, _prefillBudget, Timers, _noProgressBudget);

    /// <summary>
    /// (Re)start the liveness watchdog on the given budget without a no-progress
    /// deadline — used to re-baseline between streaming calls (tool dispatch,
    /// post-approval). Clears any stale no-progress deadline from the prior call.
    /// </summary>
    private void RestartWatchdog(TimeSpan budget)
        => _watchdog.Start(ProcessingWatchdog.LlmCall, budget, Timers);

    /// <summary>
    /// Emit a liveness/progress item to the spawning tool's stream (if any) and log
    /// the phase so it is visible in Seq even on non-streaming spawn paths (where
    /// <see cref="_activitySink"/> is null), correlated by SessionId/SubSessionId.
    /// The per-delta streaming ping passes <paramref name="log"/> = false because the
    /// <see cref="SubAgentStreamPing"/> handler already logs that liveness at Debug —
    /// logging it here too would emit one Info line per streamed delta.
    /// </summary>
    private void EmitActivity(string phase, bool log = true)
    {
        if (log)
            _log.Info("SubAgent [{AgentName}] {Phase}", _definition.Name, phase);

        _activitySink?.TryWrite(new ToolActivityUpdate(phase));
    }

    /// <summary>
    /// Record forward progress in the tool loop / post-approval: re-baseline the
    /// inter-delta watchdog and emit activity. Uses <see cref="RestartWatchdog"/>
    /// (Start, not Refresh) so it still arms after an approval wait stopped the
    /// watchdog entirely.
    /// </summary>
    private void RecordProgress(string phase)
    {
        RestartWatchdog(_interDeltaBudget);
        EmitActivity(phase);
    }

    private List<SubAgentFinding> BuildFindings(
        string output,
        string? sessionId,
        SubAgentRunOutcome outcome,
        SubAgentOutcomeReason? outcomeReason)
    {
        var content = output?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(content))
            return [];

        if (content.Length < 30)
            return [];

        var confidence = outcome == SubAgentRunOutcome.Partial ? 0.6 : 0.65;
        var policy = _policyEvaluator.EvaluateWrite(
            sensitivity: SubAgentFindingSensitivity.Normal.ToWireValue(),
            recallMode: SubAgentFindingRecallMode.Auto.ToWireValue(),
            confidence,
            isExplicitRequest: false);

        if (!policy.Allowed)
            return [];

        var evidence = new List<string> { $"subagent_outcome:{outcome.ToString().ToLowerInvariant()}" };
        if (outcomeReason is { } reason)
            evidence.Add($"subagent_outcome_reason:{reason.Value}");

        return
        [
            new SubAgentFinding
            {
                Shape = SubAgentFindingShape.Conclusion,
                Title = $"subagent:{_definition.Name}",
                Content = content,
                Kind = "record",
                Sensitivity = SubAgentFindingSensitivity.Normal,
                RecallMode = SubAgentFindingRecallMode.Searchable,
                UpdateSemantics = "append-document",
                Confidence = confidence,
                Durability = SubAgentFindingDurability.Durable,
                Reusability = SubAgentFindingReusability.Reusable,
                Evidence = evidence
            }
        ];
    }

    private void AddSystemNudge(string nudge)
        => AddMessage(new SerializableChatMessage { Role = Protocol.ChatRole.User, Content = $"[system: {nudge}]" });

    private void AddMessage(SerializableChatMessage message)
    {
        // reinjectMeta: the model wrote the timeout and background hints in its call, and the serializable form strips
        // them. Put them back, so the history matches the model message and a resumed parked call keeps its hints.
        _history.Add(ChatMessageConverter.ToAiMessage(message, ToolExecutionContext.SessionDirectory, reinjectMeta: true));
        _transcript.Add(message);
    }

    private void AddModelInputMediaNudge(IReadOnlyList<SerializableMediaReference> mediaReferences)
    {
        if (mediaReferences.Count == 0)
            return;

        var itemText = mediaReferences.Count == 1 ? "file" : "files";
        var message = new SerializableChatMessage
        {
            Role = Protocol.ChatRole.User,
            Content = $"[system: A tool loaded {mediaReferences.Count} media {itemText} for model-visible inspection. " +
                      "Use the attached media along with the tool result to complete the delegated task.]",
            MediaReferences = mediaReferences
        };
        AddMessage(message);
    }

    /// <summary>
    /// Compose the initial user message for the subagent. When <paramref name="runtimeContext"/>
    /// is present, prefixes a <c>Context:</c> block ahead of the <c>Task:</c> block so the
    /// parent-supplied background stays visually separated from the agent's task.
    /// When runtime context is null or whitespace, returns the raw task string for backward
    /// compatibility with the pre-Context protocol.
    /// </summary>
    private static string BuildUserMessage(string? runtimeContext, string workingContext, string task)
    {
        var contextParts = new[] { runtimeContext?.Trim(), workingContext.Trim() }
            .Where(part => !string.IsNullOrWhiteSpace(part));
        var combinedContext = string.Join("\n\n", contextParts);
        if (combinedContext.Length == 0)
            return task;

        return $"Context:\n{combinedContext}\n\nTask:\n{task}";
    }

    private static string BuildModelContext(
        WorkingContextSnapshot snapshot,
        TrustAudience audience,
        SessionStoragePaths? storage)
    {
        if (audience == TrustAudience.Public)
            return string.Empty;

        var workingContext = snapshot.ToContextBlock();
        var sessionContext = storage is null
            ? string.Empty
            : SessionContextFormatter.Format(storage);

        return string.Join(
            "\n\n",
            new[] { workingContext, sessionContext }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private WorkingContextDelta? BuildWorkingContextResult(bool success)
    {
        if (!success)
            return null;

        return _fileActivity.BuildResult();
    }

    private void TryApplyProjectDirectory(string? toolName, ToolInvocationReceipt? receipt)
    {
        if (toolName is not SetWorkingDirectoryTool.ToolName
            || receipt is not ToolInvocationReceipt.Succeeded
            {
                DeclaredProjectDirectory: { } projectDirectory
            })
        {
            return;
        }

        ApplyProjectDirectory(projectDirectory);
    }

    private void ApplyProjectDirectory(string projectDirectory)
    {
        var nextScope = ToolExecutionContext.RunScope with
        {
            ProjectDirectory = projectDirectory
        };
        _toolExecutionContext = new ToolExecutionContext(
            nextScope,
            ToolExecutionContext.ExecutionTimeout);
        _fileActivity.SetProjectDirectory(projectDirectory);
        _projectInstructions = _promptProvider.GetProjectInstructions(
            nextScope.Audience,
            projectDirectory);

        var prompt = BuildSystemPrompt(
            _definition,
            _projectInstructions,
            CanDeclareProjectScope());
        var systemMessage = new AiChatMessage(Microsoft.Extensions.AI.ChatRole.System, prompt);
        if (_history.Count > 0 && _history[0].Role == Microsoft.Extensions.AI.ChatRole.System)
            _history[0] = systemMessage;
        else
            _history.Insert(0, systemMessage);

        _log.Info("SubAgent [{AgentName}] project directory set to {ProjectDir}",
            _definition.Name,
            projectDirectory);
    }

    // The last text that the child model wrote in this run: the text of the final response, or else the latest
    // earlier assistant text. The parent did not see the earlier text, so the child returns it. Empty when none.
    private string LastChildText(AiChatMessage finalResponse)
        => new[] { finalResponse }
            .Concat(Enumerable.Reverse(_history).Where(static message => message.Role == Microsoft.Extensions.AI.ChatRole.Assistant))
            .Select(ExtractText)
            .FirstOrDefault(static text => text != EmptyResponseMarker && !string.IsNullOrWhiteSpace(text))
            ?? string.Empty;

    private static string ExtractText(AiChatMessage message)
    {
        var sb = new StringBuilder();
        foreach (var content in message.Contents)
        {
            if (content is TextContent text && !string.IsNullOrEmpty(text.Text))
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(text.Text);
            }
        }

        return sb.Length > 0 ? sb.ToString() : EmptyResponseMarker;
    }

    // ── Static async helpers (same pattern as LlmSessionActor) ──

    internal static Task InvokeLlmAsync(
        IChatClient client,
        List<AiChatMessage> messages,
        ChatOptions? options,
        IActorRef self,
        ITimeProvider clock,
        CancellationToken ct)
        => InvokeLlmAsync(client, messages, options, self, clock, callId: 0, ct);

    internal static async Task InvokeLlmAsync(
        IChatClient client,
        List<AiChatMessage> messages,
        ChatOptions? options,
        IActorRef self,
        ITimeProvider clock,
        long callId,
        CancellationToken ct)
    {
        try
        {
            // Use streaming to match the main session path. The non-streaming
            // GetResponseAsync path drops reasoning content for some providers
            // (e.g., Qwen emits <think> blocks that surface as TextReasoningContent
            // only in streaming mode), leaving the assistant message with no
            // TextContent and causing the subagent to report empty "(no response)".
            // The loop, counting, and response assembly are shared with the main
            // session path via StreamingResponseReader.
            var pingGate = new StreamPingGate(clock, StreamPingInterval);

            var result = await StreamingResponseReader.ReadAsync(
                client,
                messages,
                options,
                (_, cls, diag) =>
                {
                    // Always surface the first substantive delta promptly so the
                    // watchdog can promote off the generous prefill budget;
                    // otherwise throttle pings. Keepalives are forwarded too (the
                    // actor refreshes whichever budget is in force) so a healthy
                    // slow prefill is never mistaken for a hang. The gate never
                    // drops an answer update: that update resets the thinking cap.
                    if (pingGate.TryPing(cls, out var pingActivity))
                    {
                        self.Tell(new SubAgentStreamPing(
                            callId,
                            diag.UpdateCount,
                            diag.EmptyUpdateCount,
                            diag.TextDeltaCount,
                            diag.TextChars,
                            diag.ThinkingDeltaCount,
                            diag.ThinkingChars,
                            diag.ToolCallDeltaCount,
                            diag.FinishReason,
                            Activity: pingActivity));
                    }
                },
                ct);

            self.Tell(new LlmResponseReceived
            {
                Response = result.Response,
                CallId = callId,
                StreamUpdateCount = result.Diagnostics.UpdateCount,
                EmptyStreamUpdateCount = result.Diagnostics.EmptyUpdateCount,
                StreamTextDeltaCount = result.Diagnostics.TextDeltaCount,
                StreamTextChars = result.Diagnostics.TextChars,
                StreamThinkingDeltaCount = result.Diagnostics.ThinkingDeltaCount,
                StreamThinkingChars = result.Diagnostics.ThinkingChars,
                StreamToolCallDeltaCount = result.Diagnostics.ToolCallDeltaCount
            });
        }
        catch (Exception ex)
        {
            self.Tell(new LlmCallFailed(ex) { CallId = callId });
        }
    }

    private static string FormatToolCallPreview(IEnumerable<FunctionCallContent> toolCalls)
    {
        var summaries = toolCalls.Select(tc =>
            $"{tc.Name}#{tc.CallId}({PreviewArguments(tc.Arguments)})");
        return string.Join("; ", summaries);
    }

    private static string PreviewArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return "no-args";

        var rendered = string.Join(", ", arguments.Select(kvp =>
            $"{kvp.Key}={PreviewForLog(kvp.Value?.ToString() ?? "null", 160)}"));
        return PreviewForLog(rendered, 500);
    }

    // The no-progress and thinking-cap timers arm only when a budget exists. An expiry
    // without a budget is a bug, so it must fail loudly and not report a zero-second timeout.
    private TimeSpan RequireNoProgressBudget()
        => _noProgressBudget
           ?? throw new InvalidOperationException(
               "A no-progress watchdog expiry arrived, but the sub-agent has no no-progress budget.");

    // Keeps the whole cut-off answer text, so the parent can use the partial work. The
    // text takes the same redaction as the preview. A write failure is logged and returns
    // null: it must not hide the output_truncated failure.
    private string? SaveTruncatedOutput(string? stub)
    {
        if (string.IsNullOrWhiteSpace(stub))
            return null;

        try
        {
            var storage = ToolExecutionContext.SessionStorage
                ?? throw new InvalidOperationException("The child run has no session storage.");
            var path = Path.Combine(storage.ArtifactDirectory.Value, TruncatedOutputArtifactName);
            // Redact the real text, so the file keeps its line breaks. The escaped pass catches a secret
            // that spans two lines. It replaces the body only when it found one.
            var single = SecretOutputRedactor.Redact(stub);
            var escaped = single.ReplaceLineEndings("\\n");
            var escapedRedacted = SecretOutputRedactor.Redact(escaped);
            var redacted = escapedRedacted == escaped ? single : escapedRedacted;
            var content = "# Partial output (cut off at the model output limit)\n\n"
                + "This file is partial. The model reached its output limit, so the text ends early. It is not a result.\n\n"
                + redacted + "\n";
            ChildRunDocuments.WriteAtomic(path, content);
            return path;
        }
        catch (Exception ex) when (!FatalExceptionPolicy.IsFatal(ex))
        {
            _log.Warning(ex, "SubAgent [{AgentName}] could not save its truncated output as an artifact", _definition.Name);
            return null;
        }
    }

    internal static string PreviewForLog(string? text, int maxChars)
        => PreviewRedacted(text, maxChars);

    // Redaction sees the whole text first. A cut before redaction can leave a prefix of a
    // secret that no pattern matches. The first pass must come before the line break
    // escape: an escaped break puts the letter "n" before a secret at the start of a line,
    // and a rule that starts with a word boundary then misses it. The second pass must
    // come after the escape: it joins a key at the end of a line to its value on the next
    // line ("API_KEY=\n<value>"), and then a rule can match the whole pair. The cut falls
    // on a text-element boundary, so it never splits a surrogate pair. The cut can still
    // shorten a secret mask, which is safe.
    private static string PreviewRedacted(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var escaped = SecretOutputRedactor.Redact(SecretOutputRedactor.Redact(text).ReplaceLineEndings("\\n"));
        if (escaped.Length <= maxChars)
            return escaped;

        var end = 0;
        var elements = StringInfo.GetTextElementEnumerator(escaped);
        while (elements.MoveNext())
        {
            var next = elements.ElementIndex + ((string)elements.Current).Length;
            if (next > maxChars)
                break;
            end = next;
        }

        return escaped[..end] + "...";
    }

    internal static bool ContainsUnexecutedToolCallMarkup(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var outsideQuotedExamples = StripFencedAndQuotedBlocks(text);
        var lines = outsideQuotedExamples
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in lines)
        {
            if (line.StartsWith("<tool_call", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("</tool_call>", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (line.StartsWith("<function=", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("<parameter=", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("</function>", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("</parameter>", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string StripFencedAndQuotedBlocks(string text)
    {
        var builder = new StringBuilder(text.Length);
        var inFence = false;

        foreach (var rawLine in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var trimmed = rawLine.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal)
                || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence || trimmed.StartsWith('>'))
                continue;

            builder.AppendLine(rawLine);
        }

        return builder.ToString();
    }

    private static async Task ExecuteToolsAsync(
        ChildRunExecution? ownedRun,
        IToolExecutor executor,
        List<FunctionCallContent> toolCalls,
        ToolExecutionContext executionContext,
        CancellationToken ct,
        CancellationToken externalCt,
        IActorRef self,
        IParentApprovalBridge? approvalBridge,
        ManagedTemporaryCorrectionDispatch managedTemporaryCorrections,
        bool setWorkingDirectoryAvailable,
        ILoggingAdapter logger,
        AgentName agentName,
        string? parentSessionId,
        string? subSessionId,
        long callId,
        IReadOnlySet<string> refusedCallIds,
        IReadOnlyDictionary<string, ApprovalRedriveEntry>? answers)
    {
        try
        {
            var modelInputBudget = new ModelInputBatchBudget(ChannelAttachmentPolicy.DefaultMaxFileBytes);
            var tasks = toolCalls.Select(async tc =>
            {
                // Same execution-preflight seam as the main pipeline: validate +
                // extract in one step (the sub-agent previously skipped extraction
                // entirely, silently dropping timeout hints). meta.Background is
                // intentionally not consumed here — sub-agents have no
                // background-job manager. meta.Rationale goes only to the
                // "Invoking tool" log line. Only the timeout hint is materialized
                // into the immutable per-tool context.
                var interpretation = executor.InterpretToolCall(tc);
                var toolContext = CreatePerToolExecutionContext(executionContext, interpretation.Meta);
                logger.Info(
                    "SubAgent [{AgentName}] authorization attempt started " +
                    "authorizationAttemptId={AuthorizationAttemptId} sessionId={SessionId} " +
                    "subSessionId={SubSessionId} callId={CallId} toolName={ToolName}",
                    agentName,
                    toolContext.Approval.AuthorizationAttemptId.Value,
                    parentSessionId,
                    subSessionId,
                    tc.CallId,
                    tc.Name);
                // Same line and same redaction helper as SessionToolExecutionPipeline.
                // The OTLP exporter sends it off the host.
                if (logger.IsInfoEnabled)
                {
                    logger.Info(
                        "Invoking tool [{ToolName}] (call={CallId}) args={Args} rationale={Rationale} " +
                        "authorizationAttemptId={AuthorizationAttemptId} sessionId={SessionId}",
                        tc.Name,
                        tc.CallId,
                        ToolCallLogFormatter.FormatArguments(tc.Arguments),
                        ToolCallLogFormatter.FormatRationale(interpretation.Meta?.Rationale),
                        toolContext.Approval.AuthorizationAttemptId.Value,
                        parentSessionId);
                }
                if (interpretation.Rejection is { } rejection)
                {
                    var refused = refusedCallIds.Contains(tc.CallId);
                    toolContext.Outputs.TryComplete(refused
                        ? new ToolInvocationReceipt.Correction(ToolRemediationCode.BreakToolCycle)
                        : new ToolInvocationReceipt.OtherOutcome(ToolInvocationOutcomeCategory.InvalidInput));
                    var message = refused ? $"{rejection.Message}\n{ToolCycleMessages.Correction}" : rejection.Message;
                    return BuildToolResult(tc, message, toolContext, modelInputBudget);
                }

                if (refusedCallIds.Contains(tc.CallId))
                    return new SubAgentToolCallResult(
                        new SerializableChatMessage
                        {
                            Role = Protocol.ChatRole.Tool,
                            Content = ToolCycleMessages.Correction,
                            ToolCallId = new ToolCallId(tc.CallId),
                            Name = tc.Name
                        }, [], null,
                        new ToolInvocationReceipt.Correction(ToolRemediationCode.BreakToolCycle),
                        null, toolContext.Approval.AuthorizationAttemptId);

                // A resumed parked call (design D4): a refused call never dispatches. An approved call gets its
                // one-time consent, and the gate still checks it.
                if (answers is not null && answers.TryGetValue(tc.CallId, out var answer))
                {
                    if (answer.TryGetRefusalText(tc.Name, out var refusalText))
                    {
                        toolContext.Outputs.TryComplete(
                            new ToolInvocationReceipt.OtherOutcome(ToolInvocationOutcomeCategory.AccessDenied));
                        return BuildToolResult(tc, refusalText, toolContext, modelInputBudget);
                    }
                    answer.ApplyTo(toolContext.Approval, tc.Name);
                }

                var meta = interpretation.Meta;
                var cleanedTc = interpretation.Cleaned;
                var managedTemporaryCall = ManagedTemporaryCorrection.BuildCallSemantics(
                    cleanedTc,
                    meta,
                    toolContext.ExecutionTimeout.Value,
                    executor is IApprovalShellProvider shellProvider
                        ? shellProvider.Shell
                        : ApprovalShell.Bash);
                ManagedTemporaryCorrectionKey? consumedManagedTemporaryKey = null;
                if (managedTemporaryCall is { } call
                    && managedTemporaryCorrections.TryConsume(call, out var correctionKey))
                {
                    consumedManagedTemporaryKey = correctionKey;
                    toolContext.Approval.MarkManagedTemporaryRetry(correctionKey.Target);
                }
                try
                {
                    var result = await (ownedRun is null ? executor.ExecuteAsync(tc, toolContext, ct)
                        : ownedRun.Enter(() => executor.ExecuteAsync(tc, toolContext, ct)));
                    return BuildToolResult(
                        cleanedTc,
                        result,
                        toolContext,
                        modelInputBudget,
                        consumedManagedTemporaryKey is { } directConsumed
                            ? new ManagedTemporaryCorrectionChange.Consume(directConsumed)
                            : null);
                }
                catch (ToolCorrectionRequiredException correctionEx)
                {
                    var delivery = ToolCorrectionDelivery.Create(
                        correctionEx.Corrections,
                        managedTemporaryCall);
                    toolContext.Outputs.TryComplete(delivery.Receipt);
                    return BuildToolResult(
                        cleanedTc,
                        delivery.Content,
                        toolContext,
                        modelInputBudget,
                        delivery.ManagedTemporaryStateChange,
                        delivery.NativeTool is { } nativeTool
                            ? new ToolExposureRequest(nativeTool)
                            : null);
                }
                // Design D1: an owned child parks a call that needs consent. The call does not run now. The other
                // calls of the batch finish, and the owner asks the run requester.
                catch (ToolApprovalRequiredException approvalEx) when (approvalBridge is not null && ownedRun is not null)
                {
                    var request = new ParentApprovalRequest(toolContext.Approval.AuthorizationAttemptId,
                        new ToolCallId(tc.CallId), tc.Name, approvalEx.ApprovalContext);
                    return new SubAgentToolCallResult(new SerializableChatMessage
                    {
                        Role = Protocol.ChatRole.Tool, ToolCallId = request.CallId, Name = tc.Name
                    }, [], null, null, null, request.AuthorizationAttemptId) { Parked = request };
                }
                // A child without an approval bridge never needs consent: the authorizer denies the call first
                // (approval_required_unattended, D2). A child with no owner cannot park. A request here is a defect:
                // log it and deny the call.
                catch (ToolApprovalRequiredException)
                {
                    logger.Error(
                        "SubAgent tool {0} asked for consent with no approval bridge or no owner; denied as a defect.",
                        tc.Name);
                    toolContext.Outputs.TryComplete(
                        new ToolInvocationReceipt.OtherOutcome(ToolInvocationOutcomeCategory.AccessDenied));
                    return BuildToolResult(
                        tc,
                        ToolAuthorizer.ConsentWithoutBridgeResult(tc.Name),
                        toolContext,
                        modelInputBudget,
                        consumedManagedTemporaryKey is { } defectConsumed
                            ? new ManagedTemporaryCorrectionChange.Consume(defectConsumed)
                            : null);
                }
                catch (OperationCanceledException) when (externalCt.IsCancellationRequested)
                {
                    // External cancellation is not a tool error — let it propagate
                    // so the outer try routes it via ToolExecutionFailed instead of
                    // synthesising a fake tool result that the LLM would see as
                    // success-with-error and continue from. SubAgentCancelled is
                    // also enqueued via msg.Cancellation.Register, so Complete
                    // typically wins the race; this guard covers the case where
                    // it doesn't.
                    throw;
                }
                catch (Exception ex)
                {
                    var category = ex switch
                    {
                        ToolAccessDeniedException => ToolInvocationOutcomeCategory.AccessDenied,
                        UnauthorizedAccessException => ToolInvocationOutcomeCategory.AccessDenied,
                        FileNotFoundException or DirectoryNotFoundException => ToolInvocationOutcomeCategory.NotFound,
                        _ => ToolInvocationOutcomeCategory.TransientFailure
                    };
                    toolContext.Outputs.TryComplete(new ToolInvocationReceipt.OtherOutcome(category));
                    var error = ex is ToolAccessDeniedException denied
                        ? denied.ToAgentResult()
                        : $"Error: {ex.Message}";
                    return BuildToolResult(
                        tc,
                        error,
                        toolContext,
                        modelInputBudget,
                        consumedManagedTemporaryKey is { } failedConsumed
                            ? new ManagedTemporaryCorrectionChange.Consume(failedConsumed)
                            : null);
                }
            });

            var results = await Task.WhenAll(tasks);
            for (var i = 0; i < results.Length; i++)
            {
                var result = results[i];
                results[i] = result with
                {
                    Message = ToolRemediationPresenter.Present(
                        result.Message,
                        result.Receipt,
                        setWorkingDirectoryAvailable)
                };
            }
            self.Tell(new ToolExecutionCompleted(callId, results));
        }
        catch (Exception ex)
        {
            self.Tell(new ChildToolExecutionFailed(callId, ex));
        }
    }

    private static ToolExecutionContext CreatePerToolExecutionContext(
        ToolExecutionContext source,
        ToolCallMeta? meta)
    {
        var timeout = meta?.TimeoutHintSeconds is { } seconds
            ? new ToolExecutionTimeout(TimeSpan.FromSeconds(seconds))
            : source.ExecutionTimeout;
        return new ToolExecutionContext(source.RunScope, timeout, source.Outputs.Fork());
    }

    private static SubAgentToolCallResult BuildToolResult(
        FunctionCallContent toolCall,
        string resultText,
        ToolExecutionContext toolContext,
        ModelInputBatchBudget modelInputBudget,
        ManagedTemporaryCorrectionChange? managedTemporaryCorrectionUpdate = null,
        ToolExposureRequest? exposureRequest = null)
    {
        var materialization = MaterializeModelInputFiles(toolContext, modelInputBudget);
        if (materialization.RequestedCount > materialization.MediaReferences.Count)
        {
            resultText = SessionToolExecutionPipeline.AppendModelInputHandoffWarning(
                resultText,
                materialization.RequestedCount - materialization.MediaReferences.Count);
        }

        var receipt = toolContext.Receipt
            ?? new ToolInvocationReceipt.Succeeded([], null);
        return new SubAgentToolCallResult(
            new SerializableChatMessage
            {
                Role = Protocol.ChatRole.Tool,
                Content = resultText,
                ToolCallId = new ToolCallId(toolCall.CallId),
                Name = toolCall.Name
            },
            materialization.MediaReferences,
            managedTemporaryCorrectionUpdate,
            receipt,
            exposureRequest,
            toolContext.Approval.AuthorizationAttemptId);
    }

    private static ModelInputMaterializationResult MaterializeModelInputFiles(
        ToolExecutionContext toolContext,
        ModelInputBatchBudget modelInputBudget)
    {
        if (toolContext.Outputs.ModelInputFiles.Count == 0)
            return new ModelInputMaterializationResult([], 0);

        if (string.IsNullOrWhiteSpace(toolContext.SessionDirectory))
            return new ModelInputMaterializationResult([], toolContext.Outputs.ModelInputFiles.Count);

        return SessionToolExecutionPipeline.MaterializeModelInputFiles(
            toolContext,
            toolContext.SessionDirectory,
            NoLogger.Instance,
            modelInputBudget);
    }

    private sealed record SubAgentToolCallResult(
        SerializableChatMessage Message,
        IReadOnlyList<SerializableMediaReference> ModelInputMediaReferences,
        ManagedTemporaryCorrectionChange? ManagedTemporaryCorrectionUpdate,
        ToolInvocationReceipt? Receipt,
        ToolExposureRequest? ExposureRequest,
        AuthorizationAttemptId AuthorizationAttemptId)
    {
        /// <summary>The consent request of a parked call. A parked call has no result yet.</summary>
        public ParentApprovalRequest? Parked { get; init; }
    }

    private sealed record ChildToolExecutionFailed(long CallId, Exception Cause) : INoSerializationVerificationNeeded;

    private sealed record ToolExecutionCompleted : INoSerializationVerificationNeeded
    {
        public ToolExecutionCompleted(long modelCallId, SubAgentToolCallResult[] results)
        {
            var callIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var result in results)
            {
                var callId = result.Message.ToolCallId
                    ?? throw new InvalidOperationException("A child tool result requires a call identity.");
                // Validate before mailbox delivery so the normal task can report a malformed batch.
                ArgumentNullException.ThrowIfNull(callId.Value);
                if (!callIds.Add(callId.Value))
                    throw new ArgumentException("A child tool result has a duplicate call identity.", nameof(results));
            }

            CallId = modelCallId;
            Results = results;
        }

        public long CallId { get; }
        public SubAgentToolCallResult[] Results { get; }
    }

    private string BuildSystemPrompt(
        SubAgentDefinition definition,
        string? projectInstructions,
        bool canDeclareProjectScope)
    {
        // Assemble the identity stack that sub-agents inherit from the parent session:
        // 1. Embedded operating core + deployment AGENTS.md mission playbook
        // 2. Project instructions (workspace/domain context from the parent's working directory)
        var basePrompt = SystemPromptAssembler.Assemble(
            agents: definition.OperatingRules,
            projectInstructions: projectInstructions);

        // Append the sub-agent's own system prompt (markdown body + optional skill overlay)
        var agentPrompt = string.IsNullOrWhiteSpace(definition.SkillOverlay)
            ? definition.SystemPrompt
            : string.Concat(definition.SystemPrompt.TrimEnd(), "\n\n[Skill Overlay]\n", definition.SkillOverlay.Trim());
        var rolePrompt = string.IsNullOrWhiteSpace(basePrompt)
            ? agentPrompt
            : string.Concat(basePrompt.TrimEnd(), "\n\n", agentPrompt);
        var toolIndex = _toolRegistry.GenerateCompressedIndex(
            ToolExecutionContext.Audience,
            _toolAccessPolicy);
        var roleWithTools = string.IsNullOrWhiteSpace(toolIndex)
            ? rolePrompt
            : string.Concat(rolePrompt.TrimEnd(), "\n\n", toolIndex.TrimEnd());

        // Append the headless execution and precedence contract — always at the bottom,
        // after the specialized role prompt it protects from inherited mission conflicts.
        var projectScopeContract = canDeclareProjectScope
            ? string.Concat(ProjectScopeDeclarationContract, "\n")
            : string.Empty;
        return string.Concat(
            roleWithTools.TrimEnd(),
            "\n\n",
            HeadlessExecutionContractPrefix,
            projectScopeContract,
            HeadlessExecutionContractSuffix);
    }

    private sealed class ChildFileActivityTracker
    {
        private readonly HashSet<string> _readFiles = new(StringComparer.Ordinal);
        private readonly HashSet<string> _confirmedChangedFiles = new(StringComparer.Ordinal);
        private WorkingContext _workingContext;

        public ChildFileActivityTracker(WorkingContext parentSnapshot)
        {
            _workingContext = parentSnapshot;
        }

        public void Apply(ToolInvocationReceipt? receipt)
        {
            if (receipt is not ToolInvocationReceipt.Succeeded success)
                return;

            foreach (var activity in success.FileActivity)
            {
                _workingContext = _workingContext.AddRecentFile(activity.CanonicalPath);
                if (activity.Kind == ToolFileActivityKind.Read)
                    _readFiles.Add(activity.CanonicalPath);
                else
                    _confirmedChangedFiles.Add(activity.CanonicalPath);
            }
        }

        public void SetProjectDirectory(string projectDirectory)
            => _workingContext = _workingContext.WithProjectDirectory(projectDirectory);

        // A resumed child continues the file activity of its park (design D2).
        public void Restore(WorkingContextDelta activity)
        {
            _workingContext = _workingContext.WithProjectDirectory(activity.ProjectDirectory);
            _readFiles.UnionWith(activity.ReadFiles);
            _confirmedChangedFiles.UnionWith(activity.ConfirmedChangedFiles);
        }

        public WorkingContextDelta BuildResult() => new()
        {
            ProjectDirectory = _workingContext.ProjectDirectory,
            ReadFiles = _readFiles.Order(StringComparer.Ordinal).ToArray(),
            ConfirmedChangedFiles = _confirmedChangedFiles.Order(StringComparer.Ordinal).ToArray(),
            ObservedChangedFiles = []
        };

    }

    private sealed class SubAgentCancelled
    {
        public static readonly SubAgentCancelled Instance = new();
        private SubAgentCancelled() { }
    }

    /// <summary>
    /// Throttled self-message: the streaming LLM call is still alive.
    /// <see cref="Activity"/> is <see cref="StreamActivity.Keepalive"/> for content-free
    /// keepalives, so the watchdog refreshes the current budget on them but only
    /// promotes off the prefill budget on real output. It separates thinking from
    /// answer output so the thinking cap can reset.
    /// </summary>
    private sealed record SubAgentStreamPing(
        long CallId,
        int UpdateCount,
        int EmptyUpdateCount,
        int TextDeltaCount,
        int TextChars,
        int ThinkingDeltaCount,
        int ThinkingChars,
        int ToolCallDeltaCount,
        string? FinishReason,
        StreamActivity Activity);

    // ── Reuse LlmSessionActor's internal message types ──
    // These are internal to Netclaw.Actors so accessible here.

    // LlmResponseReceived, LlmCallFailed, ToolExecutionCompleted, ToolExecutionFailed
    // are defined in Sessions/LlmMessages.cs
}

// -----------------------------------------------------------------------
// <copyright file="SessionModelContext.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Akka.Actor;
using Akka.Event;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Netclaw.Actors.Sessions;

internal sealed record RecallAudit(AutomaticRecallResult Result, long DurationMs);

internal sealed record ToolExposureDiagnostic(int Core, int DeferredVisible, int Loaded);

internal sealed record PreparedModelRequest(
    SessionState State,
    List<AiChatMessage> Messages,
    SessionScopedChatOptions Options,
    ToolExposureDiagnostic? Exposure);

internal abstract record ContextCall
{
    internal sealed record Ready(PreparedModelRequest Request) : ContextCall;
    internal sealed record Inspect(Task<INoSerializationVerificationNeeded> Work, RecallAudit Audit) : ContextCall;
}

/// <summary>
/// Owns the transient sequence that prepares model context for one session.
/// The actor owns durable state, call lifetime, and admitted authority.
/// </summary>
internal sealed class SessionModelContext
{
    private readonly SessionId _sessionId;
    private readonly SessionStoragePaths _storage;
    private readonly ISystemPromptProvider _promptProvider;
    private readonly IReadOnlyList<IContextLayerProvider> _contextLayers;
    private readonly IWorkingContextSnapshotProvider _snapshots;
    private readonly IMemoryRecallCoordinator _recallCoordinator;
    private readonly MemoryConfig _memoryConfig;
    private readonly DiscoveredToolCache _discoveredTools;
    private readonly ToolRegistry? _fullRegistry;
    private readonly ToolAccessPolicy? _toolAccessPolicy;
    private readonly Skills.SkillRegistry? _skillRegistry;
    private readonly ModelModality _inputModalities;
    private readonly ILoggingAdapter _log;
    private readonly SessionRecallManager _recall = new();
    private InspectionIntent? _inspection;
    private bool _startupContextInjected;
    private string? _overlay;
    private ToolExposureDiagnostic? _lastExposure;

    private sealed record InspectionIntent(
        long Generation,
        WorkingContext WorkingContext,
        string? SkillBody,
        string? RestartNotice);

    private static readonly string SkillHintText =
        "[skill-hint] Before answering any technical or knowledge question, scan [available-skills] for a relevant skill and call skill_load(name=\"...\"). " +
        "Skills contain user-specific preferences and project conventions that override your general knowledge — even if you already know a topic, the user may have standards defined in a skill. " +
        "When in doubt, load — a redundant load is cheap, a missed skill is not.";

    // The stop note (#2633): the first call of a person's turn holds it after a person input ended an automation turn.
    // It is fixed text with no kind label, because a webhook sets its own source kind. Keep it one literal: the eval
    // automation_stop_note_person_first reads it from this source.
    internal const string AutomationStopNote = "The automated turn above stopped unfinished because a person sent a message. Answer the person's message first, and do not resume the stopped work in this reply unless the person asks for it.";

    internal SessionModelContext(
        SessionId sessionId,
        SessionStoragePaths storage,
        SessionServices services,
        SessionMemoryServices? memory,
        SessionToolServices? tools,
        DiscoveredToolCache discoveredTools,
        ModelModality inputModalities,
        ILoggingAdapter log)
    {
        _sessionId = sessionId;
        _storage = storage;
        _promptProvider = services.PromptProvider;
        _contextLayers = services.ContextLayers;
        _snapshots = services.WorkingContextSnapshots;
        _recallCoordinator = memory?.RecallCoordinator ?? NullMemoryRecallCoordinator.Instance;
        _memoryConfig = memory?.MemoryConfig ?? new MemoryConfig();
        _discoveredTools = discoveredTools;
        _fullRegistry = tools?.ToolRegistry;
        _toolAccessPolicy = tools?.AccessPolicy;
        _skillRegistry = tools?.SkillRegistry;
        _inputModalities = inputModalities;
        _log = log;
    }

    internal SessionState RefreshPrompt(SessionState state, TrustAudience audience)
    {
        var content = _promptProvider.GetSystemPrompt(audience, state.WorkingContext.ProjectDirectory);
        if (string.IsNullOrWhiteSpace(content))
        {
            // The recovery prompt retains identity when its source files disappear.
            if (state.History.Count > 0 && state.History[0].Role == Protocol.ChatRole.System)
                _log.Warning("Identity files missing — retaining last-known system prompt from recovery");
            else
                _log.Info("No system prompt layers available");
            return state;
        }

        // Equal bytes keep the immutable history spine and the model's cache prefix.
        if (state.History.Count > 0
            && state.History[0].Role == Protocol.ChatRole.System
            && string.Equals(state.History[0].Content, content, StringComparison.Ordinal))
            return state;

        var systemMessage = new SerializableChatMessage { Role = Protocol.ChatRole.System, Content = content };
        var updated = state.History.Count > 0 && state.History[0].Role == Protocol.ChatRole.System
            ? state with { History = state.History.SetItem(0, systemMessage) }
            : state with { History = state.History.Insert(0, systemMessage) };
        _log.Info("System prompt set ({PromptLength} chars, hash={ContentHash})",
            content.Length, ShortContentHash(content));
        return updated;
    }

    internal void SetOverlay(string? text) => _overlay = string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    internal void ResetForNewTurn() => _recall.ResetForNewTurn();

    internal void ResetAfterCompaction()
    {
        _startupContextInjected = false;
        _recall.ResetForCompaction();
    }

    internal void InvalidateInspection() => _inspection = null;

    internal bool IsCurrentInspection(long generation)
        => _inspection is { Generation: var active } && generation == active;

    internal ContextCall StartCall(
        long callId,
        SessionState state,
        TurnContext? admittedTurn,
        MessageSource? sourceFallback,
        EffectiveTrustContext? toolTrust,
        string? recallQuery,
        string? skillBody,
        string? restartNotice,
        CancellationToken cancellationToken)
    {
        _inspection = null;
        if (_recall.TurnRecallCache is not null)
            return new ContextCall.Ready(PrepareRequest(state, ResolveAudience(admittedTurn, sourceFallback), toolTrust, restartNotice));

        var recallWatch = Stopwatch.StartNew();
        var resolved = _recall.ResolveForTurn(
            recallQuery, state, _sessionId, sourceFallback, _recallCoordinator, _memoryConfig, admittedTurn);
        recallWatch.Stop();
        resolved = _recall.ApplyProgressiveRecall(resolved, _log);
        var audit = new RecallAudit(resolved, recallWatch.ElapsedMilliseconds);
        var audience = ResolveAudience(admittedTurn, sourceFallback);
        var intent = new InspectionIntent(callId, state.WorkingContext, skillBody, restartNotice);
        _inspection = intent;
        return new ContextCall.Inspect(
            CreateInspectionReplyAsync(_snapshots, intent, audience, cancellationToken), audit);
    }

    internal PreparedModelRequest? CompleteInspection(
        WorkingContextSnapshotReady result,
        SessionState currentState,
        TrustAudience currentAudience,
        EffectiveTrustContext? currentToolTrust)
    {
        if (!IsCurrentInspection(result.Generation))
            return null;

        var intent = _inspection!;
        _inspection = null;
        var volatileBlock = SessionMessageAssembler.BuildVolatileContextBlock(
            BuildAssemblyInput(currentState, currentAudience, intent.SkillBody, intent.RestartNotice, result.Snapshot.ToContextBlock()));
        var updated = string.IsNullOrEmpty(volatileBlock)
            ? currentState
            : currentState.AddVolatileContextNudge(volatileBlock);
        return PrepareRequest(updated, currentAudience, currentToolTrust, intent.RestartNotice);
    }

    internal PreparedModelRequest? CompleteInspection(
        WorkingContextSnapshotFailed result,
        SessionState currentState,
        TrustAudience currentAudience,
        EffectiveTrustContext? currentToolTrust)
    {
        if (!IsCurrentInspection(result.Generation))
            return null;

        var snapshot = new WorkingContextSnapshot
        {
            WorkingContext = _inspection!.WorkingContext,
            Git = new GitWorkingContextInspection.Unavailable("working context inspection failed")
        };
        return CompleteInspection(new WorkingContextSnapshotReady(result.Generation, snapshot),
            currentState, currentAudience, currentToolTrust);
    }

    private PreparedModelRequest PrepareRequest(
        SessionState state,
        TrustAudience audience,
        EffectiveTrustContext? toolTrust,
        string? restartNotice)
    {
        var messages = SessionMessageAssembler.Assemble(
            BuildAssemblyInput(state, audience, null, restartNotice, string.Empty));
        _startupContextInjected = true;

        var exposed = ResolveExposedTools(toolTrust);
        var exposure = SelectExposureDiagnostic(exposed.Count, toolTrust);
        var options = new SessionScopedChatOptions { SessionId = _sessionId.Value };
        if (exposed.Count > 0)
            options.Tools = [.. exposed];
        return new PreparedModelRequest(state, messages, options, exposure);
    }

    private ContextAssemblyInput BuildAssemblyInput(
        SessionState state,
        TrustAudience audience,
        string? skillBody,
        string? restartNotice,
        string workingContextBlock)
        => new(
            State: state,
            ContextLayers: _contextLayers,
            StartupContextInjected: _startupContextInjected,
            SlashCommandSkillContent: skillBody,
            SessionPromptOverlay: _overlay,
            TurnRestartNotice: restartNotice,
            SessionId: _sessionId,
            Storage: _storage,
            FileReadGranted: HasFileReadAvailable(),
            ActiveRecall: _recall.TurnRecallCache,
            WorkingContextBlock: workingContextBlock,
            Audience: audience,
            SkillHint: _skillRegistry is not null && _skillRegistry.GetAll().Count > 0 ? SkillHintText : null,
            // History stores canonical names. The provider needs each wire alias.
            ToolNameToLlmFacing: _fullRegistry is null ? null : _fullRegistry.ToLlmFacingName,
            SupportedInputModalities: _inputModalities);

    private IReadOnlyList<AITool> ResolveExposedTools(EffectiveTrustContext? toolTrust)
    {
        var available = _discoveredTools.AvailableTools;
        if (_toolAccessPolicy is null || _fullRegistry is null || available.Count == 0)
            return available;
        if (toolTrust is null)
        {
            _log.Error(
                "Tool exposure refused reason={Reason} operation={Operation} candidateCount={CandidateCount}: the session has a tool access policy but no resolved trust context",
                SecurityPolicyDefaults.AudienceUnresolvedReason,
                "expose_tools",
                available.Count);
            return [];
        }

        return _toolAccessPolicy.FilterExposedTools(available, _fullRegistry, toolTrust);
    }

    private ToolExposureDiagnostic? SelectExposureDiagnostic(int exposedCount, EffectiveTrustContext? toolTrust)
    {
        if (_toolAccessPolicy is null || _fullRegistry is null || toolTrust is null)
            return null;

        var coreCount = _fullRegistry.GetCoreRegistrations().Count(registration =>
            _toolAccessPolicy.IsToolExposed(registration, toolTrust));
        var visibleCount = _fullRegistry.GetAllRegistrations().Count(registration =>
            _toolAccessPolicy.IsToolExposed(registration, toolTrust));
        var exposure = new ToolExposureDiagnostic(
            coreCount,
            Math.Max(0, visibleCount - coreCount),
            Math.Max(0, exposedCount - coreCount));
        if (_lastExposure == exposure)
            return null;
        _lastExposure = exposure;
        return exposure;
    }

    private bool HasFileReadAvailable()
    {
        foreach (var tool in _discoveredTools.AvailableTools)
        {
            if (tool is AIFunction fn && string.Equals(fn.Name, FileReadTool.ToolName, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private TrustAudience ResolveAudience(TurnContext? admittedTurn, MessageSource? sourceFallback)
        => admittedTurn?.Audience
           ?? sourceFallback?.Audience
           ?? SecurityPolicyDefaults.ResolveAudienceFromSessionId(_sessionId.Value);

    private static async Task<INoSerializationVerificationNeeded> CreateInspectionReplyAsync(
        IWorkingContextSnapshotProvider snapshots,
        InspectionIntent intent,
        TrustAudience audience,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await snapshots.CreateAsync(intent.WorkingContext, audience, cancellationToken)
                .ConfigureAwait(false);
            return new WorkingContextSnapshotReady(intent.Generation, snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new WorkingContextSnapshotCancelled(intent.Generation);
        }
        catch (Exception ex) when (!FatalExceptionPolicy.IsFatal(ex))
        {
            return new WorkingContextSnapshotFailed(intent.Generation, ex);
        }
        catch (Exception ex) when (FatalExceptionPolicy.IsFatal(ex))
        {
            return new WorkingContextSnapshotFatal(ex);
        }
    }

    private static string ShortContentHash(string content)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
    }
}

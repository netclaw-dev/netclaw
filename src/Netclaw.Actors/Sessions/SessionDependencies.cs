// -----------------------------------------------------------------------
// <copyright file="SessionDependencies.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Memory;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Core runtime services required by every session actor.
/// </summary>
public sealed record SessionServices(
    IChatClientProvider ClientProvider,
    ISystemPromptProvider PromptProvider,
    IReadOnlyList<IContextLayerProvider> ContextLayers,
    IWorkingContextSnapshotProvider WorkingContextSnapshots,
    TimeProvider TimeProvider,
    NetclawPaths Paths);

/// <summary>
/// Tool execution infrastructure. Null when the session operates without tools.
/// </summary>
public sealed record SessionToolServices(
    IToolExecutor ToolExecutor,
    ToolRegistry ToolRegistry,
    ToolAccessPolicy? AccessPolicy,
    TrustContextDeriver? TrustDeriver,
    Skills.SkillRegistry? SkillRegistry,
    IToolApprovalService? ApprovalService = null,
    SubAgentDefinitionRegistry? SubAgentRegistry = null,
    SubAgentSpawner? SubAgentSpawner = null,
    FileSubAgentDefinitionLoader? SubAgentLoader = null);

/// <summary>
/// Memory infrastructure for recall, checkpoint, and curation.
/// </summary>
public sealed record SessionMemoryServices(
    IMemoryExtractor MemoryExtractor,
    IMemoryRecallCoordinator RecallCoordinator,
    IMemoryCheckpointSink CheckpointSink,
    SQLiteMemoryStore? MemoryStore,
    MemoryConfig? MemoryConfig = null);

/// <summary>
/// Metrics and lifecycle observation.
/// </summary>
public sealed record SessionObservability(
    Telemetry.ISessionMetrics? Metrics,
    ISessionLifecycleObserver? LifecycleObserver);

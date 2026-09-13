// -----------------------------------------------------------------------
// <copyright file="SessionDependencies.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Memory;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Core runtime services required by every session actor.
/// </summary>
/// <param name="ClientProvider">Resolves the selected chat client.</param>
/// <param name="PromptProvider">Supplies the system prompt.</param>
/// <param name="ContextLayers">Supplies model context layers.</param>
/// <param name="WorkingContextSnapshots">Captures current working-context state.</param>
/// <param name="TimeProvider">Supplies testable time.</param>
/// <param name="Paths">Supplies process-wide configuration paths.</param>
/// <param name="StorageResolver">Resolves the immutable storage layout for this session.</param>
public sealed record SessionServices(
    IChatClientProvider ClientProvider,
    ISystemPromptProvider PromptProvider,
    IReadOnlyList<IContextLayerProvider> ContextLayers,
    IWorkingContextSnapshotProvider WorkingContextSnapshots,
    TimeProvider TimeProvider,
    NetclawPaths Paths,
    ISessionStorageResolver StorageResolver);

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
/// <see cref="EmbedderHolder"/> resolves the process's embedder for embed-on-write
/// (memory-core-redesign Slice 2) and for the curation evaluator's embedding kNN nominator
/// (Slice 3 Stage B, task 3.1); <see cref="VectorIndexHolder"/> resolves the nominator's
/// vector index. Null is a genuine state — same as <see cref="MemoryStore"/> being null —
/// for any session/test harness that has not wired up the embedding subsystem at all.
/// </summary>
public sealed record SessionMemoryServices(
    IMemoryExtractor MemoryExtractor,
    IMemoryRecallCoordinator RecallCoordinator,
    IMemoryCheckpointSink CheckpointSink,
    SQLiteMemoryStore? MemoryStore,
    MemoryConfig? MemoryConfig = null,
    MemoryEmbedderHolder? EmbedderHolder = null,
    MemoryVectorIndexHolder? VectorIndexHolder = null);

/// <summary>
/// Metrics and lifecycle observation.
/// </summary>
public sealed record SessionObservability(
    Telemetry.ISessionMetrics? Metrics,
    ISessionLifecycleObserver? LifecycleObserver);

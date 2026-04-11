using Netclaw.Actors.Channels;
using Netclaw.Actors.Memory;
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
    TimeProvider TimeProvider,
    NetclawPaths? Paths);

/// <summary>
/// Tool execution infrastructure. Null when the session operates without tools.
/// </summary>
public sealed record SessionToolServices(
    IToolExecutor ToolExecutor,
    IToolAuditLogger? AuditLogger,
    ToolRegistry ToolRegistry,
    ToolAccessPolicy? AccessPolicy,
    TrustContextDeriver? TrustDeriver,
    Skills.SkillRegistry? SkillRegistry,
    IToolApprovalService? ApprovalService = null);

/// <summary>
/// Memory infrastructure for recall, checkpoint, and curation.
/// </summary>
public sealed record SessionMemoryServices(
    IMemoryExtractor MemoryExtractor,
    IMemoryRecallCoordinator RecallCoordinator,
    IMemoryCheckpointSink CheckpointSink,
    SQLiteMemoryStore? MemoryStore);

/// <summary>
/// Metrics and lifecycle observation.
/// </summary>
public sealed record SessionObservability(
    Telemetry.ISessionMetrics? Metrics,
    ISessionLifecycleObserver? LifecycleObserver);

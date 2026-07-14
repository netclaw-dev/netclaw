// -----------------------------------------------------------------------
// <copyright file="ToolExecutionContext.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Media;

namespace Netclaw.Tools;

/// <summary>
/// Describes a file a tool wants to add to the next LLM call as model input.
/// </summary>
public sealed record ModelInputFileInfo(string FilePath, string FileName, MimeType MimeType);

/// <summary>
/// Describes a file attachment registered by a tool during execution.
/// </summary>
public sealed record FileAttachmentInfo(string FilePath, string FileName, MimeType MimeType);

/// <summary>
/// Machine-readable working-context handoff from an ephemeral subagent run.
/// Confirmed changes have first-party tool provenance; observed changes are
/// derived from shared worktree state and do not imply authorship.
/// </summary>
public sealed record SubAgentWorkingContextInfo
{
    public string? ProjectDirectory { get; init; }
    public string? Worktree { get; init; }
    public string? Branch { get; init; }
    public string? Head { get; init; }
    public IReadOnlyList<string> ReadFiles { get; init; } = [];
    public IReadOnlyList<string> ConfirmedChangedFiles { get; init; } = [];
    public IReadOnlyList<string> ObservedChangedFiles { get; init; } = [];
}

/// <summary>
/// Lightweight subagent activity notification for the tools abstraction layer.
/// Tools emit these through <see cref="ToolExecutionOutputs"/>;
/// the session actor converts them to output events.
/// </summary>
public sealed record SubAgentNotificationInfo
{
    public required SubAgentRunId RunId { get; init; }
    public required string AgentName { get; init; }
    public required bool IsStarted { get; init; }
    public int ToolCount { get; init; }
    public bool Success { get; init; }
    public SubAgentRunOutcome? Outcome { get; init; }
    public SubAgentOutcomeReason? OutcomeReason { get; init; }
    public TimeSpan Duration { get; init; }
    public IReadOnlyList<SubAgentFinding> Findings { get; init; } = [];
    public SubAgentWorkingContextInfo? WorkingContext { get; init; }
}

/// <summary>
/// Structured candidate surfaced by a subagent for parent-session durable-memory review.
/// </summary>
public sealed record SubAgentFinding
{
    public SubAgentFindingShape Shape { get; init; } = SubAgentFindingShape.Conclusion;
    public required string Title { get; init; }
    public required string Content { get; init; }
    public string Kind { get; init; } = "record";
    public SubAgentFindingSensitivity Sensitivity { get; init; } = SubAgentFindingSensitivity.Normal;
    public SubAgentFindingRecallMode RecallMode { get; init; } = SubAgentFindingRecallMode.Auto;
    public string UpdateSemantics { get; init; } = "append-document";
    public double Confidence { get; init; } = 0.7;
    public SubAgentFindingDurability Durability { get; init; } = SubAgentFindingDurability.Durable;
    public SubAgentFindingReusability Reusability { get; init; } = SubAgentFindingReusability.Reusable;
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public long? FreshnessAtMs { get; init; }
}

/// <summary>
/// Validated wall-clock limit for one tool invocation.
/// </summary>
public readonly record struct ToolExecutionTimeout
{
    public static ToolExecutionTimeout Default { get; } = new(TimeSpan.FromSeconds(90));

    public ToolExecutionTimeout(TimeSpan value)
    {
        if (value <= TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(value), value, "Tool execution timeout must be positive or infinite.");

        Value = value;
    }

    public TimeSpan Value { get; }
}

/// <summary>
/// Validated character budget for model-visible inline tool output.
/// </summary>
public readonly record struct InlineOutputBudget
{
    public static InlineOutputBudget Default { get; } = new(12_000);

    public InlineOutputBudget(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        Value = value;
    }

    public int Value { get; }
}

/// <summary>
/// Explicit session binding for a tool run. Unbound execution is a deliberate
/// state used by operator-owned routes, never an accidental null session id.
/// </summary>
public abstract record ToolSessionScope
{
    private ToolSessionScope()
    {
    }

    public sealed record Unbound : ToolSessionScope;

    public sealed record Bound : ToolSessionScope
    {
        public Bound(string sessionId, string? sessionDirectory)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Session id is required for a bound tool run.", nameof(sessionId));

            SessionId = sessionId;
            SessionDirectory = sessionDirectory;
        }

        public string SessionId { get; }
        public string? SessionDirectory { get; }
    }
}

/// <summary>
/// Immutable authority and environment shared by every call in one admitted
/// tool run. Mutable per-call outputs belong to <see cref="ToolExecutionContext"/>.
/// </summary>
public sealed record ToolRunScope
{
    public required ToolSessionScope Session { get; init; }
    public required TrustAudience Audience { get; init; }
    public required InlineOutputBudget InlineOutputBudget { get; init; }
    public TrustBoundary? Boundary { get; init; }
    public string? ChannelType { get; init; }
    public ChannelDeliveryTargetInfo? DefaultDeliveryTarget { get; init; }
    public ChannelDeliveryTargetInfo? RequestedDeliveryTarget { get; init; }
    public bool SupportsInteractiveApproval { get; init; }
    public ModelModality ModelInputModalities { get; init; } = ModelModality.Text;
    public Func<object, string, CancellationToken, Task<object>>? SpawnChildActor { get; init; }
    public IParentApprovalBridge? ApprovalBridge { get; init; }
    public string? ProjectDirectory { get; init; }
    public string? InheritedCwd { get; init; }
    public IReadOnlyList<string> RecentFiles { get; init; } = [];
}

/// <summary>
/// Append-only products of one tool invocation. A fresh instance is allocated
/// per call and is never shared between parallel invocations.
/// </summary>
public sealed class ToolExecutionOutputs
{
    private List<FileAttachmentInfo>? _fileAttachments;
    private List<ModelInputFileInfo>? _modelInputFiles;

    public IReadOnlyList<FileAttachmentInfo> FileAttachments
        => _fileAttachments ?? (IReadOnlyList<FileAttachmentInfo>)[];

    public IReadOnlyList<ModelInputFileInfo> ModelInputFiles
        => _modelInputFiles ?? (IReadOnlyList<ModelInputFileInfo>)[];

    public Action<SubAgentNotificationInfo>? SubAgentActivitySink { get; set; }

    public void AddFileAttachment(string filePath, string fileName, MimeType mimeType)
    {
        _fileAttachments ??= [];
        _fileAttachments.Add(new FileAttachmentInfo(filePath, fileName, mimeType));
    }

    public void AddModelInputFile(string filePath, string fileName, MimeType mimeType)
    {
        _modelInputFiles ??= [];
        _modelInputFiles.Add(new ModelInputFileInfo(filePath, fileName, mimeType));
    }

    public void ReportSubAgentActivity(SubAgentNotificationInfo notification)
        => SubAgentActivitySink?.Invoke(notification);
}

/// <summary>
/// Mutable authorization state for one invocation attempt. The dispatcher and
/// policy pipeline own this object; tools receive no setters for approval state.
/// </summary>
public sealed class ToolApprovalAttempt
{
    private static readonly IReadOnlySet<string> EmptyPatterns =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private HashSet<string>? _oneTimeApprovedPatterns;

    public string? Cwd { get; private set; }
    public string? OneTimeApprovedToolName { get; private set; }
    public IReadOnlySet<string> OneTimeApprovedPatterns => _oneTimeApprovedPatterns ?? EmptyPatterns;
    public string? AppliedDecision { get; private set; }
    public string? AppliedPattern { get; private set; }

    public void SetCwd(string? cwd) => Cwd = cwd;

    public void SeedOneTimeApproval(string toolName, IEnumerable<string> patterns)
    {
        OneTimeApprovedToolName = toolName;
        _oneTimeApprovedPatterns = new HashSet<string>(patterns, StringComparer.OrdinalIgnoreCase);
    }

    public void ClearOneTimeApproval()
    {
        OneTimeApprovedToolName = null;
        _oneTimeApprovedPatterns = null;
    }

    public void ApplyDecision(string decision, string? pattern)
    {
        AppliedDecision = decision;
        AppliedPattern = pattern;
    }

    public void ClearAppliedDecision()
    {
        AppliedDecision = null;
        AppliedPattern = null;
    }
}

/// <summary>
/// Immutable per-call context visible to tool implementations.
/// </summary>
public class ToolInvocationContext
{
    public ToolInvocationContext(
        ToolRunScope runScope,
        ToolExecutionTimeout executionTimeout)
    {
        ArgumentNullException.ThrowIfNull(runScope);

        RunScope = runScope;
        Outputs = new ToolExecutionOutputs();
        Audience = runScope.Audience;
        Boundary = runScope.Boundary;
        ChannelType = runScope.ChannelType;
        DefaultDeliveryTarget = runScope.DefaultDeliveryTarget;
        RequestedDeliveryTarget = runScope.RequestedDeliveryTarget;
        SupportsInteractiveApproval = runScope.SupportsInteractiveApproval;
        ModelInputModalities = runScope.ModelInputModalities;
        SpawnChildActor = runScope.SpawnChildActor;
        ApprovalBridge = runScope.ApprovalBridge;
        ProjectDirectory = runScope.ProjectDirectory;
        InheritedCwd = runScope.InheritedCwd;
        RecentFiles = runScope.RecentFiles;
        MaxInlineToolResultChars = runScope.InlineOutputBudget.Value;
        ExecutionTimeout = executionTimeout;

        if (runScope.Session is ToolSessionScope.Bound bound)
        {
            SessionId = bound.SessionId;
            SessionDirectory = bound.SessionDirectory;
        }
    }

    /// <summary>
    /// Immutable authority and environment from which this per-call context was created.
    /// </summary>
    public ToolRunScope RunScope { get; }

    public ToolExecutionOutputs Outputs { get; }

    /// <summary>
    /// Parsed trust audience for this tool call. Required and non-nullable, so a
    /// tool gate reads it directly with no missing-audience fallback. The default
    /// is resolved once where the context is built.
    /// </summary>
    public TrustAudience Audience { get; internal init; }

    public TrustBoundary? Boundary { get; internal init; }

    /// <summary>
    /// Per-call timeout requested by the LLM after pipeline clamping.
    /// Tools that have their own internal timeout should honor this when set.
    /// </summary>
    public ToolExecutionTimeout ExecutionTimeout { get; internal init; }


    public string? ChannelType { get; internal init; }

    /// <summary>
    /// Delivery target inherited from channel-originated input. Trigger sources
    /// must not rely on this because they are not output channels.
    /// </summary>
    public ChannelDeliveryTargetInfo? DefaultDeliveryTarget { get; internal init; }

    /// <summary>
    /// Explicit delivery target selected by a trigger source such as a reminder
    /// or webhook route when it expects external output.
    /// </summary>
    public ChannelDeliveryTargetInfo? RequestedDeliveryTarget { get; internal init; }

    public ChannelDeliveryTargetInfo? EffectiveDeliveryTarget
        => RequestedDeliveryTarget ?? DefaultDeliveryTarget;

    /// <summary>
    /// Whether the originating channel supports interactive approval prompts.
    /// When false, approval-gated tools are automatically denied.
    /// </summary>
    public bool SupportsInteractiveApproval { get; internal init; }

    /// <summary>
    /// Modalities accepted by the active model for this tool call.
    /// </summary>
    public ModelModality ModelInputModalities { get; internal init; }

    /// <summary>
    /// Factory delegate for spawning subagent actors as children of the owning session.
    /// Wired by <c>LlmSessionActor</c> so subagents are supervised and lifecycle-managed
    /// by the session. Returns an <c>IActorRef</c>-compatible object that supports Ask.
    /// The delegate receives (Props, actorName, cancellationToken) and marshals the
    /// request back onto the session actor thread before creating the child.
    /// </summary>
    /// <remarks>
    /// Using <c>object</c> for Props and IActorRef to avoid Akka dependency in the
    /// tools abstraction layer. The actual types are <c>Akka.Actor.Props</c> and
    /// <c>Akka.Actor.IActorRef</c>.
    /// </remarks>
    public Func<object, string, CancellationToken, Task<object>>? SpawnChildActor { get; internal init; }

    /// <summary>
    /// Parent session's approval bridge for sub-agent approval chaining. When set,
    /// the sub-agent can route approval requests back to the interactive user.
    /// </summary>
    public IParentApprovalBridge? ApprovalBridge { get; }

    /// <summary>The session that initiated this tool call.</summary>
    public string? SessionId { get; }

    /// <summary>
    /// Session-scoped temp directory for tools that write files to disk.
    /// Created lazily on first access.
    /// </summary>
    public string? SessionDirectory { get; }

    /// <summary>
    /// The session <i>content</i> inline budget
    /// (<c>SessionTuning.MaxInlineToolResultChars</c>), surfaced here so
    /// <c>DispatchingToolExecutor</c> can bound a tool result and spill the
    /// overflow to <c>{SessionDirectory}/tool-calls/{callId}.log</c>. The dispatcher
    /// uses a tool's own <c>InlineOutputBudgetChars</c> override when set (verbose
    /// tools), else this content budget. Zero when unset (the dispatcher falls back
    /// to its built-in content default).
    /// </summary>
    public int MaxInlineToolResultChars { get; internal init; }

    /// <summary>
    /// Parent session's resolved cwd snapshot, captured at spawn time for
    /// sub-agent contexts. Read by <see cref="ResolveShellCwd"/> as the
    /// last-resort fallback so a sub-agent whose own
    /// <see cref="ProjectDirectory"/> and <see cref="SessionDirectory"/>
    /// happen to be unset still surfaces the parent's effective working
    /// directory to the approval gate. Distinct from <see cref="Cwd"/>:
    /// <c>Cwd</c> is the per-call resolved <i>output</i> the approval gate
    /// writes; <c>InheritedCwd</c> is a one-shot snapshot <i>input</i>.
    /// </summary>
    public string? InheritedCwd { get; internal init; }

    /// <summary>
    /// Absolute path to the project directory the agent is currently working
    /// on, mirroring <c>WorkingContext.ProjectDirectory</c> from the session
    /// state. Set by the session pipeline at context-build time so tools and
    /// the approval gate can resolve a cwd without a round-trip through the
    /// session actor. Null when no project root has been declared via
    /// <c>set_working_directory</c>.
    /// </summary>
    public string? ProjectDirectory { get; internal init; }

    /// <summary>
    /// Read-only snapshot of the parent session's recently used files. This is
    /// grounding for delegated work and does not grant filesystem authority.
    /// </summary>
    public IReadOnlyList<string> RecentFiles { get; internal init; }

    /// <summary>
    /// Resolves the working directory for a shell-style invocation. Returns
    /// the first non-empty value of:
    /// <list type="number">
    /// <item><paramref name="explicitArg"/> — the tool call's
    /// <c>WorkingDirectory</c> argument when the agent provided one;</item>
    /// <item><see cref="ProjectDirectory"/> — the session's declared project
    /// root, populated from <c>WorkingContext.ProjectDirectory</c>;</item>
    /// <item><see cref="SessionDirectory"/> — the per-session scratch
    /// directory under <c>~/.netclaw/sessions/&lt;id&gt;/</c>;</item>
    /// <item><see cref="InheritedCwd"/> — a sub-agent's snapshot of the
    /// parent's resolved cwd, used when the child has no
    /// <see cref="ProjectDirectory"/> or <see cref="SessionDirectory"/> of
    /// its own.</item>
    /// </list>
    /// Returns <c>null</c> only when none of the four is available, which is
    /// the contract for tools that are not directory-anchored. Shell tools
    /// SHALL never inherit the daemon process's cwd — that defeats the
    /// approval policy's safe-space invariant because the daemon's cwd is
    /// unrelated to what the agent is "working on."
    /// </summary>
    public string? ResolveShellCwd(string? explicitArg)
    {
        if (!string.IsNullOrWhiteSpace(explicitArg))
            return explicitArg;
        if (!string.IsNullOrWhiteSpace(ProjectDirectory))
            return ProjectDirectory;
        if (!string.IsNullOrWhiteSpace(SessionDirectory))
            return SessionDirectory;
        if (!string.IsNullOrWhiteSpace(InheritedCwd))
            return InheritedCwd;
        return null;
    }

    /// <summary>
    /// File attachments registered by tools during execution.
    /// </summary>
    internal IReadOnlyList<FileAttachmentInfo> FileAttachments
        => Outputs.FileAttachments;

    /// <summary>
    /// Files registered by tools for model-visible input on the next LLM call.
    /// </summary>
    internal IReadOnlyList<ModelInputFileInfo> ModelInputFiles
        => Outputs.ModelInputFiles;

    /// <summary>
    /// Register a file attachment to be emitted as <c>FileOutput</c> after tool execution.
    /// </summary>
    internal void AddFileAttachment(string filePath, string fileName, string mimeType)
        => AddFileAttachment(filePath, fileName, new MimeType(mimeType));

    internal void AddFileAttachment(string filePath, string fileName, MimeType mimeType)
        => Outputs.AddFileAttachment(filePath, fileName, mimeType);

    /// <summary>
    /// Register a file to be copied into session media and supplied to the model.
    /// </summary>
    internal void AddModelInputFile(string filePath, string fileName, string mimeType)
        => AddModelInputFile(filePath, fileName, new MimeType(mimeType));

    internal void AddModelInputFile(string filePath, string fileName, MimeType mimeType)
        => Outputs.AddModelInputFile(filePath, fileName, mimeType);

}

/// <summary>
/// Pipeline-owned execution state. Tool implementations receive only the
/// immutable <see cref="ToolInvocationContext"/> base contract.
/// </summary>
public sealed class ToolExecutionContext : ToolInvocationContext
{
    public ToolExecutionContext(ToolRunScope runScope, ToolExecutionTimeout executionTimeout)
        : base(runScope, executionTimeout)
    {
        Approval = new ToolApprovalAttempt();
    }

    public ToolApprovalAttempt Approval { get; }

    internal string? OneTimeApprovedToolName
    {
        get => Approval.OneTimeApprovedToolName;
        set
        {
            if (value is null)
                Approval.ClearOneTimeApproval();
            else
                Approval.SeedOneTimeApproval(value, Approval.OneTimeApprovedPatterns);
        }
    }

    internal string? AppliedApprovalDecision => Approval.AppliedDecision;
    internal string? AppliedApprovalPattern => Approval.AppliedPattern;
    internal IReadOnlySet<string> OneTimeApprovedPatterns => Approval.OneTimeApprovedPatterns;
    internal string? Cwd
    {
        get => Approval.Cwd;
        set => Approval.SetCwd(value);
    }

    internal void SetOneTimeApprovedPatterns(IEnumerable<string> patterns)
        => Approval.SeedOneTimeApproval(Approval.OneTimeApprovedToolName ?? string.Empty, patterns);
}

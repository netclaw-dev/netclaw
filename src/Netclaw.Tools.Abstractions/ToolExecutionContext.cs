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
/// Lightweight subagent activity notification for the tools abstraction layer.
/// Tools emit these via <see cref="ToolExecutionContext.OnSubAgentActivity"/>;
/// the session actor converts them to output events.
/// </summary>
public sealed record SubAgentNotificationInfo
{
    public required string RunId { get; init; }
    public required string AgentName { get; init; }
    public required bool IsStarted { get; init; }
    public int ToolCount { get; init; }
    public bool Success { get; init; }
    public TimeSpan Duration { get; init; }
    public IReadOnlyList<SubAgentFinding> Findings { get; init; } = [];
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
/// Per-call execution context passed from the session actor to tools.
/// Provides session-scoped state like working directories for file output.
/// </summary>
public sealed class ToolExecutionContext
{
    // Context-less sentinel for tools invoked outside a session. It carries the
    // most-restrictive audience — a tool with no trust context can only run at
    // the lowest privilege.
    public static readonly ToolExecutionContext Empty = new(null, null) { Audience = TrustAudience.Public };
    private static readonly IReadOnlySet<string> EmptyApprovedPatternSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private List<FileAttachmentInfo>? _fileAttachments;
    private List<ModelInputFileInfo>? _modelInputFiles;
    private HashSet<string>? _oneTimeApprovedPatterns;

    public ToolExecutionContext(string? sessionId, string? sessionDirectory)
    {
        SessionId = sessionId;
        SessionDirectory = sessionDirectory;
    }

    /// <summary>
    /// Parsed trust audience for this tool call. Required and non-nullable, so a
    /// tool gate reads it directly with no missing-audience fallback. The default
    /// is resolved once, where the context is built; the context-less
    /// <see cref="Empty"/> sentinel carries the most-restrictive
    /// <see cref="TrustAudience.Public"/>.
    /// </summary>
    public required TrustAudience Audience { get; init; }

    public TrustBoundary? Boundary { get; set; }

    /// <summary>
    /// Per-call timeout requested by the LLM after pipeline clamping.
    /// Tools that have their own internal timeout should honor this when set.
    /// </summary>
    public int? RequestedTimeoutSeconds { get; set; }


    public string? ChannelType { get; set; }

    /// <summary>
    /// Delivery target inherited from channel-originated input. Trigger sources
    /// must not rely on this because they are not output channels.
    /// </summary>
    public ChannelDeliveryTargetInfo? DefaultDeliveryTarget { get; set; }

    /// <summary>
    /// Explicit delivery target selected by a trigger source such as a reminder
    /// or webhook route when it expects external output.
    /// </summary>
    public ChannelDeliveryTargetInfo? RequestedDeliveryTarget { get; set; }

    public ChannelDeliveryTargetInfo? EffectiveDeliveryTarget
        => RequestedDeliveryTarget ?? DefaultDeliveryTarget;

    /// <summary>
    /// Whether the originating channel supports interactive approval prompts.
    /// When false, approval-gated tools are automatically denied.
    /// </summary>
    public bool? SupportsInteractiveApproval { get; set; }

    /// <summary>
    /// Modalities accepted by the active model for this tool call.
    /// </summary>
    public ModelModality ModelInputModalities { get; set; } = ModelModality.Text;

    /// <summary>
    /// Optional callback for tools that spawn subagents.
    /// The session wires this to relay notifications as <c>SubAgentOutput</c> events.
    /// </summary>
    public Action<SubAgentNotificationInfo>? OnSubAgentActivity { get; set; }

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
    public Func<object, string, CancellationToken, Task<object>>? SpawnChildActor { get; set; }

    /// <summary>
    /// Parent session's approval bridge for sub-agent approval chaining. When set,
    /// the sub-agent can route approval requests back to the interactive user.
    /// </summary>
    public IParentApprovalBridge? ApprovalBridge { get; set; }

    /// <summary>
    /// Tool name granted a one-shot approval for the current execution retry.
    /// This is not persisted and only applies to the current in-memory context.
    /// </summary>
    public string? OneTimeApprovedToolName { get; set; }

    /// <summary>
    /// Approval decision applied without an interactive prompt for this tool
    /// call, used by the session audit trail to distinguish cached approvals
    /// from tools that were never gated.
    /// </summary>
    public string? AppliedApprovalDecision { get; set; }

    /// <summary>
    /// Human-readable approval match scopes used by
    /// <see cref="AppliedApprovalDecision"/>.
    /// </summary>
    public string? AppliedApprovalPattern { get; set; }

    /// <summary>
    /// Approval patterns granted for a single retry of the current tool call.
    /// </summary>
    public IReadOnlySet<string> OneTimeApprovedPatterns
        => _oneTimeApprovedPatterns ?? EmptyApprovedPatternSet;

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
    public int MaxInlineToolResultChars { get; init; }

    /// <summary>
    /// Resolved absolute working directory for the in-flight tool call. Set
    /// by the session pipeline from the candidate tool arguments,
    /// <c>WorkingContext.ProjectDirectory</c>, or <see cref="SessionDirectory"/>
    /// — whichever resolves first. The approval gate uses this as the directory
    /// half of the candidate <c>(verb, directory)</c> pair when evaluating
    /// folder-scoped <see cref="Netclaw.Configuration.ApprovalEntry"/> records.
    /// Null when the tool call is not directory-anchored (e.g. an in-process
    /// tool like <c>store_memory</c>).
    /// </summary>
    public string? Cwd { get; set; }

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
    public string? InheritedCwd { get; init; }

    /// <summary>
    /// Absolute path to the project directory the agent is currently working
    /// on, mirroring <c>WorkingContext.ProjectDirectory</c> from the session
    /// state. Set by the session pipeline at context-build time so tools and
    /// the approval gate can resolve a cwd without a round-trip through the
    /// session actor. Null when no project root has been declared via
    /// <c>set_working_directory</c>.
    /// </summary>
    public string? ProjectDirectory { get; set; }

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
    public IReadOnlyList<FileAttachmentInfo> FileAttachments
        => _fileAttachments ?? (IReadOnlyList<FileAttachmentInfo>)[];

    /// <summary>
    /// Files registered by tools for model-visible input on the next LLM call.
    /// </summary>
    public IReadOnlyList<ModelInputFileInfo> ModelInputFiles
        => _modelInputFiles ?? (IReadOnlyList<ModelInputFileInfo>)[];

    /// <summary>
    /// Register a file attachment to be emitted as <c>FileOutput</c> after tool execution.
    /// </summary>
    public void AddFileAttachment(string filePath, string fileName, string mimeType)
        => AddFileAttachment(filePath, fileName, new MimeType(mimeType));

    public void AddFileAttachment(string filePath, string fileName, MimeType mimeType)
    {
        _fileAttachments ??= [];
        _fileAttachments.Add(new FileAttachmentInfo(filePath, fileName, mimeType));
    }

    /// <summary>
    /// Register a file to be copied into session media and supplied to the model.
    /// </summary>
    public void AddModelInputFile(string filePath, string fileName, string mimeType)
        => AddModelInputFile(filePath, fileName, new MimeType(mimeType));

    public void AddModelInputFile(string filePath, string fileName, MimeType mimeType)
    {
        _modelInputFiles ??= [];
        _modelInputFiles.Add(new ModelInputFileInfo(filePath, fileName, mimeType));
    }

    public void SetOneTimeApprovedPatterns(IEnumerable<string> patterns)
    {
        _oneTimeApprovedPatterns = new HashSet<string>(patterns, StringComparer.OrdinalIgnoreCase);
    }
}

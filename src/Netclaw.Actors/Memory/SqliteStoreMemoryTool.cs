using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Tools;

namespace Netclaw.Actors.Memory;

[NetclawTool("store_memory",
    "Save knowledge to cross-session memory for future retrieval. "
    + "Use for solutions, decisions, research findings, and project context. "
    + "Write descriptive titles and include WHY not just WHAT.",
    Grant = "builtin")]
public sealed partial class SqliteStoreMemoryTool : NetclawTool<SqliteStoreMemoryTool.Params>
{
    private readonly IMemoryCheckpointSink _checkpointSink;
    private readonly ILogger _logger;

    public record Params(
        [property: Description("Title for the memory entry")]
        string Title,
        [property: Description("Content to store — use markdown, include code blocks, provide full context")]
        string Content,
        [property: Description("Optional comma-separated tags")]
        string? Tags = null);

    public SqliteStoreMemoryTool(IMemoryCheckpointSink checkpointSink, ILogger<SqliteStoreMemoryTool>? logger = null)
    {
        _checkpointSink = checkpointSink;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    protected override async Task<string> ExecuteAsync(Params args, ToolExecutionContext context, CancellationToken ct)
    {
        var sessionId = string.IsNullOrWhiteSpace(context.SessionId)
            ? "manual/tool"
            : context.SessionId!;

        var payload = new MemoryCheckpointPayload(
            SessionId: sessionId,
            TriggerType: "explicit-memory-request",
            Source: "store_memory",
            Content: args.Content,
            UserContent: args.Content,
            AssistantContent: null,
            IsExplicitRequest: true,
            HasVerifiedToolFinding: false,
            IsCompactionBoundary: false,
            HasAcceptedSubAgentFinding: false,
            Domain: ResolveDomain(sessionId),
            Sensitivity: "normal",
            RecallMode: "manual",
            Confidence: 0.95,
            Title: args.Title,
            UpdateSemantics: "merge-document",
            Kind: "document");

        var result = await _checkpointSink.EnqueueAsync(new MemoryCheckpointRequest(
            SessionId: sessionId,
            TurnId: null,
            TriggerType: payload.TriggerType,
            Priority: 100,
            Payload: payload), ct);

        _logger.LogInformation("SQLite store_memory committed checkpoint={CheckpointId} session={SessionId}",
            result.CheckpointId, sessionId);
        return $"Memory save confirmed: \"{args.Title}\" (checkpoint: {result.CheckpointId}).";
    }

    protected override Task<string> ExecuteAsync(Params args, CancellationToken ct)
        => ExecuteAsync(args, ToolExecutionContext.Empty, ct);

    private static string ResolveDomain(string sessionId)
    {
        var slash = sessionId.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0)
            return $"project:{sessionId[..slash].ToLowerInvariant()}";
        return "project:default";
    }
}

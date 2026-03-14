using System.ComponentModel;
using Akka.Actor;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Actors.Memory;

/// <summary>
/// Memory storage tool backed by a curation subagent that delegates to Memorizer MCP.
/// The frontline model calls <c>store_memory</c> with title/content/tags.
/// A subagent handles decomposition, classification, workspace routing, dedup,
/// and relationship linking via Memorizer's full tool suite.
///
/// Registered when <c>Memory.Provider = "memorizer"</c>.
/// </summary>
[NetclawTool("store_memory",
    "Save knowledge to cross-session memory for future retrieval. "
    + "Use for solutions, decisions, research findings, and project context.",
    Grant = "builtin")]
public sealed partial class MemorizerStoreMemoryTool : NetclawTool<MemorizerStoreMemoryTool.Params>
{
    private readonly ActorSystem _actorSystem;
    private readonly IChatClientProvider _clientProvider;
    private readonly ToolRegistry _toolRegistry;
    private readonly SubAgentConfig _subAgentConfig;
    private readonly ILogger _logger;

    /// <summary>
    /// Memorizer MCP tools the curation agent needs access to.
    /// </summary>
    private static readonly string[] CurationToolNames =
    [
        "memorizer/store",
        "memorizer/store_memory",
        "memorizer/search_memories",
        "memorizer/get_workspace",
        "memorizer/get_project_context",
        "memorizer/create_reference",
        "memorizer/update_metadata",
        "memorizer/move_memory"
    ];

    private const string CurationPrompt = """
        You are a memory curation agent. Your job is to organize memories into the
        Memorizer knowledge base. You receive a memory (title, content, tags) from
        the user and must store it properly.

        Steps:
        1. Search for similar existing memories using memorizer/search_memories to avoid duplicates.
        2. Store the memory using memorizer/store.
        3. If you find closely related existing memories, create references between them.

        CRITICAL — memorizer/store requires these exact parameters:
        - "title": (string, required) descriptive title
        - "text": (string, required) the full content to store — this is NOT called "content"
        - "type": (string, required) use "reference" for facts/knowledge
        - "source": (string, required) use "LLM"
        - "tags": (array of strings, optional) e.g. ["reference", "debugging"]

        Example memorizer/store call:
        {"title": "My Title", "text": "The content goes here", "type": "reference", "source": "LLM", "tags": ["tag1", "tag2"]}

        ## Title Quality

        Titles should be descriptive and searchable — they are the primary way
        memories are discovered later.

        BAD: "DB fix", "config issue", "deployment notes"
        GOOD: "PostgreSQL connection pooling fix for Npgsql 8.x",
              "Kubernetes pod eviction caused by memory limits on worker nodes",
              "Akka.NET cluster sharding rebalance strategy for 50+ entity types"

        ## Content Quality

        Include WHY, not just WHAT. Rich memories with context are the only useful
        kind. Thin memories ("use X instead of Y") waste storage and confuse future
        retrieval.

        BAD:
        "Fixed the DB connection issue by increasing pool size."

        GOOD:
        "## Problem\nProduction DB connections exhausted under load.\n\n## Root Cause\n
        Npgsql 8.x defaults to poolSize=100. Our worker service opens connections per
        actor, and with 200 sharded entities we exceed the pool.\n\n## Solution\n
        Increased `MaxPoolSize` to 300 in connection string. Also added
        `Connection Idle Lifetime=60` to reclaim idle connections faster.\n\n## Links\n
        - PR: https://github.com/org/repo/pull/42\n
        - Npgsql docs: https://www.npgsql.org/doc/connection-string-parameters.html"

        ## Formatting Rules

        - Use markdown: headers (##), code blocks (```), bullet lists, bold for emphasis.
        - Include hyperlinks to repos, PRs, docs, Stack Overflow answers, or any
          external resources that provide context.
        - Code samples should be in fenced code blocks with language tags.

        ## Guidelines

        - If a near-duplicate exists, skip storing and mention the existing memory.
        - Keep your responses brief — just confirm what you stored.
        """;

    public record Params(
        [property: Description("Title for the memory entry")]
        string Title,
        [property: Description("Content to store — use markdown, include code blocks, provide full context")]
        string Content,
        [property: Description("Optional comma-separated tags for categorization (e.g. \"reference, how-to, decision\")")]
        string? Tags = null);

    public MemorizerStoreMemoryTool(
        ActorSystem actorSystem,
        IChatClientProvider clientProvider,
        ToolRegistry toolRegistry,
        SubAgentConfig? subAgentConfig = null,
        ILogger<MemorizerStoreMemoryTool>? logger = null)
    {
        _actorSystem = actorSystem;
        _clientProvider = clientProvider;
        _toolRegistry = toolRegistry;
        _subAgentConfig = subAgentConfig ?? new SubAgentConfig();
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    protected override async Task<string> ExecuteAsync(Params args, CancellationToken ct)
        => await ExecuteAsync(args, ToolExecutionContext.Empty, ct);

    protected override async Task<string> ExecuteAsync(Params args, ToolExecutionContext context, CancellationToken ct)
    {
        var tools = ResolveCurationTools();
        if (tools.Count == 0)
        {
            _logger.LogWarning("No Memorizer tools available for curation — cannot store memory");
            return "Memory store unavailable: Memorizer MCP server not connected.";
        }

        var definition = new SubAgentDefinition
        {
            Name = "memory-curator",
            SystemPrompt = CurationPrompt,
            Tools = tools,
            ModelRole = ModelRole.Compaction,
            EmitStructuredFindings = true
        };

        var runId = System.Guid.NewGuid().ToString("N");

        // Notify session that subagent is starting
        context.OnSubAgentActivity?.Invoke(new SubAgentNotificationInfo
        {
            RunId = runId,
            AgentName = definition.Name,
            IsStarted = true,
            ToolCount = tools.Count
        });

        var task = FormatTask(args);
        var chatClient = _clientProvider.GetClient(definition.ModelRole);
        var subAgentScopeId = !string.IsNullOrWhiteSpace(context.SessionId)
            ? $"{context.SessionId}/subagent/{definition.Name}/{runId}"
            : $"subagent/{definition.Name}/{runId}";

        // Spawn subagent as a top-level actor (not tied to a session)
        var subAgent = _actorSystem.ActorOf(
            SubAgentActor.CreateProps(definition, chatClient));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var subAgentTimeout = TimeSpan.FromSeconds(_subAgentConfig.StoreMemoryTimeoutSeconds);
            var result = await subAgent.Ask<SubAgentResult>(
                new RunSubAgent
                {
                    Task = task,
                    Timeout = subAgentTimeout,
                    SessionScopeId = subAgentScopeId,
                    Cancellation = ct
                },
                timeout: subAgentTimeout.Add(TimeSpan.FromSeconds(5)), // slightly longer than subagent timeout
                cancellationToken: ct);

            sw.Stop();

            // Notify session that subagent completed
            context.OnSubAgentActivity?.Invoke(new SubAgentNotificationInfo
            {
                RunId = runId,
                AgentName = definition.Name,
                IsStarted = false,
                Success = result.Success,
                Duration = sw.Elapsed,
                Findings = result.Findings
                    .Select(f => new SubAgentFindingCandidate
                    {
                        Title = f.Title,
                        Content = f.Content,
                        Kind = f.Kind,
                        Domain = f.Domain,
                        Sensitivity = f.Sensitivity,
                        RecallMode = f.RecallMode,
                        UpdateSemantics = f.UpdateSemantics,
                        Confidence = f.Confidence,
                        FreshnessAtMs = f.FreshnessAtMs
                    })
                    .ToArray()
            });

            if (result.Success)
            {
                _logger.LogInformation("Memory curation completed: title='{Title}'", args.Title);
                return $"Memory saved: \"{args.Title}\"";
            }
            else
            {
                _logger.LogWarning("Memory curation failed: title='{Title}', reason={Reason}",
                    args.Title, result.Output);
                return $"Memory curation failed: {result.Output}";
            }
        }
        catch (Exception ex)
        {
            sw.Stop();

            TryStopSubAgent(subAgent);

            context.OnSubAgentActivity?.Invoke(new SubAgentNotificationInfo
            {
                RunId = runId,
                AgentName = definition.Name,
                IsStarted = false,
                Success = false,
                Duration = sw.Elapsed
            });

            _logger.LogError(ex, "Memory curation error: title='{Title}'", args.Title);
            return $"Error saving memory: {ex.Message}";
        }
    }

    private IReadOnlyList<INetclawTool> ResolveCurationTools()
    {
        var tools = new List<INetclawTool>();
        foreach (var name in CurationToolNames)
        {
            var tool = _toolRegistry.GetByName(name);
            if (tool is not null)
                tools.Add(tool);
        }

        return tools;
    }

    private static void TryStopSubAgent(IActorRef subAgent)
    {
        subAgent.Tell(PoisonPill.Instance);
    }

    private static string FormatTask(Params args)
    {
        var tagsSection = string.IsNullOrWhiteSpace(args.Tags)
            ? ""
            : $"\nTags: {args.Tags}";

        return $"""
            Store this memory in the knowledge base:

            Title: {args.Title}{tagsSection}

            Content:
            {args.Content}
            """;
    }
}

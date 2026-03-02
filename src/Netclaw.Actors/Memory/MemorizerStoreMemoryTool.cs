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

        Guidelines:
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
        ILogger<MemorizerStoreMemoryTool>? logger = null)
    {
        _actorSystem = actorSystem;
        _clientProvider = clientProvider;
        _toolRegistry = toolRegistry;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    protected override async Task<string> ExecuteAsync(Params args, CancellationToken ct)
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
            ModelRole = ModelRole.Compaction
        };

        var task = FormatTask(args);
        var chatClient = _clientProvider.GetClient(definition.ModelRole);

        // Spawn subagent as a top-level actor (not tied to a session)
        var subAgent = _actorSystem.ActorOf(
            SubAgentActor.CreateProps(definition, chatClient));

        try
        {
            var result = await subAgent.Ask<SubAgentResult>(
                new RunSubAgent
                {
                    Task = task,
                    Timeout = TimeSpan.FromMinutes(3)
                },
                timeout: TimeSpan.FromMinutes(4), // slightly longer than subagent timeout
                cancellationToken: ct);

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

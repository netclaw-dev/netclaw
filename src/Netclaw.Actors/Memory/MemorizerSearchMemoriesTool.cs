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
/// Memory search tool backed by a curation subagent that delegates to Memorizer MCP.
/// The frontline model calls <c>search_memories</c> with a query.
/// A subagent enriches the search results with project context, related memories,
/// and workspace metadata — acting as a recommender rather than a filter.
///
/// Registered when <c>Memory.Provider = "memorizer"</c>.
/// </summary>
[NetclawTool("search_memories",
    "Search cross-session memory for prior knowledge, saved context, and project information. "
    + "Returns matching memories ranked by relevance.",
    Grant = "builtin")]
public sealed partial class MemorizerSearchMemoriesTool : NetclawTool<MemorizerSearchMemoriesTool.Params>
{
    private readonly ActorSystem _actorSystem;
    private readonly IChatClientProvider _clientProvider;
    private readonly ToolRegistry _toolRegistry;
    private readonly SubAgentConfig _subAgentConfig;
    private readonly ILogger _logger;

    /// <summary>
    /// Memorizer MCP tools the retrieval agent needs access to.
    /// </summary>
    private static readonly string[] RetrievalToolNames =
    [
        "memorizer/search_memories",
        "memorizer/get",
        "memorizer/get_many",
        "memorizer/get_workspace",
        "memorizer/get_project_context"
    ];

    private const string RetrievalPrompt = """
        You are a memory retrieval agent. Your job is to find and curate relevant
        memories for the user's query. You have access to the Memorizer knowledge base.

        Steps:
        1. Search for memories matching the query.
        2. For promising results, fetch full details if needed.
        3. Check if the memories belong to a project or workspace that adds context.
        4. Return a curated summary of the most relevant findings.

        Guidelines:
        - Return the most relevant memories, not everything that matches.
        - Include workspace/project context when it helps understand the memory.
        - Format your response as readable text with clear memory titles and content.
        - If nothing is found, say so clearly.
        - Keep your response focused and concise.
        """;

    public record Params(
        [property: Description("Search query to find relevant memories")]
        string Query,
        [property: Description("Maximum number of results to return (default 5)")]
        int? Limit = null);

    public MemorizerSearchMemoriesTool(
        ActorSystem actorSystem,
        IChatClientProvider clientProvider,
        ToolRegistry toolRegistry,
        SubAgentConfig? subAgentConfig = null,
        ILogger<MemorizerSearchMemoriesTool>? logger = null)
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
        var tools = ResolveRetrievalTools();
        if (tools.Count == 0)
        {
            _logger.LogWarning("No Memorizer tools available for retrieval");
            return "Memory search unavailable: Memorizer MCP server not connected.";
        }

        var definition = new SubAgentDefinition
        {
            Name = "memory-retriever",
            SystemPrompt = RetrievalPrompt,
            Tools = tools,
            ModelRole = ModelRole.Compaction
        };

        // Notify session that subagent is starting
        context.OnSubAgentActivity?.Invoke(new SubAgentNotificationInfo
        {
            AgentName = definition.Name,
            IsStarted = true,
            ToolCount = tools.Count
        });

        var task = $"Search for memories related to: {args.Query}";
        if (args.Limit is > 0)
            task += $"\nReturn at most {args.Limit} results.";

        var chatClient = _clientProvider.GetClient(definition.ModelRole);

        var subAgent = _actorSystem.ActorOf(
            SubAgentActor.CreateProps(definition, chatClient));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var subAgentTimeout = TimeSpan.FromSeconds(_subAgentConfig.SearchMemoriesTimeoutSeconds);
            var result = await subAgent.Ask<SubAgentResult>(
                new RunSubAgent
                {
                    Task = task,
                    Timeout = subAgentTimeout
                },
                timeout: subAgentTimeout.Add(TimeSpan.FromSeconds(5)),
                cancellationToken: ct);

            sw.Stop();

            // Notify session that subagent completed
            context.OnSubAgentActivity?.Invoke(new SubAgentNotificationInfo
            {
                AgentName = definition.Name,
                IsStarted = false,
                Success = result.Success,
                Duration = sw.Elapsed
            });

            if (result.Success)
            {
                _logger.LogInformation("Memory retrieval completed: query='{Query}', output={OutputLength} chars",
                    args.Query, result.Output.Length);
                return result.Output;
            }
            else
            {
                _logger.LogWarning("Memory retrieval failed: query='{Query}', reason={Reason}",
                    args.Query, result.Output);
                return $"Memory search failed: {result.Output}";
            }
        }
        catch (Exception ex)
        {
            sw.Stop();

            context.OnSubAgentActivity?.Invoke(new SubAgentNotificationInfo
            {
                AgentName = definition.Name,
                IsStarted = false,
                Success = false,
                Duration = sw.Elapsed
            });

            _logger.LogError(ex, "Memory retrieval error: query='{Query}'", args.Query);
            return $"Error searching memories: {ex.Message}";
        }
    }

    private IReadOnlyList<INetclawTool> ResolveRetrievalTools()
    {
        var tools = new List<INetclawTool>();
        foreach (var name in RetrievalToolNames)
        {
            var tool = _toolRegistry.GetByName(name);
            if (tool is not null)
                tools.Add(tool);
        }

        return tools;
    }
}

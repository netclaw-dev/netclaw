using Microsoft.Extensions.AI;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Registration entry pairing a tool with its ACL grant category.
/// </summary>
public sealed record ToolRegistration(INetclawTool Tool, string GrantCategory);

/// <summary>
/// Registers <see cref="INetclawTool"/> definitions with grant categories for policy filtering.
/// Sessions receive only tools whose grant category is in the session's allowed set.
/// </summary>
public sealed class ToolRegistry
{
    private readonly List<ToolRegistration> _tools = new();

    public void Register(INetclawTool tool)
    {
        _tools.Add(new ToolRegistration(tool, tool.GrantCategory));
    }

    /// <summary>
    /// Register an <see cref="AITool"/> directly (for test fakes that don't implement INetclawTool).
    /// </summary>
    public void Register(AITool tool, string grantCategory)
    {
        _tools.Add(new ToolRegistration(new AIToolAdapter(tool, grantCategory), grantCategory));
    }

    /// <summary>All registered tools as AITool for ChatOptions.Tools.</summary>
    public IReadOnlyList<AITool> GetAllTools() =>
        _tools.Select(t => t.Tool.ToAITool()).ToList();

    /// <summary>Only tools whose grant category is in the allowed set.</summary>
    public IReadOnlyList<AITool> GetToolsForGrants(IReadOnlySet<string> grantedCategories) =>
        _tools
            .Where(t => grantedCategories.Contains(t.GrantCategory))
            .Select(t => t.Tool.ToAITool())
            .ToList();

    /// <summary>Find a tool by name for dispatch.</summary>
    public INetclawTool? GetByName(string name) =>
        _tools.FirstOrDefault(t => t.Tool.Name == name)?.Tool;

    /// <summary>
    /// Adapter to wrap bare <see cref="AITool"/> instances (e.g. test fakes) as <see cref="INetclawTool"/>.
    /// </summary>
    private sealed class AIToolAdapter : INetclawTool
    {
        private readonly AITool _tool;

        public AIToolAdapter(AITool tool, string grantCategory)
        {
            _tool = tool;
            GrantCategory = grantCategory;
            Name = tool.GetType().Name;
            Description = "";
            ParameterSchema = default;
        }

        public string Name { get; }
        public string Description { get; }
        public string GrantCategory { get; }
        public System.Text.Json.JsonElement ParameterSchema { get; }
        public AITool ToAITool() => _tool;

        public Task<string> ExecuteAsync(IDictionary<string, object?>? arguments, CancellationToken ct = default)
            => Task.FromResult("Not supported via adapter");
    }
}

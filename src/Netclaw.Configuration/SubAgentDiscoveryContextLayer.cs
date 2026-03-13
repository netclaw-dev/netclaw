namespace Netclaw.Configuration;

/// <summary>
/// Dynamic context layer that advertises available subagents to the frontline LLM.
/// Follows the same volatile-string pattern as <see cref="ToolIndexContextLayer"/>.
/// Content is updated after startup and MCP discovery by the ToolIndexUpdater.
/// </summary>
public sealed class SubAgentDiscoveryContextLayer : IContextLayerProvider
{
    private volatile string _index = string.Empty;

    /// <summary>
    /// Replace the subagent discovery content. Thread-safe via volatile write.
    /// </summary>
    public void Update(string index) => _index = index;

    public string GetContextLayer() => _index;
}

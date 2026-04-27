namespace Netclaw.Configuration;

/// <summary>
/// Dynamic context layer that advertises available subagents to the frontline LLM.
/// Content is updated after startup and MCP discovery by the ToolIndexUpdater.
/// Returns empty for Public audience or when subagents are disabled.
/// </summary>
public sealed class SubAgentDiscoveryContextLayer : IContextLayerProvider
{
    private readonly SubAgentConfig _config;
    private volatile string _index = string.Empty;

    public SubAgentDiscoveryContextLayer() : this(new SubAgentConfig()) { }

    public SubAgentDiscoveryContextLayer(SubAgentConfig config)
    {
        _config = config;
    }

    public ContextLayerTiming Timing => ContextLayerTiming.OnceAtStart;

    /// <summary>
    /// Replace the subagent discovery content. Thread-safe via volatile write.
    /// </summary>
    public void Update(string index) => _index = index;

    public string GetContextLayer(TrustAudience audience)
    {
        if (audience == TrustAudience.Public)
            return string.Empty;
        if (!_config.Enabled)
            return string.Empty;
        return _index;
    }
}

namespace Netclaw.Configuration;

/// <summary>
/// Dynamic context layer that provides the compressed skill index.
/// Updated after skill scanning or enrichment completes.
/// Returns empty for Public audience or when skill sync is disabled.
/// </summary>
public sealed class SkillIndexContextLayer : IContextLayerProvider
{
    private readonly SkillSyncConfig _config;
    private volatile string _index = string.Empty;

    public SkillIndexContextLayer() : this(new SkillSyncConfig()) { }

    public SkillIndexContextLayer(SkillSyncConfig config)
    {
        _config = config;
    }

    public ContextLayerTiming Timing => ContextLayerTiming.OnceAtStart;

    /// <summary>
    /// Replace the skill index content. Thread-safe via volatile write.
    /// Called by sync and enrichment services after rebuilding menus.
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

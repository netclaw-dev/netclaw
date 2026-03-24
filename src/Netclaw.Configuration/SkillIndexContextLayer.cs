namespace Netclaw.Configuration;

/// <summary>
/// Dynamic context layer that provides the compressed skill index.
/// Updated after skill scanning or enrichment completes.
/// Currently serves the Personal audience menu (most permissive).
/// Audience-differentiated injection will be wired when sessions
/// pass their effective audience to the context layer system.
/// </summary>
public sealed class SkillIndexContextLayer : IContextLayerProvider
{
    private volatile string _index = string.Empty;

    public ContextLayerTiming Timing => ContextLayerTiming.OnceAtStart;

    /// <summary>
    /// Replace the skill index content. Thread-safe via volatile write.
    /// Called by sync and enrichment services after rebuilding menus.
    /// </summary>
    public void Update(string index) => _index = index;

    public string GetContextLayer() => _index;
}

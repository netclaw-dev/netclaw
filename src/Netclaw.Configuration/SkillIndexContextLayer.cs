namespace Netclaw.Configuration;

/// <summary>
/// Dynamic context layer that provides the compressed skill index.
/// Structurally identical to <see cref="ToolIndexContextLayer"/>.
/// Updated after skill scanning completes.
/// </summary>
public sealed class SkillIndexContextLayer : IContextLayerProvider
{
    private volatile string _index = string.Empty;

    /// <summary>
    /// Replace the skill index content. Thread-safe via volatile write.
    /// </summary>
    public void Update(string index) => _index = index;

    public string GetContextLayer() => _index;
}

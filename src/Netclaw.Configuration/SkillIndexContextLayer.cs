// -----------------------------------------------------------------------
// <copyright file="SkillIndexContextLayer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// Dynamic context layer that provides the compressed skill index.
/// Updated after skill scanning or enrichment completes.
/// Returns empty for Public audience or when skill sync is disabled.
/// </summary>
public sealed class SkillIndexContextLayer : IContextLayerProvider
{
    private readonly SkillSyncConfig _config;
    private volatile string _teamIndex = string.Empty;
    private volatile string _personalIndex = string.Empty;

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
    public void Update(string index)
    {
        _teamIndex = index;
        _personalIndex = index;
    }

    public void Update(TrustAudience audience, string index)
    {
        switch (audience)
        {
            case TrustAudience.Team:
                _teamIndex = index;
                break;
            case TrustAudience.Personal:
                _personalIndex = index;
                break;
            case TrustAudience.Public:
                throw new ArgumentOutOfRangeException(nameof(audience), audience,
                    "Public sessions cannot receive a skill index.");
            default:
                throw new ArgumentOutOfRangeException(nameof(audience), audience, null);
        }
    }

    public string GetContextLayer(TrustAudience audience)
    {
        if (audience == TrustAudience.Public)
            return string.Empty;
        if (!_config.Enabled)
            return string.Empty;
        return audience switch
        {
            TrustAudience.Team => _teamIndex,
            TrustAudience.Personal => _personalIndex,
            _ => string.Empty,
        };
    }
}

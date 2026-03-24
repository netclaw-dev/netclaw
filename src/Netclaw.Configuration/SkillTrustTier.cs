namespace Netclaw.Configuration;

/// <summary>
/// Trust classification for a skill based on its source.
/// Lower values indicate higher trust. Trust tier is inferred from the
/// skill's directory location — a skill cannot self-declare its tier.
/// </summary>
public enum SkillTrustTier
{
    /// <summary>Compiled into the binary or delivered via the official system feed.</summary>
    System = 0,

    /// <summary>Placed on disk by the operator or created by the user via <c>skill_manage</c>.</summary>
    User = 1,

    /// <summary>From the Netclaw org community feed (PR-reviewed).</summary>
    Community = 2,

    /// <summary>From a third-party marketplace or well-known endpoint.</summary>
    External = 3,

    /// <summary>Synthesized autonomously by the agent without user direction.</summary>
    Agent = 4
}

public static class SkillTrustTierExtensions
{
    /// <summary>
    /// Returns the default minimum <see cref="TrustAudience"/> required for a skill
    /// at this trust tier to be visible in the session's skill index.
    /// Individual skills can override this via the <c>minimum-audience</c> frontmatter
    /// field, but only System and User tiers may widen to <see cref="TrustAudience.Public"/>.
    /// </summary>
    public static TrustAudience DefaultMinimumAudience(this SkillTrustTier tier) => tier switch
    {
        SkillTrustTier.System => TrustAudience.Team,
        SkillTrustTier.User => TrustAudience.Team,
        SkillTrustTier.Community => TrustAudience.Team,
        SkillTrustTier.External => TrustAudience.Personal,
        SkillTrustTier.Agent => TrustAudience.Personal,
        _ => TrustAudience.Personal
    };
}

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

    /// <summary>Manually placed by the operator in the user skills directory.</summary>
    Operator = 1,

    /// <summary>From the Netclaw org community feed (PR-reviewed).</summary>
    Community = 2,

    /// <summary>From a third-party marketplace or well-known endpoint.</summary>
    External = 3,

    /// <summary>Created by the agent at runtime.</summary>
    Agent = 4
}

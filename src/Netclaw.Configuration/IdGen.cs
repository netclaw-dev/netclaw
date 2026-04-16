namespace Netclaw.Configuration;

/// <summary>
/// Centralized ID generation with purpose-named methods to replace scattered
/// <c>Guid.NewGuid().ToString("N")[..N]</c> patterns across the codebase.
/// </summary>
public static class IdGen
{
    /// <summary>12-character alert ID for <see cref="OperationalAlert"/>.</summary>
    public static string AlertId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>8-character short ID for turn IDs, message IDs, and probe IDs.</summary>
    public static string ShortId() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>6-character suffix for reminder IDs and similar short suffixes.</summary>
    public static string Suffix() => Guid.NewGuid().ToString("N")[..6];

    /// <summary>Full 32-character hex string for state tokens, temp directories, and other non-truncated uses.</summary>
    public static string Full() => Guid.NewGuid().ToString("N");
}

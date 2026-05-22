// -----------------------------------------------------------------------
// <copyright file="NetclawUserAgent.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// The User-Agent string Netclaw advertises on every outbound HTTP request,
/// plus the structured component header that identifies the calling subsystem.
/// Format: <c>Netclaw/{version} (+https://netclaw.dev; sha={shortSha})</c>.
/// </summary>
public static class NetclawUserAgent
{
    /// <summary>
    /// Header name carrying the calling subsystem (e.g. "mcp", "webhook",
    /// "update-check"). Server operators use this for triage and rate-limiting
    /// without parsing the User-Agent comment.
    /// </summary>
    public const string ComponentHeader = "X-Netclaw-Component";

    /// <summary>
    /// The shared User-Agent value. Resolved lazily from <see cref="BuildInfo"/>
    /// the first time it is read, so callers that set
    /// <see cref="BuildInfo.TargetAssembly"/> early in startup see the override.
    /// </summary>
    public static string Value => _lazyValue.Value;

    private static readonly Lazy<string> _lazyValue = new(BuildValue, isThreadSafe: true);

    private static string BuildValue()
    {
        var version = BuildInfo.Version;
        var sha = BuildInfo.CommitHash;
        return $"Netclaw/{version} (+https://netclaw.dev; sha={sha})";
    }
}

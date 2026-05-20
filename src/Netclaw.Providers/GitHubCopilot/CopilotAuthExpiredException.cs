// -----------------------------------------------------------------------
// <copyright file="CopilotAuthExpiredException.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Providers.GitHubCopilot;

/// <summary>
/// Thrown when the GitHub OAuth token presented to
/// <c>/copilot_internal/v2/token</c> is rejected with HTTP 401.
/// The stored OAuth token is left intact — the operator must explicitly
/// re-run the device flow by removing and re-adding the provider entry
/// (<c>netclaw provider remove &lt;name&gt;</c> followed by
/// <c>netclaw provider add &lt;name&gt; github-copilot --auth oauth-device</c>).
/// </summary>
public sealed class CopilotAuthExpiredException : Exception
{
    public CopilotAuthExpiredException()
        : base("GitHub Copilot authorization expired. Run 'netclaw provider remove <name>' "
            + "then 'netclaw provider add <name> github-copilot --auth oauth-device' to re-authenticate.")
    {
    }
}

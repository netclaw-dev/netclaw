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
/// re-run the device flow via <c>netclaw provider fix &lt;name&gt;</c>.
/// </summary>
public sealed class CopilotAuthExpiredException : Exception
{
    public CopilotAuthExpiredException()
        : base("GitHub Copilot authorization expired. Run 'netclaw provider fix <name>' to re-authenticate.")
    {
    }
}

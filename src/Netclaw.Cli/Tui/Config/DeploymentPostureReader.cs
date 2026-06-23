// -----------------------------------------------------------------------
// <copyright file="DeploymentPostureReader.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Config;
using Netclaw.Configuration;

namespace Netclaw.Cli.Tui.Config;

/// <summary>
/// Single source of truth for reading <c>Security.DeploymentPosture</c> from config. A MISSING key is
/// the normal "not yet configured" state and defaults to Personal. A PRESENT but unrecognized value
/// (renamed enum member, stale numeric, hand-edited typo) is a misconfiguration: it fails CLOSED to
/// Public — the most restrictive posture, matching the daemon's <see cref="TrustContextPolicy"/>
/// fallback — and reports the raw value via <paramref name="invalidValue"/>. Both the Security and
/// Channels editors read posture through here so the same corrupt value degrades consistently instead
/// of failing closed on one page and throwing into the constructor of the other.
/// </summary>
internal static class DeploymentPostureReader
{
    public static bool TryRead(Dictionary<string, object> config, out DeploymentPosture posture, out string? invalidValue)
    {
        invalidValue = null;
        if (!ConfigFileHelper.TryGetPathValue(config, "Security.DeploymentPosture", out var value))
        {
            posture = DeploymentPosture.Personal;
            return true;
        }

        if (value is string text && Enum.TryParse<DeploymentPosture>(text, ignoreCase: true, out var parsed))
        {
            posture = parsed;
            return true;
        }

        posture = DeploymentPosture.Public;
        invalidValue = value?.ToString() ?? "(null)";
        return false;
    }
}

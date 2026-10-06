// -----------------------------------------------------------------------
// <copyright file="DeploymentPostureReader.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Config;
using Netclaw.Configuration;

namespace Netclaw.Cli.Tui.Config;

/// <summary>
/// Single source of truth for reading <c>Security.DeploymentPosture</c> from config in the editors.
/// A MISSING key resolves the same way as the daemon (<see cref="SecurityPolicyDefaults"/>): Public,
/// unless <c>Security.StrictDefaults</c> is false. An editor that showed Personal there reported
/// "already active" for a posture that the daemon did not use. A PRESENT but unrecognized value
/// (renamed enum member, stale numeric, hand-edited typo) is a misconfiguration: it fails CLOSED to
/// Public and reports the raw value via <paramref name="invalidValue"/>. Both the Security and
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
            // The daemon binder also accepts the text "false".
            var strictDefaults = !ConfigFileHelper.TryGetPathValue(config, "Security.StrictDefaults", out var strict)
                || !(strict is false || (strict is string strictText && bool.TryParse(strictText, out var strictValue) && !strictValue));
            posture = SecurityPolicyDefaults.ResolveDeploymentPosture(configured: null, strictDefaults);
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

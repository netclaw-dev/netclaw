// -----------------------------------------------------------------------
// <copyright file="ChatOnboarding.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Config;
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;

namespace Netclaw.Cli;

/// <summary>
/// <c>netclaw chat --onboarding</c>: starts a chat whose first turn is the same onboarding
/// trigger the init wizard and the redo identity flow send, built from the saved identity.
/// </summary>
internal static class ChatOnboarding
{
    public const string Flag = "--onboarding";

    /// <summary>Returns an error for <c>--onboarding=&lt;value&gt;</c>, which would otherwise open a plain chat.</summary>
    public static string? ValidateToken(string arg)
        => arg.StartsWith(Flag + "=", StringComparison.Ordinal)
            ? "netclaw: --onboarding takes no value. Use `netclaw chat --onboarding`."
            : null;

    /// <summary>Returns why <c>--onboarding</c> cannot combine with the other chat options, or null.</summary>
    public static string? ValidateCombination(bool headless, string? resumeSessionId)
    {
        if (headless)
            return "netclaw: chat --onboarding starts an interactive interview and cannot be combined with -p.";

        if (resumeSessionId is not null)
            return "netclaw: chat --onboarding starts a new session and cannot be combined with --resume.";

        return null;
    }

    /// <summary>
    /// The extra line printed after "Could not reach the Netclaw daemon" when the failed chat
    /// was meant to start the interview, so the trigger is not lost. Null for any other chat.
    /// </summary>
    public static string? DaemonUnavailableHint(ChatNavigationState? navigation)
        => navigation?.IsOnboarding == true
            ? "Once the daemon is up, run `netclaw chat --onboarding` to start the identity interview."
            : null;

    public static string BuildTrigger(NetclawPaths paths)
    {
        // The daemon and plain `netclaw chat` accept a netclaw.json with comments or trailing
        // commas. This reader does not, so such a file gives the wizard defaults, not a crash.
        var config = ConfigFileHelper.TryLoadJsonDictOrNull(paths.NetclawConfigPath, out _);
        return IdentityStepViewModel.BuildOnboardingTrigger(
            paths,
            ReadString(config, "Identity.UserName"),
            ReadString(config, "Identity.CommunicationStyle"));
    }

    private static string? ReadString(Dictionary<string, object>? config, string path)
        => config is not null && ConfigFileHelper.TryGetPathValue(config, path, out var value)
            ? value as string
            : null;
}

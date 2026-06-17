// -----------------------------------------------------------------------
// <copyright file="MattermostStepViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Cli.Mattermost;
using Netclaw.Configuration;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Wizard step for configuring Mattermost integration.
/// 8 sub-steps (allowed user IDs is shown only when restricting to specific users):
/// enable -> server URL -> bot token -> channel IDs -> DM enabled -> user access choice ->
/// allowed user IDs (conditional) -> callback URL.
/// Mattermost is self-hosted, so the server URL is collected up front and there is no
/// auth probe. The health-check step resolves channel references to canonical IDs against
/// the live server (mirroring Slack/Discord) so only matchable IDs persist into the ACL;
/// connectivity itself is validated locally.
/// </summary>
public sealed class MattermostStepViewModel : IWizardStepViewModel, IChannelAdapterViewModel
{
    private readonly IMattermostProbe _mattermostProbe;
    private int _currentSubStep;
    private int _highWaterSubStep;
    private WizardContext? _context;

    public MattermostStepViewModel(IMattermostProbe mattermostProbe)
    {
        _mattermostProbe = mattermostProbe;
    }

    public string StepId => WizardStepIds.Mattermost;
    public string DisplayTitle => "Mattermost";

    public bool MattermostEnabled { get; set; }

    bool IChannelAdapterViewModel.AdapterEnabled
    {
        get => MattermostEnabled;
        set => MattermostEnabled = value;
    }

    int IChannelAdapterViewModel.ConfiguredChannelCount =>
        ParseChannelIds(ChannelIdsInput).Count;

    public string? ServerUrl { get; set; }
    public string? BotToken { get; set; }
    internal string? ServerUrlDraft { get; set; }
    internal string? BotTokenDraft { get; set; }
    public bool HasPersistedBotToken { get; set; }
    public string? ChannelIdsInput { get; set; }
    public bool AllowDirectMessages { get; set; }
    public bool RestrictToSpecificUsers { get; set; }
    public string? AllowedUserIdsInput { get; set; }
    public string? CallbackUrl { get; set; }
    internal string? CallbackUrlDraft { get; set; }

    // Most recent channel-reference resolution against the live Mattermost server. Drives both
    // the red-flag rendering of unresolved rows and the canonical-ID persistence in
    // ContributeConfig / BuildChannelAudiences (names never persist verbatim into the ACL).
    internal MattermostChannelResolutionResult? LastChannelResolution { get; set; }

    internal bool SkipEnableSubStep { get; set; }

    public bool IsApplicable(WizardContext context) => true;

    public int CurrentSubStep => _currentSubStep;

    // Sub-steps when enabled: server URL, bot token, channel IDs, DM enabled,
    // user access choice, callback URL (+ allowed user IDs when restricting).
    public int SubStepCount => MattermostEnabled
        ? (SkipEnableSubStep ? 6 : 7) + (RestrictToSpecificUsers ? 1 : 0)
        : 1;

    public string GetHelpText() => _currentSubStep switch
    {
        0 => "  Enable Mattermost to connect Netclaw with a bot token.",
        1 => "  Enter the base URL of your self-hosted Mattermost server (e.g. https://mm.example.com).",
        2 => "  Enter the Mattermost bot account access token.",
        3 => "  Allowed channel IDs are comma-separated. Leave blank for no channel ingress.",
        4 => "  Enable DMs only when you want Mattermost direct messages to be accepted.",
        5 => "  Choose whether to restrict bot interactions to specific Mattermost user IDs.",
        6 => "  Enter the Mattermost user IDs who should have access. At least one ID is required.",
        7 => "  Optional. Required only for interactive approval buttons. Leave blank to use text replies.",
        _ => ""
    };

    public bool TryAdvance()
    {
        if (_currentSubStep == 0 && MattermostEnabled)
        {
            _currentSubStep = 1;
            _highWaterSubStep = 1;
            return true;
        }

        // Linear advance through server URL -> bot token -> channel IDs -> DM enabled.
        if (_currentSubStep >= 1 && _currentSubStep < 4 && MattermostEnabled)
        {
            _currentSubStep++;
            _highWaterSubStep = _currentSubStep;
            return true;
        }

        // User access choice: branch into allowed user IDs (5 -> 6) or skip to callback URL.
        if (_currentSubStep == 4 && MattermostEnabled)
        {
            _currentSubStep = 5;
            _highWaterSubStep = 5;
            return true;
        }

        if (_currentSubStep == 5 && MattermostEnabled)
        {
            if (RestrictToSpecificUsers)
            {
                _currentSubStep = 6;
                _highWaterSubStep = 6;
                return true;
            }

            AllowedUserIdsInput = null;
            _currentSubStep = 7;
            _highWaterSubStep = 7;
            return true;
        }

        if (_currentSubStep == 6 && MattermostEnabled)
        {
            _currentSubStep = 7;
            _highWaterSubStep = 7;
            return true;
        }

        // Callback URL (7) is the last sub-step — completes the step.
        return false;
    }

    public bool TryGoBack()
    {
        var minSubStep = SkipEnableSubStep ? 1 : 0;

        // Skip over the allowed-user-IDs sub-step when not restricting.
        if (_currentSubStep == 7 && !RestrictToSpecificUsers)
        {
            _currentSubStep = 5;
            return true;
        }

        if (_currentSubStep > minSubStep)
        {
            _currentSubStep--;
            return true;
        }

        return false;
    }

    public void OnEnter(WizardContext context, NavigationDirection direction)
    {
        _context = context;
        var startSubStep = SkipEnableSubStep ? 1 : 0;
        if (direction == NavigationDirection.Back)
            _currentSubStep = _highWaterSubStep;
        else
            _currentSubStep = startSubStep;
    }

    void IChannelAdapterViewModel.ResetConfig() => ResetConfig();

    internal void ResetConfig()
    {
        MattermostEnabled = false;
        ServerUrl = null;
        BotToken = null;
        ServerUrlDraft = null;
        BotTokenDraft = null;
        ChannelIdsInput = null;
        AllowDirectMessages = false;
        RestrictToSpecificUsers = false;
        AllowedUserIdsInput = null;
        CallbackUrl = null;
        CallbackUrlDraft = null;
        var startSubStep = SkipEnableSubStep ? 1 : 0;
        _currentSubStep = startSubStep;
        _highWaterSubStep = startSubStep;
    }

    public void OnLeave()
    {
        if (_context is null)
            return;

        _context.AnyChatServicesEnabled = _context.AnyChatServicesEnabled || MattermostEnabled;

        if (!MattermostEnabled)
        {
            _context.ChannelEntries.Remove(ChannelType.Mattermost);
            return;
        }

        var posture = _context.SelectedPosture ?? DeploymentPosture.Personal;
        var entries = new List<ChannelEntry>();

        if (AllowDirectMessages)
        {
            var allowedUsers = ParseUserIds(AllowedUserIdsInput);
            var dmAudience = ChannelAudienceDefaults.ForDirectMessage(posture, allowedUsers.Count);
            entries.Add(new ChannelEntry("Mattermost DMs", "dm", dmAudience, isDmRow: true));
        }

        var channelAudience = ChannelAudienceDefaults.ForChannel(posture);

        var channelIds = ParseChannelIds(ChannelIdsInput);
        foreach (var channelId in channelIds)
            entries.Add(new ChannelEntry($"Mattermost:{channelId}", channelId, channelAudience));

        _context.ChannelEntries[ChannelType.Mattermost] = entries;
    }

    public void ContributeConfig(WizardConfigBuilder builder)
    {
        if (!MattermostEnabled)
            return;

        var userIds = ParseUserIds(AllowedUserIdsInput);

        // Persist only canonical channel IDs the runtime ACL can match. An unresolved channel
        // reference (a name/slug the bot can't see) is omitted, not written verbatim — an
        // unmatchable entry in AllowedChannelIds is inert and grants nothing. Mirrors Slack/Discord.
        var resolvedChannelIds = LastChannelResolution is { Resolved.Count: > 0 } resolution
            ? resolution.Resolved.Select(channel => channel.ChannelId).ToList()
            : new List<string>();

        builder.Mattermost = new MattermostConfigSection
        {
            Enabled = true,
            ServerUrl = string.IsNullOrWhiteSpace(ServerUrl) ? null : ServerUrl.Trim(),
            CallbackUrl = string.IsNullOrWhiteSpace(CallbackUrl) ? null : CallbackUrl.Trim(),
            DefaultChannelId = resolvedChannelIds.FirstOrDefault(),
            AllowedChannelIds = resolvedChannelIds.Count > 0 ? resolvedChannelIds : null,
            AllowDirectMessages = AllowDirectMessages,
            AllowedUserIds = userIds.Count > 0 ? userIds : null,
            ChannelAudiences = BuildChannelAudiences()
        };
    }

    public void ContributeSecrets(WizardSecretsBuilder builder)
    {
        if (!MattermostEnabled || string.IsNullOrWhiteSpace(BotToken))
            return;

        builder.AddSection("Mattermost", new Dictionary<string, object>
        {
            ["BotToken"] = BotToken
        });
    }

    public async Task ContributeHealthChecksAsync(HealthCheckRunner runner, CancellationToken ct)
    {
        if (!runner.BeginAdapterCheck("Mattermost", MattermostEnabled, (ServerUrl, "server URL"), (BotToken, "bot token")))
            return;

        // Mattermost is self-hosted with no first-party auth-probe API; the daemon
        // verifies connectivity on startup. The wizard validates configuration locally.
        runner.UpdateLast(new HealthCheckItem(
            $"Mattermost configured (server: {ServerUrl})", true));

        // Resolve channel references (id / slug / display name) against the live server so the
        // persisted allow-list holds canonical channel IDs the runtime ACL can match — an
        // unresolved name in AllowedChannelIds is inert. Mirrors Slack/Discord. BeginAdapterCheck
        // above already guaranteed ServerUrl and BotToken are present.
        var parsedChannelIds = ParseChannelIds(ChannelIdsInput);
        if (parsedChannelIds.Count == 0)
            return;

        runner.Add(new HealthCheckItem("Resolving Mattermost channels", null));
        try
        {
            LastChannelResolution = await _mattermostProbe.ResolveChannelIdsAsync(
                ServerUrl!, BotToken!, parsedChannelIds, ct);

            if (LastChannelResolution.ErrorMessage is not null)
            {
                runner.UpdateLast(new HealthCheckItem(
                    $"Mattermost channel lookup failed: {LastChannelResolution.ErrorMessage}", false));
            }
            else if (LastChannelResolution.Unresolved.Count > 0)
            {
                var notFound = string.Join(", ", LastChannelResolution.Unresolved);
                runner.UpdateLast(new HealthCheckItem(
                    $"Mattermost channels: resolved {LastChannelResolution.Resolved.Count}/{parsedChannelIds.Count}, not found: {notFound}",
                    false));
            }
            else
            {
                runner.UpdateLast(new HealthCheckItem(
                    $"Mattermost channels resolved ({LastChannelResolution.Resolved.Count})", true));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            runner.UpdateLast(new HealthCheckItem(
                "Mattermost channel resolution timed out. Check your network connection.", false));
        }
    }

    private Dictionary<string, string>? BuildChannelAudiences()
    {
        if (_context is null)
            return null;

        if (!_context.ChannelEntries.TryGetValue(ChannelType.Mattermost, out var entries))
            return null;

        var audiences = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            // Only write an audience under a key the runtime ACL can match — a resolved channel ID
            // or the literal "dm" DM key. An unresolved channel reference is a dead key the runtime
            // never matches, so omit it instead of silently writing inert ACL config (a
            // no-silent-fallback violation on a security path). Mirrors Slack/Discord.
            if (TryResolveChannelAudienceKey(entry, out var key))
                audiences[key] = entry.Audience.ToWireValue();
        }

        return audiences.Count > 0 ? audiences : null;
    }

    private bool TryResolveChannelAudienceKey(ChannelEntry entry, out string key)
    {
        if (entry.IsDmRow)
        {
            key = entry.Id; // canonical DM key ("dm")
            return true;
        }

        key = string.Empty;
        if (LastChannelResolution is null)
            return false;

        var resolved = LastChannelResolution.Resolved.FirstOrDefault(channel =>
            string.Equals(channel.ChannelName, entry.Id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(channel.ChannelId, entry.Id, StringComparison.Ordinal));

        if (string.IsNullOrWhiteSpace(resolved?.ChannelId))
            return false;

        key = resolved.ChannelId;
        return true;
    }

    internal static List<string> ParseChannelIds(string? input)
        => string.IsNullOrWhiteSpace(input)
            ? []
            : [.. input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.Trim().TrimStart('#'))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)];

    private static List<string> ParseUserIds(string? input)
        => WizardStepHelpers.ParseUserIds(input);

    public void Dispose()
    {
    }
}

// -----------------------------------------------------------------------
// <copyright file="TeamsStepViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Channels.Teams;
using Netclaw.Configuration;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Configures the Teams adapter without treating display names as authorization
/// values. Graph discovery can enrich these values later; the manual path always
/// accepts only canonical IDs.
/// </summary>
public sealed class TeamsStepViewModel : IWizardStepViewModel, IChannelAdapterViewModel
{
    private int _currentSubStep;
    private int _highWaterSubStep;
    private WizardContext? _context;

    public string StepId => "teams";
    public string DisplayTitle => "Microsoft Teams";
    public bool TeamsEnabled { get; set; }
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? BotId { get; set; }
    public string? ClientSecret { get; set; }
    public bool HasPersistedClientSecret { get; set; }
    public string? TeamIdsInput { get; set; }
    public string? ChannelIdsInput { get; set; }
    public bool AllowDirectMessages { get; set; }
    public bool AllowGroupChats { get; set; }
    public bool AllowAttachments { get; set; }
    public string? AllowedGroupChatIdsInput { get; set; }
    public string? AllowedUserIdsInput { get; set; }
    public string? AllowedGroupIdsInput { get; set; }
    public List<TeamsChannelAudienceOverride> ChannelAudienceOverrides { get; set; } = [];
    public List<TeamsChannelAccessOverride> ChannelAccessOverrides { get; set; } = [];
    public bool MentionOnly { get; set; } = true;
    internal bool SkipEnableSubStep { get; set; }

    bool IChannelAdapterViewModel.AdapterEnabled
    {
        get => TeamsEnabled;
        set => TeamsEnabled = value;
    }

    int IChannelAdapterViewModel.ConfiguredChannelCount => ParseCanonicalIds(ChannelIdsInput).Count;

    public bool IsApplicable(WizardContext context) => true;
    public int CurrentSubStep => _currentSubStep;
    public int SubStepCount => TeamsEnabled ? (SkipEnableSubStep ? 9 : 10) : 1;

    public string GetHelpText() => _currentSubStep switch
    {
        0 => "  Enable Microsoft Teams to configure its Entra application credentials.",
        1 => "  Enter the canonical Entra tenant ID.",
        2 => "  Enter the Entra application (client) ID.",
        3 => "  Enter the Teams bot registration ID.",
        4 => "  Enter the client secret. It is stored only in the encrypted secrets overlay.",
        5 => "  Enter canonical Teams IDs. Graph discovery is available after credentials are configured.",
        6 => "  Enter canonical channel IDs. Use Graph discovery where available or the advanced manual path.",
        7 => "  Enable DMs only for explicitly allowed users or verified allowed group members.",
        8 => "  Enter canonical Entra user IDs allowed globally.",
        9 => "  Enter canonical Entra group IDs allowed globally.",
        _ => string.Empty
    };

    public bool TryAdvance()
    {
        if (!TeamsEnabled || _currentSubStep >= 9)
            return false;

        _currentSubStep++;
        _highWaterSubStep = _currentSubStep;
        return true;
    }

    public bool TryGoBack()
    {
        var minimum = SkipEnableSubStep ? 1 : 0;
        if (_currentSubStep <= minimum)
            return false;

        _currentSubStep--;
        return true;
    }

    public void OnEnter(WizardContext context, NavigationDirection direction)
    {
        _context = context;
        _currentSubStep = direction == NavigationDirection.Back ? _highWaterSubStep : SkipEnableSubStep ? 1 : 0;
    }

    public void OnLeave()
    {
        if (_context is null)
            return;

        if (!TeamsEnabled)
        {
            _context.ChannelEntries.Remove(ChannelType.Teams);
            return;
        }

        _context.AnyChatServicesEnabled = true;
        var posture = _context.SelectedPosture ?? DeploymentPosture.Personal;
        var entries = ParseCanonicalIds(ChannelIdsInput)
            .Select(id => new ChannelEntry($"Teams:{id}", id, ChannelAudienceDefaults.ForChannel(posture)))
            .ToList();
        if (AllowDirectMessages)
            entries.Add(new ChannelEntry("Microsoft Teams DMs", "dm", ChannelAudienceDefaults.ForDirectMessage(posture, ParseCanonicalIds(AllowedUserIdsInput).Count), isDmRow: true));
        _context.ChannelEntries[ChannelType.Teams] = entries;
    }

    public void ContributeConfig(WizardConfigBuilder builder)
    {
        if (!TeamsEnabled)
            return;

        builder.Teams = new TeamsConfigSection
        {
            Enabled = true,
            TenantId = Normalize(TenantId),
            ClientId = Normalize(ClientId),
            BotId = Normalize(BotId),
            AllowDirectMessages = AllowDirectMessages,
            AllowGroupChats = AllowGroupChats,
            AllowAttachments = AllowAttachments,
            MentionOnly = MentionOnly,
            AllowedTeamIds = ToList(TeamIdsInput),
            AllowedChannelIds = ToList(ChannelIdsInput),
            AllowedGroupChatIds = ToList(AllowedGroupChatIdsInput),
            AllowedUserIds = ToList(AllowedUserIdsInput),
            AllowedGroupIds = ToList(AllowedGroupIdsInput),
            ChannelAudienceOverrides = ChannelAudienceOverrides.Count == 0 ? null : [.. ChannelAudienceOverrides],
            ChannelAccessOverrides = ChannelAccessOverrides.Count == 0 ? null : [.. ChannelAccessOverrides]
        };
    }

    public void ContributeSecrets(WizardSecretsBuilder builder)
    {
        var secret = Normalize(ClientSecret);
        if (TeamsEnabled && secret is not null)
            builder.AddSection("Teams", new Dictionary<string, object> { ["ClientSecret"] = secret });
    }

    public Task ContributeHealthChecksAsync(HealthCheckRunner runner, CancellationToken ct)
    {
        if (!runner.BeginAdapterCheck("Microsoft Teams", TeamsEnabled, (TenantId, "tenant ID"), (ClientId, "application ID"), (BotId, "bot ID"), (ClientSecret, "client secret")))
            return Task.CompletedTask;

        runner.UpdateLast(new HealthCheckItem("Microsoft Teams configured. Directory access is checked at runtime.", true));
        return Task.CompletedTask;
    }

    void IChannelAdapterViewModel.ResetConfig() => ResetConfig();

    internal void ResetConfig()
    {
        TeamsEnabled = false;
        TenantId = null;
        ClientId = null;
        BotId = null;
        ClientSecret = null;
        HasPersistedClientSecret = false;
        TeamIdsInput = null;
        ChannelIdsInput = null;
        AllowDirectMessages = false;
        AllowGroupChats = false;
        AllowAttachments = false;
        AllowedGroupChatIdsInput = null;
        AllowedUserIdsInput = null;
        AllowedGroupIdsInput = null;
        ChannelAudienceOverrides = [];
        ChannelAccessOverrides = [];
        MentionOnly = true;
        _currentSubStep = SkipEnableSubStep ? 1 : 0;
        _highWaterSubStep = _currentSubStep;
    }

    private static List<string>? ToList(string? values)
    {
        var parsed = ParseCanonicalIds(values);
        return parsed.Count == 0 ? null : parsed;
    }

    private static List<string> ParseCanonicalIds(string? values)
        => string.IsNullOrWhiteSpace(values)
            ? []
            : [.. values.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)];

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public void Dispose()
    {
    }
}

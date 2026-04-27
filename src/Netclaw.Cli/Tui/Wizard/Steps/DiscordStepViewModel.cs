using Netclaw.Actors.Channels;
using Netclaw.Cli.Discord;
using Netclaw.Configuration;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Wizard step for configuring Discord integration.
/// 5 sub-steps: enable -> bot token -> allowed channel IDs -> DM enabled -> allowed user IDs.
/// </summary>
public sealed class DiscordStepViewModel : IWizardStepViewModel, IChannelAdapterViewModel
{
    private readonly IDiscordProbe _discordProbe;
    private int _currentSubStep;
    private int _highWaterSubStep;
    private WizardContext? _context;
    private CancellationTokenSource? _resolutionCts;

    public DiscordStepViewModel(IDiscordProbe discordProbe)
    {
        _discordProbe = discordProbe;
    }

    public string StepId => "discord";
    public string DisplayTitle => "Discord";

    public bool DiscordEnabled { get; set; }

    bool IChannelAdapterViewModel.AdapterEnabled
    {
        get => DiscordEnabled;
        set => DiscordEnabled = value;
    }

    int IChannelAdapterViewModel.ConfiguredChannelCount =>
        ParseChannelIds(ChannelIdsInput).Count;

    public string? BotToken { get; set; }
    public string? ChannelIdsInput { get; set; }
    public bool AllowDirectMessages { get; set; }
    public string? AllowedUserIdsInput { get; set; }
    internal DiscordChannelResolutionResult? LastChannelResolution { get; set; }

    internal bool SkipEnableSubStep { get; set; }

    public bool IsApplicable(WizardContext context) => true;

    public int CurrentSubStep => _currentSubStep;
    public int SubStepCount => DiscordEnabled ? (SkipEnableSubStep ? 4 : 5) : 1;

    public string GetHelpText() => _currentSubStep switch
    {
        0 => "  Enable Discord to connect Netclaw with a bot token.",
        1 => "  Enter the Discord bot token from your application settings.",
        2 => "  Allowed channel IDs are comma-separated. Leave blank for no guild channel ingress.",
        3 => "  Enable DMs only when you want Discord direct messages to be accepted.",
        4 => "  Restrict Discord DM/message access to specific user IDs. Leave blank to allow any user in allowed conversations.",
        _ => ""
    };

    public bool TryAdvance()
    {
        if (_currentSubStep == 0 && DiscordEnabled)
        {
            _currentSubStep = 1;
            _highWaterSubStep = 1;
            return true;
        }

        if (_currentSubStep >= 1 && _currentSubStep < 4 && DiscordEnabled)
        {
            if (_currentSubStep == 2)
                StartBackgroundChannelResolution();

            _currentSubStep++;
            _highWaterSubStep = _currentSubStep;
            return true;
        }

        return false;
    }

    public bool TryGoBack()
    {
        var minSubStep = SkipEnableSubStep ? 1 : 0;
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
        DiscordEnabled = false;
        BotToken = null;
        ChannelIdsInput = null;
        AllowDirectMessages = false;
        AllowedUserIdsInput = null;
        LastChannelResolution = null;
        var startSubStep = SkipEnableSubStep ? 1 : 0;
        _currentSubStep = startSubStep;
        _highWaterSubStep = startSubStep;
    }

    public void OnLeave()
    {
        if (_context is null)
            return;

        _context.AnyChatServicesEnabled = _context.AnyChatServicesEnabled || DiscordEnabled;

        if (!DiscordEnabled)
        {
            _context.ChannelEntries.Remove(ChannelType.Discord);
            return;
        }

        var posture = _context.SelectedPosture ?? DeploymentPosture.Personal;
        var entries = new List<ChannelEntry>();

        if (AllowDirectMessages)
        {
            var allowedUsers = ParseUserIds(AllowedUserIdsInput);
            var dmAudience = allowedUsers.Count == 1
                ? TrustAudience.Personal
                : posture == DeploymentPosture.Personal
                    ? TrustAudience.Personal
                    : posture == DeploymentPosture.Team
                        ? TrustAudience.Team
                        : TrustAudience.Public;
            entries.Add(new ChannelEntry("Discord DMs", "dm", dmAudience, isDmRow: true));
        }

        var channelAudience = posture == DeploymentPosture.Public
            ? TrustAudience.Public
            : TrustAudience.Team;

        var channelIds = ParseChannelIds(ChannelIdsInput);
        foreach (var channelId in channelIds)
            entries.Add(new ChannelEntry($"Discord:{channelId}", channelId, channelAudience));

        _context.ChannelEntries[ChannelType.Discord] = entries;

        if (LastChannelResolution is not null)
            ApplyResolvedDisplayNames(entries);
    }

    public void ContributeConfig(WizardConfigBuilder builder)
    {
        if (!DiscordEnabled)
            return;

        var channelIds = ParseChannelIds(ChannelIdsInput);
        var userIds = ParseUserIds(AllowedUserIdsInput);

        builder.Discord = new DiscordConfigSection
        {
            Enabled = true,
            DefaultChannelId = channelIds.FirstOrDefault(),
            AllowedChannelIds = channelIds.Count > 0 ? channelIds : null,
            AllowDirectMessages = AllowDirectMessages,
            AllowedUserIds = userIds.Count > 0 ? userIds : null,
            ChannelAudiences = BuildChannelAudiences()
        };
    }

    public void ContributeSecrets(WizardSecretsBuilder builder)
    {
        if (!DiscordEnabled || string.IsNullOrWhiteSpace(BotToken))
            return;

        builder.AddSection("Discord", new Dictionary<string, object>
        {
            ["BotToken"] = BotToken
        });
    }

    public async Task ContributeHealthChecksAsync(HealthCheckRunner runner, CancellationToken ct)
    {
        runner.Add(new HealthCheckItem("Discord configuration", null));

        if (!DiscordEnabled)
        {
            runner.UpdateLast(new HealthCheckItem("Discord configuration (disabled)", true));
            return;
        }

        if (string.IsNullOrWhiteSpace(BotToken))
        {
            runner.UpdateLast(new HealthCheckItem("Discord configuration (bot token missing)", false));
            return;
        }

        bool discordAuthOk;
        try
        {
            var probeResult = await _discordProbe.ProbeAsync(BotToken!, ct);
            if (probeResult.Success)
            {
                runner.UpdateLast(new HealthCheckItem(
                    $"Discord bot authenticated (user: {probeResult.BotUsername})", true));
                discordAuthOk = true;
            }
            else
            {
                runner.UpdateLast(new HealthCheckItem(
                    $"Discord auth failed: {probeResult.ErrorMessage}", false));
                discordAuthOk = false;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            runner.UpdateLast(new HealthCheckItem(
                "Discord auth timed out. Check your network connection.", false));
            discordAuthOk = false;
        }

        var parsedChannelIds = ParseChannelIds(ChannelIdsInput);
        if (discordAuthOk && parsedChannelIds.Count > 0)
        {
            if (LastChannelResolution is { Success: true })
            {
                runner.Add(new HealthCheckItem(
                    $"Discord channels resolved ({LastChannelResolution.Resolved.Count})", true));
                ApplyResolvedDisplayNamesToContext();
                return;
            }

            runner.Add(new HealthCheckItem("Resolving Discord channels", null));

            try
            {
                LastChannelResolution = await _discordProbe.ResolveChannelIdsAsync(
                    BotToken!, parsedChannelIds, ct);

                if (LastChannelResolution.ErrorMessage is not null)
                {
                    runner.UpdateLast(new HealthCheckItem(
                        $"Discord channel lookup failed: {LastChannelResolution.ErrorMessage}", false));
                }
                else if (LastChannelResolution.Unresolved.Count > 0)
                {
                    var notFound = string.Join(", ", LastChannelResolution.Unresolved);
                    runner.UpdateLast(new HealthCheckItem(
                        $"Discord channels: resolved {LastChannelResolution.Resolved.Count}/{parsedChannelIds.Count}, not found: {notFound}",
                        false));
                }
                else
                {
                    runner.UpdateLast(new HealthCheckItem(
                        $"Discord channels resolved ({LastChannelResolution.Resolved.Count})", true));
                }

                ApplyResolvedDisplayNamesToContext();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                runner.UpdateLast(new HealthCheckItem(
                    "Discord channel resolution timed out. Check your network connection.", false));
            }
        }
    }

    private void StartBackgroundChannelResolution()
    {
        var channelIds = ParseChannelIds(ChannelIdsInput);
        if (string.IsNullOrWhiteSpace(BotToken) || channelIds.Count == 0)
            return;

        _resolutionCts?.Cancel();
        _resolutionCts?.Dispose();
        _resolutionCts = new CancellationTokenSource();
        var ct = _resolutionCts.Token;
        var token = BotToken!;
        var context = _context;

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _discordProbe.ResolveChannelIdsAsync(token, channelIds, ct);
                if (ct.IsCancellationRequested)
                    return;

                LastChannelResolution = result;

                if (context is not null &&
                    context.ChannelEntries.TryGetValue(ChannelType.Discord, out var entries))
                {
                    ApplyResolvedDisplayNames(entries);
                    context.RequestRedraw();
                }
            }
            catch (OperationCanceledException)
            {
                LastChannelResolution = null;
            }
            catch (HttpRequestException)
            {
                LastChannelResolution = null;
            }
        }, ct);
    }

    private void ApplyResolvedDisplayNames(List<ChannelEntry> entries)
    {
        if (LastChannelResolution is null)
            return;

        var resolvedLookup = LastChannelResolution.Resolved
            .ToDictionary(r => r.ChannelId, r => r, StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (!entry.IsDmRow && resolvedLookup.TryGetValue(entry.Id, out var resolved))
                entry.DisplayName = resolved.ToDisplayName();
        }
    }

    private void ApplyResolvedDisplayNamesToContext()
    {
        if (_context is null || LastChannelResolution is null)
            return;

        if (_context.ChannelEntries.TryGetValue(ChannelType.Discord, out var entries))
            ApplyResolvedDisplayNames(entries);
    }

    private Dictionary<string, string>? BuildChannelAudiences()
    {
        if (_context is null)
            return null;

        if (!_context.ChannelEntries.TryGetValue(ChannelType.Discord, out var entries))
            return null;

        var audiences = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
            audiences[entry.Id] = entry.Audience.ToWireValue();

        return audiences.Count > 0 ? audiences : null;
    }

    internal static List<string> ParseChannelIds(string? input)
        => string.IsNullOrWhiteSpace(input)
            ? []
            : input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.Trim().TrimStart('#'))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToList();

    private static List<string> ParseUserIds(string? input)
        => string.IsNullOrWhiteSpace(input)
            ? []
            : input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToList();

    public void Dispose()
    {
        _resolutionCts?.Cancel();
        _resolutionCts?.Dispose();
    }
}

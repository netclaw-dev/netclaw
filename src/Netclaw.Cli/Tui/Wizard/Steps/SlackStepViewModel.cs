using Netclaw.Actors.Channels;
using Netclaw.Channels.Slack;
using Netclaw.Configuration;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Wizard step for configuring Slack integration.
/// 6 sub-steps: enable → bot token → app token → channel names → DM enabled → allowed user IDs.
/// </summary>
public sealed class SlackStepViewModel : IWizardStepViewModel, IChannelAdapterViewModel
{
    private static readonly TimeSpan SlackProbeHardTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ChannelResolutionHardTimeout = TimeSpan.FromSeconds(35);

    private readonly ISlackProbe _slackProbe;
    private int _currentSubStep;
    private int _highWaterSubStep;
    private WizardContext? _context;

    public SlackStepViewModel(ISlackProbe slackProbe)
    {
        _slackProbe = slackProbe;
    }

    public string StepId => "slack";
    public string DisplayTitle => "Slack";

    // ── State ──
    public bool SlackEnabled { get; set; }

    bool IChannelAdapterViewModel.AdapterEnabled
    {
        get => SlackEnabled;
        set => SlackEnabled = value;
    }

    int IChannelAdapterViewModel.ConfiguredChannelCount =>
        ParseChannelNames(ChannelNamesInput).Count;
    public string? BotToken { get; set; }
    public string? AppToken { get; set; }
    public string? ChannelNamesInput { get; set; }
    public bool AllowDirectMessages { get; set; }
    public string? AllowedUserIdsInput { get; set; }
    internal SlackChannelResolutionResult? LastChannelResolution { get; set; }

    internal bool SkipEnableSubStep { get; set; }

    public bool IsApplicable(WizardContext context) => true;

    public int CurrentSubStep => _currentSubStep;
    public int SubStepCount => SlackEnabled ? (SkipEnableSubStep ? 5 : 6) : 1;

    public string GetHelpText() => _currentSubStep switch
    {
        0 => "  Enable Slack to connect Netclaw as a Slack bot.",
        1 => "  Socket Mode requires both tokens. See: https://api.slack.com/apis/socket-mode",
        2 => "  Socket Mode requires both tokens. See: https://api.slack.com/apis/socket-mode",
        3 => "  Channel names separated by commas. Bot needs channels:read scope to resolve.",
        4 => "  DMs create a private session per conversation. Each top-level DM starts a new session.",
        5 => "  Restrict Slack access to specific user IDs for both channels and DMs. Leave blank to allow all workspace members.",
        _ => ""
    };

    public bool TryAdvance()
    {
        if (_currentSubStep == 0 && SlackEnabled)
        {
            _currentSubStep = 1;
            _highWaterSubStep = 1;
            return true;
        }

        if (_currentSubStep >= 1 && _currentSubStep < 5 && SlackEnabled)
        {
            _currentSubStep++;
            _highWaterSubStep = _currentSubStep;
            return true;
        }

        return false; // step complete (disabled at sub-step 0, or sub-step 5 complete)
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
        SlackEnabled = false;
        BotToken = null;
        AppToken = null;
        ChannelNamesInput = null;
        AllowDirectMessages = false;
        AllowedUserIdsInput = null;
        LastChannelResolution = null;
        var startSubStep = SkipEnableSubStep ? 1 : 0;
        _currentSubStep = startSubStep;
        _highWaterSubStep = startSubStep;
    }

    public void OnLeave()
    {
        if (_context is null) return;

        // Publish to shared context — additive so multiple channel steps coexist
        _context.AnyChatServicesEnabled = _context.AnyChatServicesEnabled || SlackEnabled;

        // Populate channel entries for the Channels step to display/edit
        if (SlackEnabled)
        {
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
                entries.Add(new ChannelEntry("DMs", "dm", dmAudience, isDmRow: true));
            }

            var channelAudience = posture == DeploymentPosture.Public
                ? TrustAudience.Public
                : TrustAudience.Team;

            if (!string.IsNullOrWhiteSpace(ChannelNamesInput))
            {
                var names = ChannelNamesInput
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(n => n.TrimStart('#'))
                    .Distinct(StringComparer.OrdinalIgnoreCase);

                foreach (var name in names)
                    entries.Add(new ChannelEntry($"#{name}", name, channelAudience));
            }

            _context.ChannelEntries[ChannelType.Slack] = entries;
        }
        else
        {
            _context.ChannelEntries.Remove(ChannelType.Slack);
        }
    }

    public void ContributeConfig(WizardConfigBuilder builder)
    {
        if (!SlackEnabled)
            return;

        var userIds = ParseUserIds(AllowedUserIdsInput);

        builder.Slack = new SlackConfigSection
        {
            Enabled = true,
            AllowedChannelIds = LastChannelResolution is { Resolved.Count: > 0 }
                ? LastChannelResolution.Resolved.Select(r => r.Id).ToList()
                : null,
            AllowDirectMessages = AllowDirectMessages,
            AllowedUserIds = userIds.Count > 0 ? userIds : null,
            ChannelAudiences = BuildChannelAudiences()
        };
    }

    public void ContributeSecrets(WizardSecretsBuilder builder)
    {
        if (!SlackEnabled)
            return;

        var slackSecrets = new Dictionary<string, object>();
        if (!string.IsNullOrWhiteSpace(BotToken))
            slackSecrets["BotToken"] = BotToken;
        if (!string.IsNullOrWhiteSpace(AppToken))
            slackSecrets["AppToken"] = AppToken;

        if (slackSecrets.Count > 0)
            builder.AddSection("Slack", slackSecrets);
    }

    public async Task ContributeHealthChecksAsync(HealthCheckRunner runner, CancellationToken ct)
    {
        runner.Add(new HealthCheckItem("Slack configuration", null));

        if (!SlackEnabled)
        {
            runner.UpdateLast(new HealthCheckItem("Slack configuration (disabled)", true));
            return;
        }

        if (string.IsNullOrWhiteSpace(BotToken))
        {
            runner.UpdateLast(new HealthCheckItem("Slack configuration (bot token missing)", false));
            return;
        }

        // Probe Slack auth
        bool slackAuthOk;
        try
        {
            using var slackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            slackCts.CancelAfter(SlackProbeHardTimeout);
            var slackResult = await _slackProbe.ProbeAsync(BotToken!, slackCts.Token);
            if (slackResult.Success)
            {
                runner.UpdateLast(new HealthCheckItem(
                    $"Slack bot authenticated (team: {slackResult.TeamName})", true));
                slackAuthOk = true;
            }
            else
            {
                runner.UpdateLast(new HealthCheckItem(
                    $"Slack auth failed: {slackResult.ErrorMessage}", false));
                slackAuthOk = false;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            runner.UpdateLast(new HealthCheckItem(
                "Slack auth timed out (15s). Check your network connection.", false));
            slackAuthOk = false;
        }

        // Channel resolution
        var parsedChannelNames = ParseChannelNames(ChannelNamesInput);
        if (slackAuthOk && parsedChannelNames.Count > 0)
        {
            runner.Add(new HealthCheckItem("Resolving Slack channels", null));

            try
            {
                using var channelCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                channelCts.CancelAfter(ChannelResolutionHardTimeout);
                LastChannelResolution = await _slackProbe.ResolveChannelNamesAsync(
                    BotToken!, parsedChannelNames, channelCts.Token);

                if (LastChannelResolution.ErrorMessage is not null)
                {
                    runner.UpdateLast(new HealthCheckItem(
                        $"Slack channel lookup failed: {LastChannelResolution.ErrorMessage}", false));
                }
                else if (LastChannelResolution.Unresolved.Count > 0)
                {
                    var notFound = string.Join(", ", LastChannelResolution.Unresolved.Select(n => $"#{n}"));
                    runner.UpdateLast(new HealthCheckItem(
                        $"Slack channels: resolved {LastChannelResolution.Resolved.Count}/{parsedChannelNames.Count}, not found: {notFound}",
                        false));
                }
                else
                {
                    runner.UpdateLast(new HealthCheckItem(
                        $"Slack channels resolved ({LastChannelResolution.Resolved.Count})", true));
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                runner.UpdateLast(new HealthCheckItem(
                    "Slack channel resolution timed out (35s). Check your network connection.", false));
            }
        }
    }

    // ── Helpers ──

    private Dictionary<string, string>? BuildChannelAudiences()
    {
        if (_context is null)
            return null;

        // Read from the Slack bucket in the source-keyed channel entries
        if (!_context.ChannelEntries.TryGetValue(ChannelType.Slack, out var slackEntries))
            return null;

        var audiences = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in slackEntries)
            audiences[entry.Id] = entry.Audience.ToWireValue();

        return audiences.Count > 0 ? audiences : null;
    }

    internal static IReadOnlyList<string> ParseChannelNames(string? input)
        => string.IsNullOrWhiteSpace(input)
            ? []
            : input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(n => n.TrimStart('#'))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();

    private static List<string> ParseUserIds(string? input)
        => string.IsNullOrWhiteSpace(input)
            ? []
            : input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList();

    public void Dispose() { }
}

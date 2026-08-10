// -----------------------------------------------------------------------
// <copyright file="TelegramStepViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Actors.Channels;
using Netclaw.Cli.Telegram;
using Netclaw.Configuration;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Wizard step for configuring Telegram integration.
/// 6 sub-steps: enable -> bot token -> allowed channel IDs -> DM enabled -> user access choice -> allowed user IDs (conditional).
/// </summary>
public sealed class TelegramStepViewModel : IWizardStepViewModel, IChannelAdapterViewModel
{
    private readonly ITelegramProbe _telegramProbe;
    private int _currentSubStep;
    private int _highWaterSubStep;
    private WizardContext? _context;
    private CancellationTokenSource? _resolutionCts;
    private Task? _resolutionTask;

    public TelegramStepViewModel(ITelegramProbe telegramProbe)
    {
        _telegramProbe = telegramProbe;
    }

    public string StepId => WizardStepIds.Telegram;
    public string DisplayTitle => "Telegram";

    public bool TelegramEnabled { get; set; }

    bool IChannelAdapterViewModel.AdapterEnabled
    {
        get => TelegramEnabled;
        set => TelegramEnabled = value;
    }

    int IChannelAdapterViewModel.ConfiguredChannelCount =>
        ParseChannelIds(ChannelIdsInput).Count;

    public string? BotToken { get; set; }
    internal string? BotTokenDraft { get; set; }
    public bool HasPersistedBotToken { get; set; }
    public string? ChannelIdsInput { get; set; }
    public bool AllowDirectMessages { get; set; }
    public bool RestrictToSpecificUsers { get; set; }
    public string? AllowedUserIdsInput { get; set; }
    public bool MentionOnly { get; set; } = true;
    internal TelegramChatResolutionResult? LastChannelResolution { get; set; }

    internal bool SkipEnableSubStep { get; set; }

    public bool IsApplicable(WizardContext context) => true;

    public int CurrentSubStep => _currentSubStep;
    public int SubStepCount => TelegramEnabled
        ? (SkipEnableSubStep ? 5 : 6) + (RestrictToSpecificUsers ? 1 : 0)
        : 1;

    public string GetHelpText() => _currentSubStep switch
    {
        0 => "  Enable Telegram to connect Netclaw with a bot token.",
        1 => "  Enter the Telegram bot token from your application settings.",
        2 => "  Require a mention or a reply before the bot accepts a group message.",
        3 => "  Allowed group chat IDs are comma-separated numeric Telegram IDs.",
        4 => "  Enable DMs only when you want Telegram direct messages to be accepted.",
        5 => "  Choose whether to restrict bot interactions to specific Telegram user IDs.",
        6 => "  Enter the Telegram user IDs who should have access. At least one ID is required.",
        _ => ""
    };

    public bool TryAdvance()
    {
        if (_currentSubStep == 0 && TelegramEnabled)
        {
            _currentSubStep = 1;
            _highWaterSubStep = 1;
            return true;
        }

        if (_currentSubStep >= 1 && _currentSubStep < 5 && TelegramEnabled)
        {
            if (_currentSubStep == 3)
                StartBackgroundChannelResolution();

            _currentSubStep++;
            _highWaterSubStep = _currentSubStep;
            return true;
        }

        if (_currentSubStep == 5 && TelegramEnabled)
        {
            if (RestrictToSpecificUsers)
            {
                _currentSubStep = 6;
                _highWaterSubStep = 6;
                return true;
            }

            AllowedUserIdsInput = null;
            _highWaterSubStep = 5;
            return false;
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
        TelegramEnabled = false;
        BotToken = null;
        BotTokenDraft = null;
        ChannelIdsInput = null;
        AllowDirectMessages = false;
        RestrictToSpecificUsers = false;
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

        _context.AnyChatServicesEnabled = _context.AnyChatServicesEnabled || TelegramEnabled;

        if (!TelegramEnabled)
        {
            _context.ChannelEntries.Remove(ChannelType.Telegram);
            return;
        }

        var posture = _context.SelectedPosture ?? DeploymentPosture.Personal;
        var entries = new List<ChannelEntry>();

        if (AllowDirectMessages)
        {
            var allowedUsers = ParseUserIds(AllowedUserIdsInput);
            var dmAudience = ChannelAudienceDefaults.ForDirectMessage(posture, allowedUsers.Count);
            entries.Add(new ChannelEntry("Telegram DMs", "dm", dmAudience, isDmRow: true));
            foreach (var userId in allowedUsers)
                entries.Add(new ChannelEntry($"Telegram user {userId}", userId, dmAudience, isDmRow: true));
        }

        var channelAudience = ChannelAudienceDefaults.ForChannel(posture);

        var channelIds = ParseChannelIds(ChannelIdsInput);
        foreach (var channelId in channelIds)
            entries.Add(new ChannelEntry($"Telegram:{channelId}", channelId, channelAudience));

        _context.ChannelEntries[ChannelType.Telegram] = entries;

        if (LastChannelResolution is not null)
            ApplyResolvedDisplayNames(entries);
    }

    public void ContributeConfig(WizardConfigBuilder builder)
    {
        if (!TelegramEnabled)
            return;

        var userIds = ParseUserIds(AllowedUserIdsInput);

        // Persist only canonical channel IDs the runtime ACL can match. An unresolved channel
        // reference (a name/id the bot can't see) is omitted, not written verbatim — an
        // unmatchable entry in AllowedChannelIds is inert and grants nothing. Mirrors Slack/Mattermost.
        var resolvedChannelIds = LastChannelResolution is { Resolved.Count: > 0 } resolution
            ? resolution.Resolved.Select(chat => chat.ChatId).ToList()
            : new List<string>();

        builder.Telegram = new TelegramConfigSection
        {
            Enabled = true,
            AllowedChatIds = resolvedChannelIds.Count > 0 ? resolvedChannelIds : null,
            AllowDirectMessages = AllowDirectMessages,
            MentionOnly = MentionOnly,
            AllowedUserIds = userIds.Count > 0 ? userIds : null,
            ChatAudiences = BuildChannelAudiences()
        };
    }

    public void ContributeSecrets(WizardSecretsBuilder builder)
    {
        if (!TelegramEnabled || string.IsNullOrWhiteSpace(BotToken))
            return;

        builder.AddSection("Telegram", new Dictionary<string, object>
        {
            ["BotToken"] = BotToken
        });
    }

    public async Task ContributeHealthChecksAsync(HealthCheckRunner runner, CancellationToken ct)
    {
        // A background channel-name prefetch may still be in flight (kicked off when the user
        // advanced past the channel-IDs sub-step). Await it first so its LastChannelResolution
        // publish is observed here rather than racing the read below — and so its work is reused
        // instead of re-resolved.
        await AwaitPendingResolutionAsync();

        if (!runner.BeginAdapterCheck("Telegram", TelegramEnabled, (BotToken, "bot token")))
            return;

        bool telegramAuthOk;
        try
        {
            var probeResult = await _telegramProbe.ProbeAsync(BotToken!, ct);
            if (probeResult.Success)
            {
                runner.UpdateLast(new HealthCheckItem(
                    $"Telegram bot authenticated (user: {probeResult.BotUsername})", true));
                telegramAuthOk = true;
            }
            else
            {
                runner.UpdateLast(new HealthCheckItem(
                    $"Telegram auth failed: {probeResult.ErrorMessage}", false));
                telegramAuthOk = false;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            runner.UpdateLast(new HealthCheckItem(
                "Telegram auth timed out. Check your network connection.", false));
            telegramAuthOk = false;
        }

        var parsedChannelIds = ParseChannelIds(ChannelIdsInput);
        if (telegramAuthOk && parsedChannelIds.Count > 0)
        {
            if (LastChannelResolution is { Success: true })
            {
                runner.Add(new HealthCheckItem(
                    $"Telegram channels resolved ({LastChannelResolution.Resolved.Count})", true));
                ApplyResolvedDisplayNamesToContext();
                return;
            }

            runner.Add(new HealthCheckItem("Resolving Telegram channels", null));

            try
            {
                LastChannelResolution = await _telegramProbe.ResolveChatIdsAsync(
                    BotToken!, parsedChannelIds, ct);

                if (LastChannelResolution.ErrorMessage is not null)
                {
                    runner.UpdateLast(new HealthCheckItem(
                        $"Telegram channel lookup failed: {LastChannelResolution.ErrorMessage}", false));
                }
                else if (LastChannelResolution.Unresolved.Count > 0)
                {
                    var notFound = string.Join(", ", LastChannelResolution.Unresolved);
                    runner.UpdateLast(new HealthCheckItem(
                        $"Telegram channels: resolved {LastChannelResolution.Resolved.Count}/{parsedChannelIds.Count}, not found: {notFound}",
                        false));
                }
                else
                {
                    runner.UpdateLast(new HealthCheckItem(
                        $"Telegram channels resolved ({LastChannelResolution.Resolved.Count})", true));
                }

                ApplyResolvedDisplayNamesToContext();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                runner.UpdateLast(new HealthCheckItem(
                    "Telegram channel resolution timed out. Check your network connection.", false));
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
        var token = BotToken!;
        var context = _context;

        // Track the prefetch instead of firing Task.Run-and-forget. The network probe runs off the
        // loop, but its only published state is LastChannelResolution — an atomic reference write
        // guarded by the cancellation token. ChannelEntry.DisplayName is NOT mutated from this
        // background task; the resolved names are applied on the loop thread (OnLeave /
        // ApplyResolvedDisplayNamesToContext), so the render thread never reads a ChannelEntry that
        // a pool thread is concurrently mutating. The tracked task lets the health-check phase await
        // the publish before reading it.
        _resolutionTask = ResolveChannelLabelsAsync(token, channelIds, context, _resolutionCts.Token);
    }

    private async Task ResolveChannelLabelsAsync(
        string token, IReadOnlyList<string> channelIds, WizardContext? context, CancellationToken ct)
    {
        try
        {
            var result = await _telegramProbe.ResolveChatIdsAsync(token, channelIds, ct);

            // Re-check cancellation right before the publish. This narrows — but cannot fully
            // close — the window where a probe the user superseded (by editing the channel IDs
            // and re-advancing) lands a stale result; a late publish here is at worst a
            // cosmetically-wrong display name applied on the loop thread, never a crash, and the
            // authoritative resolution in ContributeHealthChecksAsync re-resolves as the source
            // of truth. Do not null out a prior result on cancellation/error below — a superseded
            // probe must not clobber a still-valid resolution.
            if (ct.IsCancellationRequested)
                return;

            // Atomic reference publish; OnLeave / ContributeHealthChecksAsync apply the display
            // names to ChannelEntries on the loop thread.
            LastChannelResolution = result;
            context?.RequestRedraw();
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or JsonException)
        {
            // Best-effort cosmetic prefetch: cancellation/supersession and a probe failure (network,
            // or a non-JSON Telegram error body that fails to parse) are normal runtime conditions that
            // must never fault the loop. Only the EXPECTED probe exceptions are caught here — an
            // unexpected fault still surfaces (via the tracked task / health-check re-resolve) rather
            // than being silently swallowed. On a genuine failure of the *current* probe clear our
            // stale state so a later read re-resolves; on cancellation leave any newer result intact
            // (a superseded probe must not clobber a still-valid resolution).
            if (!ct.IsCancellationRequested)
                LastChannelResolution = null;
        }
    }

    // Exposes the in-flight background channel-name prefetch so the health-check phase can observe
    // its publish on the loop thread, and so tests can await it without polling.
    internal Task? PendingResolution => _resolutionTask;

    // Awaits the in-flight prefetch (if any) so LastChannelResolution is published before a
    // loop-thread reader inspects it. The prefetch swallows its own probe failures, so the awaited
    // task always completes successfully — this never throws.
    private async Task AwaitPendingResolutionAsync()
    {
        var inFlight = _resolutionTask;
        if (inFlight is not null)
            await inFlight;
    }

    private void ApplyResolvedDisplayNames(List<ChannelEntry> entries)
    {
        if (LastChannelResolution is null)
            return;

        foreach (var entry in entries)
        {
            var resolved = LastChannelResolution.Resolved.FirstOrDefault(chat => SameTelegramId(chat.ChatId, entry.Id));
            if (!entry.IsDmRow && resolved is not null)
                entry.DisplayName = resolved.DisplayName;
        }
    }

    private void ApplyResolvedDisplayNamesToContext()
    {
        if (_context is null || LastChannelResolution is null)
            return;

        if (_context.ChannelEntries.TryGetValue(ChannelType.Telegram, out var entries))
            ApplyResolvedDisplayNames(entries);
    }

    private Dictionary<string, string>? BuildChannelAudiences()
    {
        if (_context is null)
            return null;

        if (!_context.ChannelEntries.TryGetValue(ChannelType.Telegram, out var entries))
            return null;

        var audiences = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            // Only write an audience under a key the runtime ACL can match — a resolved channel ID
            // or the literal "dm" DM key. An unresolved channel reference is a dead key the runtime
            // never matches, so omit it instead of silently writing inert ACL config (a
            // no-silent-fallback violation on a security path). Mirrors Slack/Mattermost.
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
            SameTelegramId(channel.ChatId, entry.Id));

        if (string.IsNullOrWhiteSpace(resolved?.ChatId))
            return false;

        key = resolved.ChatId;
        return true;
    }

    private static bool SameTelegramId(string left, string right)
        => long.TryParse(left, out var leftId)
           && long.TryParse(right, out var rightId)
           && leftId == rightId;

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
        _resolutionCts?.Cancel();
        _resolutionCts?.Dispose();
    }
}

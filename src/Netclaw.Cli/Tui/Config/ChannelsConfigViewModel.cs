// -----------------------------------------------------------------------
// <copyright file="ChannelsConfigViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Config;
using Netclaw.Configuration;
using R3;
using Termina.Reactive;

namespace Netclaw.Cli.Tui.Config;

public enum ChannelsConfigMode
{
    Providers,
    Details
}

public enum ChannelsConfigProvider
{
    Slack,
    Discord,
    Mattermost
}

public sealed record ChannelsConfigItem(
    ChannelsConfigProvider Provider,
    string Label,
    string Summary,
    string Description);

public sealed record ChannelsConfigDetail(string Label, string Value);

public sealed class ChannelsConfigViewModel : ReactiveViewModel
{
    private static readonly ChannelProviderSpec[] Providers =
    [
        new(
            ChannelsConfigProvider.Slack,
            "Slack",
            "Socket Mode chat adapter.",
            "Slack",
            ["Slack.BotToken", "Slack.AppToken"]),
        new(
            ChannelsConfigProvider.Discord,
            "Discord",
            "Discord bot adapter.",
            "Discord",
            ["Discord.BotToken"]),
        new(
            ChannelsConfigProvider.Mattermost,
            "Mattermost",
            "Mattermost bot adapter.",
            "Mattermost",
            ["Mattermost.BotToken"])
    ];

    private readonly NetclawPaths _paths;
    private readonly TuiNavigation? _navigation;

    public ChannelsConfigViewModel(NetclawPaths paths, TuiNavigation? navigation = null)
    {
        _paths = paths;
        _navigation = navigation;
    }

    public ReactiveProperty<ChannelsConfigMode> Mode { get; } = new(ChannelsConfigMode.Providers);
    public ReactiveProperty<int> SelectedIndex { get; } = new(0);
    public ReactiveProperty<string> StatusMessage { get; } = new("");

    internal bool ShutdownRequestedForTest { get; private set; }

    public IReadOnlyList<ChannelsConfigItem> Items => BuildItems();

    public ChannelsConfigItem SelectedItem => Items[Math.Clamp(SelectedIndex.Value, 0, Items.Count - 1)];

    public IReadOnlyList<ChannelsConfigDetail> SelectedDetails => BuildDetails(SelectedItem.Provider);

    public void MoveSelection(int delta)
    {
        var next = Math.Clamp(SelectedIndex.Value + delta, 0, Providers.Length - 1);
        if (next != SelectedIndex.Value)
            SelectedIndex.Value = next;
    }

    public void ActivateSelected()
    {
        if (Mode.Value == ChannelsConfigMode.Providers)
            OpenSelectedProvider();
    }

    internal void OpenSelectedProvider()
    {
        Mode.Value = ChannelsConfigMode.Details;
        StatusMessage.Value = "";
        RequestRedraw();
    }

    public void GoBack()
    {
        if (Mode.Value == ChannelsConfigMode.Details)
        {
            Mode.Value = ChannelsConfigMode.Providers;
            StatusMessage.Value = "";
            RequestRedraw();
            return;
        }

        if (TryGoBack())
            return;

        RequestQuit();
    }

    public void RequestQuit()
    {
        ShutdownRequestedForTest = true;
        Shutdown();
    }

    public override void Dispose()
    {
        Mode.Dispose();
        SelectedIndex.Dispose();
        StatusMessage.Dispose();
        base.Dispose();
    }

    private bool TryGoBack()
    {
        if (_navigation is null)
            return false;

        try
        {
            return _navigation.TryGoBack();
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private IReadOnlyList<ChannelsConfigItem> BuildItems()
    {
        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        return Providers
            .Select(provider => new ChannelsConfigItem(
                provider.Provider,
                provider.Label,
                ReadSummary(config, provider),
                provider.Description))
            .ToArray();
    }

    private IReadOnlyList<ChannelsConfigDetail> BuildDetails(ChannelsConfigProvider providerValue)
    {
        var provider = Providers.Single(p => p.Provider == providerValue);
        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        var configured = SectionPresent(config, provider.SectionName) || HasAnySecret(provider.SecretPaths);
        var enabled = configured && GetBool(config, $"{provider.SectionName}.Enabled", defaultValue: false);
        var channels = ReadConfiguredChannels(config, provider.SectionName);
        var users = GetStringArray(config, $"{provider.SectionName}.AllowedUserIds");
        var allowDms = GetBool(config, $"{provider.SectionName}.AllowDirectMessages", defaultValue: false);
        var mentionOnly = GetBool(config, $"{provider.SectionName}.MentionOnly", defaultValue: true);
        var mentionRequiredInDm = GetBool(config, $"{provider.SectionName}.MentionRequiredInDm", defaultValue: false);
        var audienceOverrides = GetDictionaryCount(config, $"{provider.SectionName}.ChannelAudiences");

        var details = new List<ChannelsConfigDetail>
        {
            new("Status", enabled ? "enabled" : configured ? "disabled" : "not configured")
        };

        AddCredentialDetails(details, provider);

        if (provider.Provider == ChannelsConfigProvider.Slack)
            details.Add(new ChannelsConfigDetail("Socket Mode", GetBool(config, "Slack.SocketMode", defaultValue: true) ? "enabled" : "disabled"));

        if (provider.Provider == ChannelsConfigProvider.Mattermost)
        {
            details.Add(new ChannelsConfigDetail("Server URL", FormatOptional(GetString(config, "Mattermost.ServerUrl"))));
            details.Add(new ChannelsConfigDetail("Callback URL", FormatOptional(GetString(config, "Mattermost.CallbackUrl"))));
        }

        details.Add(new ChannelsConfigDetail("Default channel", FormatDefaultChannel(config, provider.SectionName)));
        details.Add(new ChannelsConfigDetail("Allowed channels", FormatCount(channels.Count, "configured")));
        details.Add(new ChannelsConfigDetail("Allowed users", FormatCount(users.Count, "configured")));
        details.Add(new ChannelsConfigDetail("DMs", allowDms ? "enabled" : "disabled"));
        details.Add(new ChannelsConfigDetail("Channel mentions", mentionOnly ? "required" : "not required"));
        details.Add(new ChannelsConfigDetail("DM mentions", allowDms && mentionRequiredInDm ? "required" : "not required"));
        details.Add(new ChannelsConfigDetail("Audience overrides", FormatCount(audienceOverrides, "configured")));

        return details;
    }

    private string ReadSummary(Dictionary<string, object> config, ChannelProviderSpec provider)
    {
        var configured = SectionPresent(config, provider.SectionName) || HasAnySecret(provider.SecretPaths);
        if (!configured)
            return "not configured";

        var enabled = GetBool(config, $"{provider.SectionName}.Enabled", defaultValue: false);
        if (!enabled)
            return "disabled";

        var channelCount = ReadConfiguredChannels(config, provider.SectionName).Count;
        var userCount = GetStringArray(config, $"{provider.SectionName}.AllowedUserIds").Count;
        var allowDms = GetBool(config, $"{provider.SectionName}.AllowDirectMessages", defaultValue: false);

        var parts = new List<string>
        {
            channelCount > 0
                ? Pluralize(channelCount, "channel", "channels")
                : allowDms ? "DMs only" : "no channels"
        };

        if (userCount > 0)
            parts.Add(Pluralize(userCount, "user", "users"));

        return string.Join(", ", parts);
    }

    private void AddCredentialDetails(List<ChannelsConfigDetail> details, ChannelProviderSpec provider)
    {
        foreach (var path in provider.SecretPaths)
        {
            var label = path switch
            {
                "Slack.BotToken" => "Bot token",
                "Slack.AppToken" => "App token",
                "Discord.BotToken" => "Bot token",
                "Mattermost.BotToken" => "Bot token",
                _ => path
            };

            details.Add(new ChannelsConfigDetail(label, ConfigFileHelper.SecretPresent(_paths, path) ? "configured" : "missing"));
        }
    }

    private bool HasAnySecret(IReadOnlyList<string> paths)
        => paths.Any(path => ConfigFileHelper.SecretPresent(_paths, path));

    private static bool SectionPresent(Dictionary<string, object> config, string sectionName)
    {
        if (!ConfigFileHelper.TryGetPathValue(config, sectionName, out var value) || value is null)
            return false;

        if (value is Dictionary<string, object>)
            return true;

        throw new InvalidOperationException($"Configuration section '{sectionName}' must be an object.");
    }

    private static bool GetBool(Dictionary<string, object> config, string path, bool defaultValue)
    {
        if (!ConfigFileHelper.TryGetPathValue(config, path, out var value) || value is null)
            return defaultValue;

        return value is bool boolValue
            ? boolValue
            : throw new InvalidOperationException($"Configuration value '{path}' must be a boolean.");
    }

    private static string? GetString(Dictionary<string, object> config, string path)
    {
        if (!ConfigFileHelper.TryGetPathValue(config, path, out var value) || value is null)
            return null;

        return value is string stringValue
            ? stringValue
            : throw new InvalidOperationException($"Configuration value '{path}' must be a string.");
    }

    private static IReadOnlyList<string> ReadConfiguredChannels(Dictionary<string, object> config, string sectionName)
    {
        var channels = new List<string>();
        channels.AddRange(GetStringArray(config, $"{sectionName}.AllowedChannelIds"));

        var defaultChannelId = GetString(config, $"{sectionName}.DefaultChannelId");
        if (!string.IsNullOrWhiteSpace(defaultChannelId))
            channels.Add(defaultChannelId);

        if (string.Equals(sectionName, "Slack", StringComparison.Ordinal))
        {
            var defaultChannelName = GetString(config, "Slack.DefaultChannelName");
            if (!string.IsNullOrWhiteSpace(defaultChannelName))
                channels.Add(defaultChannelName.StartsWith('#') ? defaultChannelName : $"#{defaultChannelName}");
        }

        return channels
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> GetStringArray(Dictionary<string, object> config, string path)
    {
        if (!ConfigFileHelper.TryGetPathValue(config, path, out var value) || value is null)
            return [];

        if (value is object[] objectValues)
        {
            return objectValues
                .Select(static item => item switch
                {
                    string stringValue => stringValue,
                    JsonElement { ValueKind: JsonValueKind.String } element => element.GetString()!,
                    _ => throw new InvalidOperationException("Channel list values must be strings.")
                })
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .ToArray();
        }

        if (value is string[] stringValues)
            return stringValues.Where(static item => !string.IsNullOrWhiteSpace(item)).ToArray();

        throw new InvalidOperationException($"Configuration value '{path}' must be an array of strings.");
    }

    private static int GetDictionaryCount(Dictionary<string, object> config, string path)
    {
        if (!ConfigFileHelper.TryGetPathValue(config, path, out var value) || value is null)
            return 0;

        return value is Dictionary<string, object> dict
            ? dict.Count
            : throw new InvalidOperationException($"Configuration value '{path}' must be an object.");
    }

    private static string FormatDefaultChannel(Dictionary<string, object> config, string sectionName)
    {
        var id = GetString(config, $"{sectionName}.DefaultChannelId");
        if (!string.IsNullOrWhiteSpace(id))
            return id;

        if (string.Equals(sectionName, "Slack", StringComparison.Ordinal))
        {
            var name = GetString(config, "Slack.DefaultChannelName");
            if (!string.IsNullOrWhiteSpace(name))
                return name.StartsWith('#') ? name : $"#{name}";
        }

        return "not set";
    }

    private static string FormatOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? "not set" : value;

    private static string FormatCount(int count, string suffix)
        => count == 0 ? "none" : $"{count} {suffix}";

    private static string Pluralize(int count, string singular, string plural)
        => count == 1 ? $"1 {singular}" : $"{count} {plural}";

    private sealed record ChannelProviderSpec(
        ChannelsConfigProvider Provider,
        string Label,
        string Description,
        string SectionName,
        IReadOnlyList<string> SecretPaths);
}

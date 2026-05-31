// -----------------------------------------------------------------------
// <copyright file="ChannelsEditorModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Options;
using Netclaw.Actors.Channels;
using Netclaw.Cli.Tui.Wizard.Steps;

namespace Netclaw.Cli.Tui.Config;

internal sealed class ChannelsEditorModel
{
    public SlackChannelEditorModel Slack { get; } = new();

    public DiscordChannelEditorModel Discord { get; } = new();

    public MattermostChannelEditorModel Mattermost { get; } = new();

    public static ChannelsEditorModel FromStep(ChannelPickerStepViewModel step)
    {
        var slack = step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack);
        var discord = step.GetAdapterViewModel<DiscordStepViewModel>(ChannelType.Discord);
        var mattermost = step.GetAdapterViewModel<MattermostStepViewModel>(ChannelType.Mattermost);

        var model = new ChannelsEditorModel
        {
            Slack =
            {
                Enabled = step.IsAdapterEnabled(ChannelType.Slack),
                BotTokenDraft = Normalize(slack.BotToken),
                HasPersistedBotToken = slack.HasPersistedBotToken,
                AppTokenDraft = Normalize(slack.AppToken),
                HasPersistedAppToken = slack.HasPersistedAppToken,
            },
            Discord =
            {
                Enabled = step.IsAdapterEnabled(ChannelType.Discord),
                BotTokenDraft = Normalize(discord.BotToken),
                HasPersistedBotToken = discord.HasPersistedBotToken,
            },
            Mattermost =
            {
                Enabled = step.IsAdapterEnabled(ChannelType.Mattermost),
                ServerUrl = Normalize(mattermost.ServerUrl),
                BotTokenDraft = Normalize(mattermost.BotToken),
                HasPersistedBotToken = mattermost.HasPersistedBotToken,
                CallbackUrl = Normalize(mattermost.CallbackUrl),
            }
        };

        return model;
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal abstract class ChannelEditorProviderModel
{
    public bool Enabled { get; set; }
}

internal sealed class SlackChannelEditorModel : ChannelEditorProviderModel
{
    public string? BotTokenDraft { get; set; }

    public bool HasPersistedBotToken { get; set; }

    public string? AppTokenDraft { get; set; }

    public bool HasPersistedAppToken { get; set; }
}

internal sealed class DiscordChannelEditorModel : ChannelEditorProviderModel
{
    public string? BotTokenDraft { get; set; }

    public bool HasPersistedBotToken { get; set; }
}

internal sealed class MattermostChannelEditorModel : ChannelEditorProviderModel
{
    public string? ServerUrl { get; set; }

    public string? BotTokenDraft { get; set; }

    public bool HasPersistedBotToken { get; set; }

    public string? CallbackUrl { get; set; }
}

internal sealed class ChannelsEditorValidator : IValidateOptions<ChannelsEditorModel>
{
    public ValidateOptionsResult Validate(string? name, ChannelsEditorModel options)
    {
        var errors = new List<string>();

        if (options.Slack.Enabled)
        {
            if (!HasEffectiveSecret(options.Slack.BotTokenDraft, options.Slack.HasPersistedBotToken))
                errors.Add(ChannelsEditorValidationMessages.SlackBotTokenRequired);
            else if (!string.IsNullOrWhiteSpace(options.Slack.BotTokenDraft)
                     && !options.Slack.BotTokenDraft.StartsWith("xoxb-", StringComparison.OrdinalIgnoreCase))
                errors.Add(ChannelsEditorValidationMessages.SlackBotTokenPrefix);

            if (!HasEffectiveSecret(options.Slack.AppTokenDraft, options.Slack.HasPersistedAppToken))
                errors.Add(ChannelsEditorValidationMessages.SlackAppTokenRequired);
            else if (!string.IsNullOrWhiteSpace(options.Slack.AppTokenDraft)
                     && !options.Slack.AppTokenDraft.StartsWith("xapp-", StringComparison.OrdinalIgnoreCase))
                errors.Add(ChannelsEditorValidationMessages.SlackAppTokenPrefix);
        }

        if (options.Discord.Enabled
            && !HasEffectiveSecret(options.Discord.BotTokenDraft, options.Discord.HasPersistedBotToken))
            errors.Add(ChannelsEditorValidationMessages.DiscordBotTokenRequired);

        if (options.Mattermost.Enabled)
        {
            if (string.IsNullOrWhiteSpace(options.Mattermost.ServerUrl))
                errors.Add(ChannelsEditorValidationMessages.MattermostServerUrlRequired);
            else if (!IsHttpUrl(options.Mattermost.ServerUrl))
                errors.Add(ChannelsEditorValidationMessages.MattermostServerUrlAbsoluteHttp);

            if (!HasEffectiveSecret(options.Mattermost.BotTokenDraft, options.Mattermost.HasPersistedBotToken))
                errors.Add(ChannelsEditorValidationMessages.MattermostBotTokenRequired);

            if (!string.IsNullOrWhiteSpace(options.Mattermost.CallbackUrl)
                && !IsHttpUrl(options.Mattermost.CallbackUrl))
                errors.Add(ChannelsEditorValidationMessages.MattermostCallbackUrlAbsoluteHttp);
        }

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }

    private static bool HasEffectiveSecret(string? draftValue, bool hasPersistedSecret)
        => !string.IsNullOrWhiteSpace(draftValue) || hasPersistedSecret;

    internal static bool IsHttpUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && uri.Scheme is "http" or "https";
}

internal static class ChannelsEditorFieldPaths
{
    internal const string SlackBotToken = "Slack.BotToken";
    internal const string SlackAppToken = "Slack.AppToken";
    internal const string SlackAllowedChannelIds = "Slack.AllowedChannelIds";
    internal const string DiscordBotToken = "Discord.BotToken";
    internal const string DiscordAllowedChannelIds = "Discord.AllowedChannelIds";
    internal const string MattermostServerUrl = "Mattermost.ServerUrl";
    internal const string MattermostBotToken = "Mattermost.BotToken";
    internal const string MattermostCallbackUrl = "Mattermost.CallbackUrl";
    internal const string MattermostAllowedChannelIds = "Mattermost.AllowedChannelIds";
}

internal static class ChannelsEditorValidationMessages
{
    internal const string SlackBotTokenRequired = "Slack bot token is required.";
    internal const string SlackBotTokenPrefix = "Slack bot token must start with xoxb-.";
    internal const string SlackAppTokenRequired = "Slack Socket Mode app token is required.";
    internal const string SlackAppTokenPrefix = "Slack app token must start with xapp-.";
    internal const string DiscordBotTokenRequired = "Discord bot token is required.";
    internal const string MattermostServerUrlRequired = "Mattermost server URL is required.";
    internal const string MattermostServerUrlAbsoluteHttp = "Mattermost server URL must be an absolute http:// or https:// URL.";
    internal const string MattermostBotTokenRequired = "Mattermost bot token is required.";
    internal const string MattermostCallbackUrlAbsoluteHttp = "Mattermost callback URL must be an absolute http:// or https:// URL.";
}

internal sealed record ChannelsEditorValidationIssue(string? FieldId, string Message, ConfigValidationSeverity Severity);

internal sealed record ChannelsEditorValidationResult(IReadOnlyList<ChannelsEditorValidationIssue> Issues)
{
    public static readonly ChannelsEditorValidationResult Empty = new([]);

    public bool HasErrors => Issues.Any(static issue => issue.Severity == ConfigValidationSeverity.Error);

    public IReadOnlyList<ChannelsEditorValidationIssue> IssuesFor(string fieldId)
        => [.. Issues.Where(issue => string.Equals(issue.FieldId, fieldId, StringComparison.Ordinal))];
}

internal sealed class ChannelsEditorValidationAdapter
{
    private readonly ChannelsEditorValidator _validator = new();

    internal ChannelsEditorValidationResult Validate(ChannelsEditorModel model)
    {
        var result = _validator.Validate(name: null, model);
        if (result.Succeeded)
            return ChannelsEditorValidationResult.Empty;

        var failures = result.Failures ?? [];
        var issues = new List<ChannelsEditorValidationIssue>();
        foreach (var failure in failures)
            issues.Add(new ChannelsEditorValidationIssue(FieldForMessage(failure), failure, ConfigValidationSeverity.Error));

        return new ChannelsEditorValidationResult(issues);
    }

    private static string? FieldForMessage(string message)
        => message switch
        {
            ChannelsEditorValidationMessages.SlackBotTokenRequired => ChannelsEditorFieldPaths.SlackBotToken,
            ChannelsEditorValidationMessages.SlackBotTokenPrefix => ChannelsEditorFieldPaths.SlackBotToken,
            ChannelsEditorValidationMessages.SlackAppTokenRequired => ChannelsEditorFieldPaths.SlackAppToken,
            ChannelsEditorValidationMessages.SlackAppTokenPrefix => ChannelsEditorFieldPaths.SlackAppToken,
            ChannelsEditorValidationMessages.DiscordBotTokenRequired => ChannelsEditorFieldPaths.DiscordBotToken,
            ChannelsEditorValidationMessages.MattermostServerUrlRequired => ChannelsEditorFieldPaths.MattermostServerUrl,
            ChannelsEditorValidationMessages.MattermostServerUrlAbsoluteHttp => ChannelsEditorFieldPaths.MattermostServerUrl,
            ChannelsEditorValidationMessages.MattermostBotTokenRequired => ChannelsEditorFieldPaths.MattermostBotToken,
            ChannelsEditorValidationMessages.MattermostCallbackUrlAbsoluteHttp => ChannelsEditorFieldPaths.MattermostCallbackUrl,
            _ => null,
        };
}

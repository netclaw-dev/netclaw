// -----------------------------------------------------------------------
// <copyright file="NotificationsConfigValidator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Netclaw.Configuration;

/// <summary>
/// Rejects a notification webhook that has no URL from any config source. The URL normally lives in
/// secrets.json, so a missing value means a lost or misspelled secrets entry. The daemon must stop
/// at startup instead of skipping the webhook and dropping every alert.
/// </summary>
public sealed class NotificationsConfigValidator : IValidateOptions<NotificationsConfig>
{
    public ValidateOptionsResult Validate(string? name, NotificationsConfig options)
    {
        var errors = options.Webhooks
            .Where(static webhook => webhook.Value.Url.IsNullOrEmpty())
            .Select(static webhook =>
                $"Notifications:Webhooks:{webhook.Key}:Url is required. Set it in secrets.json " +
                $"(`netclaw secrets set Notifications:Webhooks:{webhook.Key}:Url <url>`) or in netclaw.json. " +
                $"A Slack URL also needs Notifications:Webhooks:{webhook.Key}:Format set to Slack. " +
                "The daemon does not watch secrets.json, so edit the files, then restart the daemon.")
            .ToList();

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }

    /// <summary>
    /// The check that the config watcher runs on a candidate configuration before it restarts the daemon.
    /// Returns the error text, or null when every webhook has a URL.
    /// </summary>
    public static string? Check(IConfiguration configuration)
    {
        var options = configuration.GetSection("Notifications").Get<NotificationsConfig>() ?? new NotificationsConfig();
        var result = new NotificationsConfigValidator().Validate(name: null, options);
        return result.Failed ? string.Join(' ', result.Failures!) : null;
    }
}

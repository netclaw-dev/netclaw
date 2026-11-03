// -----------------------------------------------------------------------
// <copyright file="NotificationsConfigValidator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
                $"(`netclaw secrets set Notifications:Webhooks:{webhook.Key}:Url <url>`) or in netclaw.json.")
            .ToList();

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}

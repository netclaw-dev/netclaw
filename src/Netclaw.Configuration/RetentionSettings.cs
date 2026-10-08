// -----------------------------------------------------------------------
// <copyright file="RetentionSettings.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// One <c>Retention:*:Days</c> setting. The daemon's retention job, the <c>netclaw config</c>
/// editor, and <c>netclaw config retention</c> all read this one description.
/// </summary>
/// <param name="Id">Short name that the dashboard summary shows.</param>
/// <param name="ConfigKey">The key in configuration form, with colons.</param>
/// <param name="Label">What the setting prunes, as the editor shows it.</param>
/// <param name="DefaultDays">The number of days that applies when the key is not set.</param>
/// <param name="CliOption">The <c>netclaw config retention</c> option that sets it.</param>
internal sealed record RetentionSetting(string Id, string ConfigKey, string Label, int DefaultDays, string CliOption)
{
    /// <summary>The key as a dotted path in netclaw.json.</summary>
    public string FilePath => ConfigKey.Replace(':', '.');
}

/// <summary>
/// Every retention setting. Another kind of data that expires adds one entry here, one job in
/// the daemon, and nothing else in the editor or the command.
/// </summary>
internal static class RetentionSettings
{
    public static readonly RetentionSetting Logs = new("logs", "Retention:Logs:Days", "Daemon and crash logs", 14, "--logs-days");

    public static IReadOnlyList<RetentionSetting> All { get; } = [Logs];
}

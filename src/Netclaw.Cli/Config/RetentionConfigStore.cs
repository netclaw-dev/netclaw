// -----------------------------------------------------------------------
// <copyright file="RetentionConfigStore.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Tui.Sections;
using Netclaw.Configuration;

namespace Netclaw.Cli.Config;

/// <summary>The stored value of one <see cref="RetentionSetting"/>.</summary>
/// <param name="Days">The number of days that applies. Zero keeps the data forever.</param>
/// <param name="IsSet">False when netclaw.json does not set the key and the default applies.</param>
/// <param name="Warning">Set when the stored value is not an integer and the default applies instead.</param>
internal sealed record RetentionValue(int Days, bool IsSet, string? Warning = null);

/// <summary>
/// Reads and writes the retention settings in netclaw.json for the <c>netclaw config</c> editor
/// and <c>netclaw config retention</c>. A read resolves the value the way the daemon does, and a
/// write goes through the shared config editor pipeline.
/// </summary>
internal static class RetentionConfigStore
{
    /// <summary>Appended to a status line after a write.</summary>
    public const string Applied = ConfigFileHelper.DaemonAppliesChange;

    public static RetentionValue Read(NetclawPaths paths, RetentionSetting setting)
    {
        var configuration = BuildFileConfiguration(paths);
        var days = RetentionPolicy.ResolveDays(configuration, setting.ConfigKey, setting.DefaultDays, out var warning);
        return new RetentionValue(days, !string.IsNullOrWhiteSpace(configuration[setting.ConfigKey]), warning);
    }

    private static IConfigurationRoot BuildFileConfiguration(NetclawPaths paths)
        => new ConfigurationBuilder()
            .AddJsonFile(paths.NetclawConfigPath, optional: true, reloadOnChange: false)
            .Build();

    /// <summary>
    /// The warning to show when an environment variable overrides the file, or null. The daemon
    /// reads <c>NETCLAW_</c> variables after netclaw.json, so a change to the file has no effect.
    /// A blank variable counts: the daemon uses it instead of the file.
    /// </summary>
    public static string? EnvironmentOverrideWarning(RetentionSetting setting)
    {
        var environment = new ConfigurationBuilder().AddEnvironmentVariables("NETCLAW_").Build();
        if (!HasKey(environment, setting.ConfigKey))
            return null;

        // The provider matches the prefix and the key without case, and reads "__" as ":".
        var name = Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .FirstOrDefault(variable => variable.StartsWith("NETCLAW_", StringComparison.OrdinalIgnoreCase)
                && string.Equals(variable["NETCLAW_".Length..].Replace("__", ":", StringComparison.Ordinal), setting.ConfigKey, StringComparison.OrdinalIgnoreCase))
            ?? "NETCLAW_" + setting.ConfigKey.Replace(":", "__", StringComparison.Ordinal);
        return $"{name} is set and overrides netclaw.json for the daemon.";
    }

    // Presence, not value: a blank or null value is still a key the configuration loader sees.
    private static bool HasKey(IConfiguration configuration, string key)
        => configuration.AsEnumerable().Any(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Writes the changed settings in one save and returns the number of keys written. A key that
    /// is not set stays unset when the new value is the default, and a value that did not change
    /// is not written.
    /// </summary>
    public static int Save(NetclawPaths paths, IEnumerable<(RetentionSetting Setting, int Days)> changes)
    {
        var pending = new List<(RetentionSetting Setting, int Days, RetentionValue Current)>();
        foreach (var (setting, days) in changes)
        {
            var current = Read(paths, setting);
            var unchanged = current.Warning is null && (current.IsSet ? current.Days == days : days == setting.DefaultDays);
            if (!unchanged)
                pending.Add((setting, days, current));
        }

        if (pending.Count == 0)
            return 0;

        var session = new ConfigEditorSession(paths);
        var configuration = BuildFileConfiguration(paths);
        session.Apply(new SectionContribution(pending
            .Select(p => new SectionFieldAction(PathToWrite(session.Config, configuration, p.Setting), SectionFieldActionKind.Set, p.Days))
            .ToList()));
        session.Save();
        return pending.Count;
    }

    // The daemon reads "Retention:Logs:Days" from a nested object or from a flat key with the
    // colons. A write must go to the spelling the file has: a second spelling gives a duplicate
    // key, and the daemon then stops at startup. Any other spelling is refused whatever its value,
    // because a blank or null value is still a key the daemon loads.
    private static string PathToWrite(Dictionary<string, object> existing, IConfiguration configuration, RetentionSetting setting)
    {
        var nested = ConfigFileHelper.ResolveExistingKeyPath(existing, setting.FilePath);
        if (ConfigFileHelper.TryGetPathValue(existing, nested, out _))
            return nested;

        var flat = existing.Keys.FirstOrDefault(key => string.Equals(key, setting.ConfigKey, StringComparison.OrdinalIgnoreCase));
        if (flat is not null)
            return flat;

        if (HasKey(configuration, setting.ConfigKey))
            throw new InvalidOperationException($"{setting.ConfigKey} is set in a spelling this command cannot edit. Edit netclaw.json by hand.");

        return nested;
    }

    /// <summary>"keep 14 days", "keep 1 day", or "keep forever".</summary>
    public static string Describe(int days)
        => days <= 0 ? "keep forever" : $"keep {days} {(days == 1 ? "day" : "days")}";

    /// <summary>The value for the dashboard summary: "14d" or "forever".</summary>
    public static string Short(int days) => days <= 0 ? "forever" : $"{days}d";
}

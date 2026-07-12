// -----------------------------------------------------------------------
// <copyright file="DoctorFixService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json.Nodes;
using System.Text.Json;
using Json.Schema;
using Netclaw.Cli.Config;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Json;
using Netclaw.Configuration;

namespace Netclaw.Cli.Doctor;

public sealed class DoctorFixService
{
    private readonly NetclawPaths _paths;
    private readonly string _systemdUnitPath;
    private readonly bool _systemdEnabled;

    public DoctorFixService(NetclawPaths paths)
        : this(paths, DaemonManager.SystemdUserUnitFilePath, OperatingSystem.IsLinux())
    {
    }

    /// <summary>
    /// Test seam: explicit systemd unit path and platform gate so the daemon PATH
    /// rehydration fix can be exercised hermetically, without depending on the host's
    /// real <c>~/.config/systemd/user/netclaw.service</c>.
    /// </summary>
    internal DoctorFixService(NetclawPaths paths, string systemdUnitPath, bool systemdEnabled)
    {
        _paths = paths;
        _systemdUnitPath = systemdUnitPath;
        _systemdEnabled = systemdEnabled;
    }

    public Task<DoctorFixPlan> BuildPlanAsync(CancellationToken cancellationToken = default)
    {
        var fixes = new List<DoctorFileFix>();

        // Daemon shell-tool PATH rehydration is independent of netclaw.json — it must be
        // evaluated even when the app config file is absent, so it runs before the
        // config-file early-return below.
        TryAddDaemonPathEnvironmentFix(fixes);

        if (!File.Exists(_paths.NetclawConfigPath))
            return Task.FromResult(new DoctorFixPlan(fixes));

        string original;
        JsonObject? obj;
        try
        {
            original = File.ReadAllText(_paths.NetclawConfigPath);
            obj = JsonNode.Parse(original) as JsonObject;
        }
        catch
        {
            return Task.FromResult(new DoctorFixPlan(fixes));
        }

        if (obj is null)
            return Task.FromResult(new DoctorFixPlan(fixes));

        var appliedFixes = new List<string>();

        if (obj["Models"] is JsonObject modelsNode)
        {
            var legacyEnvironmentOverride = ModelEntryWriter.FindLegacyEnvironmentOverride();
            if (legacyEnvironmentOverride is not null)
            {
                throw new InvalidOperationException(
                    $"Cannot migrate Models while legacy environment override '{legacyEnvironmentOverride}' is set. " +
                    "Move model overrides to NETCLAW_Models__Definitions__<name>__* and " +
                    "NETCLAW_Models__Roles__* first.");
            }

            var models = JsonSerializer.Deserialize<Dictionary<string, object>>(modelsNode.ToJsonString())!;
            if (ModelEntryWriter.MigrateLegacy(models))
            {
                obj["Models"] = JsonNode.Parse(JsonSerializer.Serialize(models, JsonDefaults.ConfigFile));
                appliedFixes.Add("named model definitions");
            }
        }

        // --- Manual fixes (not derivable from schema alone) ---

        if (obj["configVersion"] is null)
        {
            obj["configVersion"] = EmbeddedSchemaLoader.CurrentSchemaVersion;
            appliedFixes.Add("configVersion");
        }

        if (obj["Slack"] is JsonObject slack && DoctorJsonConfigReader.ReadBool(slack, "Enabled"))
        {
            var hasAllowedChannels = slack["AllowedChannelIds"] is JsonArray { Count: > 0 };
            var hasDefaultChannel = !string.IsNullOrWhiteSpace(slack["DefaultChannelId"]?.GetValue<string>())
                                    || !string.IsNullOrWhiteSpace(slack["DefaultChannelName"]?.GetValue<string>());

            if (!hasAllowedChannels && !hasDefaultChannel)
            {
                slack["AllowedChannelIds"] = new JsonArray();
                appliedFixes.Add("Slack ACL defaults");
            }
        }

        if (obj["Telemetry"] is JsonObject telemetry && DoctorJsonConfigReader.ReadBool(telemetry, "Enabled"))
        {
            telemetry["Otlp"] ??= new JsonObject();
            if (telemetry["Otlp"] is JsonObject otlp
                && string.IsNullOrWhiteSpace(otlp["Endpoint"]?.GetValue<string>()))
            {
                otlp["Endpoint"] = "http://127.0.0.1:4317";
                appliedFixes.Add("telemetry endpoint");
            }
        }

        // Webhook format auto-detection
        if (obj["Notifications"] is JsonObject notif
            && notif["Webhooks"] is JsonArray webhooksArr)
        {
            foreach (var item in webhooksArr)
            {
                if (item is not JsonObject wh)
                    continue;

                var url = wh["Url"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                if (WebhookFormatDetection.InferFromUrl(url) != WebhookFormat.Slack)
                    continue;

                var existing = wh["Format"]?.GetValue<string>();
                if (existing is null || existing.Equals(nameof(WebhookFormat.Generic), StringComparison.OrdinalIgnoreCase))
                {
                    wh["Format"] = nameof(WebhookFormat.Slack);
                    appliedFixes.Add("webhook format");
                }
            }
        }

        // --- Schema-driven fixes ---
        TryApplySchemaFixes(obj, appliedFixes);

        if (appliedFixes.Count > 0)
        {
            var normalized = obj.ToJsonString(JsonDefaults.Indented);

            var replacement = normalized.EndsWith(Environment.NewLine, StringComparison.Ordinal)
                ? normalized
                : normalized + Environment.NewLine;

            fixes.Add(new DoctorFileFix(
                FilePath: _paths.NetclawConfigPath,
                Description: $"Apply safe configuration autofixes ({string.Join(", ", appliedFixes)}).",
                OriginalText: original,
                UpdatedText: replacement));
        }

        return Task.FromResult(new DoctorFixPlan(fixes));
    }

    private static void TryApplySchemaFixes(JsonObject config, List<string> appliedFixes)
    {
        var version = EmbeddedSchemaLoader.CurrentSchemaVersion;
        if (config["configVersion"] is JsonValue versionValue
            && versionValue.TryGetValue<int>(out var parsedVersion))
        {
            version = parsedVersion;
        }

        var schemaText = EmbeddedSchemaLoader.LoadConfigSchema(version);
        if (schemaText is null)
            return;

        JsonSchema schema;
        JsonObject? schemaJson;
        try
        {
            schema = JsonSchema.FromText(schemaText);
            schemaJson = JsonNode.Parse(schemaText) as JsonObject;
        }
        catch
        {
            return;
        }

        if (schemaJson is null)
            return;

        if (SchemaFixResolver.TryApplySchemaFixes(schema, schemaJson, config, out var schemaFixes))
            appliedFixes.AddRange(schemaFixes);
    }

    /// <summary>
    /// Rehydrates the daemon's shell-tool PATH environment file
    /// (<see cref="NetclawPaths.DaemonEnvironmentFilePath"/>) from the operator's
    /// current, real PATH when it is missing or no longer includes the daemon's install
    /// directory. The CLI process running <c>doctor --fix</c> is a child of the operator's
    /// shell, so its PATH is the value we want — captured with zero shell execution.
    /// </summary>
    /// <remarks>
    /// Only acts when the installed unit already references this env file. Legacy units
    /// (inline <c>Environment=PATH=</c>, no <c>EnvironmentFile=</c>) are routed to
    /// <c>netclaw daemon install</c> by <c>SystemdUnitPathDoctorCheck</c>; doctor --fix
    /// does not rewrite systemd units. The fix writes the file only — the operator must run
    /// <c>systemctl --user restart netclaw</c> (surfaced in the description) for the daemon
    /// to pick it up; we never restart the daemon implicitly.
    /// </remarks>
    private void TryAddDaemonPathEnvironmentFix(List<DoctorFileFix> fixes)
    {
        if (!_systemdEnabled || !File.Exists(_systemdUnitPath))
            return;

        string[] unitLines;
        try
        {
            unitLines = File.ReadAllLines(_systemdUnitPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        // Require the unit to reference OUR env file and to expose an install dir; anything
        // else is a reinstall case, not a file-content rehydration.
        if (!DaemonPathEnvironmentFile.TryGetEnvironmentFilePath(unitLines, out var referencedEnvPath)
            || !DaemonPathEnvironmentFile.TryGetInstallDir(unitLines, out var installDir))
        {
            return;
        }

        var envPath = _paths.DaemonEnvironmentFilePath;
        try
        {
            if (!string.Equals(Path.GetFullPath(referencedEnvPath), Path.GetFullPath(envPath), StringComparison.Ordinal))
                return;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            // A hand-edited/malformed EnvironmentFile= value is not our managed file; skip the
            // daemon-PATH fix rather than aborting the whole doctor --fix run on GetFullPath.
            return;
        }

        string? existing = null;
        if (File.Exists(envPath))
        {
            try
            {
                existing = File.ReadAllText(envPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
        }

        var healthy = existing is not null
            && DaemonPathEnvironmentFile.ReadPathValue(existing) is { } current
            && DaemonPathEnvironmentFile.PathContainsDirectory(current, installDir);

        if (healthy)
            return;

        var captured = DaemonPathEnvironmentFile.CaptureCurrentPath();
        var updated = DaemonPathEnvironmentFile.Render(installDir, captured);

        fixes.Add(new DoctorFileFix(
            FilePath: envPath,
            Description: "Rehydrate the daemon's shell-tool PATH from your current environment. "
                + "Run `systemctl --user restart netclaw` afterward for the daemon to pick it up.",
            OriginalText: existing ?? string.Empty,
            UpdatedText: updated));
    }

    public Task ApplyAsync(DoctorFixPlan plan, CancellationToken cancellationToken = default)
    {
        foreach (var fix in plan.Fixes)
        {
            // Ensure the parent directory exists before writing. The daemon-PATH fix can
            // target ~/.netclaw/config even after that directory has been removed, so a bare
            // File.WriteAllTextAsync would throw DirectoryNotFoundException and abort the run.
            var dir = Path.GetDirectoryName(fix.FilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            cancellationToken.ThrowIfCancellationRequested();
            if (fix.Description.Contains("named model definitions", StringComparison.Ordinal)
                && File.Exists(fix.FilePath))
            {
                var backupPath = fix.FilePath + ".legacy-models.bak";
                if (!File.Exists(backupPath))
                    File.Copy(fix.FilePath, backupPath);
            }

            AtomicFile.WriteAllText(fix.FilePath, fix.UpdatedText);
        }

        return Task.CompletedTask;
    }

}

public sealed record DoctorFixPlan(IReadOnlyList<DoctorFileFix> Fixes)
{
    public bool HasChanges => Fixes.Count > 0;
}

public sealed record DoctorFileFix(
    string FilePath,
    string Description,
    string OriginalText,
    string UpdatedText);

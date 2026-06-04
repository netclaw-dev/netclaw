// -----------------------------------------------------------------------
// <copyright file="SkillSourcesConfigViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Netclaw.Cli.Config;
using Netclaw.Cli.Json;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using R3;
using Termina.Reactive;

namespace Netclaw.Cli.Tui.Config;

internal sealed record SkillFeedReachabilityResult(bool Success, string Message);

internal interface ISkillFeedReachabilityProbe
{
    SkillFeedReachabilityResult Probe(string baseUrl, string? apiKey, int timeoutSeconds);
}

internal sealed class SkillFeedReachabilityProbe : ISkillFeedReachabilityProbe
{
    public SkillFeedReachabilityResult Probe(string baseUrl, string? apiKey, int timeoutSeconds)
    {
        try
        {
            var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 10));
            using var cts = new CancellationTokenSource(timeout);
            using var client = new HttpClient { Timeout = timeout };
            var root = baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : baseUrl + "/";
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(new Uri(root), ".well-known/agent-skills/index.json"));

            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = client.Send(request, cts.Token);
            if (response.IsSuccessStatusCode)
                return new SkillFeedReachabilityResult(true, "Skill feed discovery endpoint is reachable.");

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new SkillFeedReachabilityResult(false, $"Skill feed authentication failed with HTTP {(int)response.StatusCode}.");

            return new SkillFeedReachabilityResult(false, $"Skill feed probe returned HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            return new SkillFeedReachabilityResult(false, $"Skill feed probe failed: {ex.Message}");
        }
    }
}

internal sealed class SkillSourcesConfigViewModel : ReactiveViewModel
{
    private const string CustomExternalSourceName = "custom-skills";
    private const string CustomFeedName = "custom-feed";
    private const int DefaultFeedTimeoutSeconds = 30;

    private readonly NetclawPaths _paths;
    private readonly ISkillFeedReachabilityProbe _probe;
    private string? _saveAnywayFingerprint;

    public SkillSourcesConfigViewModel(NetclawPaths paths, ISkillFeedReachabilityProbe? probe = null)
    {
        _paths = paths;
        _probe = probe ?? new SkillFeedReachabilityProbe();
        var state = LoadState(paths);
        ExternalSourceCount = new ReactiveProperty<int>(state.ExternalSourceCount);
        SkillFeedCount = new ReactiveProperty<int>(state.SkillFeedCount);
        HasPersistedFeedApiKey = new ReactiveProperty<bool>(state.HasPersistedFeedApiKey);
        ExternalDirectoryDraft = new ReactiveProperty<string>(string.Empty);
        SkillFeedUrlDraft = new ReactiveProperty<string>(string.Empty);
        SkillFeedApiKeyDraft = new ReactiveProperty<string>(string.Empty);
        SelectedRow = new ReactiveProperty<int>(0);
        Status = new ReactiveProperty<ConfigStatusMessage>(new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral));
        IsSaved = new ReactiveProperty<bool>(false);
    }

    internal Action<string>? RouteRequested { get; set; }
    internal bool ShutdownRequestedForTest { get; private set; }

    public ReactiveProperty<int> ExternalSourceCount { get; }
    public ReactiveProperty<int> SkillFeedCount { get; }
    public ReactiveProperty<bool> HasPersistedFeedApiKey { get; }
    public ReactiveProperty<string> ExternalDirectoryDraft { get; }
    public ReactiveProperty<string> SkillFeedUrlDraft { get; }
    public ReactiveProperty<string> SkillFeedApiKeyDraft { get; }
    public ReactiveProperty<int> SelectedRow { get; }
    public ReactiveProperty<ConfigStatusMessage> Status { get; }
    public ReactiveProperty<bool> IsSaved { get; }

    public IReadOnlyList<string> Rows { get; } =
    [
        "External skill directory",
        "Skill feed URL",
        "Skill feed API key"
    ];

    public void MoveSelection(int delta)
    {
        var next = Math.Clamp(SelectedRow.Value + delta, 0, Rows.Count - 1);
        if (next != SelectedRow.Value)
            SelectedRow.Value = next;
    }

    public void AppendText(string text)
    {
        switch (SelectedRow.Value)
        {
            case 0:
                ExternalDirectoryDraft.Value += text;
                break;
            case 1:
                SkillFeedUrlDraft.Value += text;
                break;
            case 2:
                SkillFeedApiKeyDraft.Value += text;
                break;
            default:
                return;
        }

        MarkDirty();
    }

    public void Backspace()
    {
        var target = SelectedRow.Value switch
        {
            0 => ExternalDirectoryDraft,
            1 => SkillFeedUrlDraft,
            2 => SkillFeedApiKeyDraft,
            _ => null
        };

        if (target is null || target.Value.Length == 0)
            return;

        target.Value = target.Value[..^1];
        MarkDirty();
    }

    public bool Save()
    {
        var externalDraft = ExternalDirectoryDraft.Value.Trim();
        var feedUrlDraft = SkillFeedUrlDraft.Value.Trim();
        var apiKeyDraft = SkillFeedApiKeyDraft.Value.Trim();

        string? externalDirectory = null;
        if (!string.IsNullOrWhiteSpace(externalDraft)
            && !TryNormalizeExternalDirectory(externalDraft, out externalDirectory, out var externalError))
        {
            Status.Value = new ConfigStatusMessage(externalError, ConfigStatusTone.Error);
            RequestRedraw();
            return false;
        }

        if (!string.IsNullOrWhiteSpace(apiKeyDraft) && string.IsNullOrWhiteSpace(feedUrlDraft))
        {
            Status.Value = new ConfigStatusMessage("Skill feed URL is required before saving a feed API key.", ConfigStatusTone.Error);
            RequestRedraw();
            return false;
        }

        if (!TryValidateApiKeyDraft(apiKeyDraft, out var apiKeyError))
        {
            Status.Value = new ConfigStatusMessage(apiKeyError, ConfigStatusTone.Error);
            RequestRedraw();
            return false;
        }

        string? feedUrl = null;
        if (!string.IsNullOrWhiteSpace(feedUrlDraft)
            && !TryNormalizeFeedUrl(feedUrlDraft, out feedUrl, out var feedUrlError))
        {
            Status.Value = new ConfigStatusMessage(feedUrlError, ConfigStatusTone.Error);
            RequestRedraw();
            return false;
        }

        var root = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        var feedsConfig = LoadSkillFeedsSection(root);
        var existingFeed = feedsConfig.Feeds.FirstOrDefault(static f => string.Equals(f.Name, CustomFeedName, StringComparison.OrdinalIgnoreCase));
        string? effectiveApiKey = null;
        if (feedUrl is not null)
        {
            if (!string.IsNullOrWhiteSpace(apiKeyDraft))
            {
                effectiveApiKey = apiKeyDraft;
            }
            else if (existingFeed?.ApiKey is { Length: > 0 } existingApiKey
                && !TryDecryptExistingApiKey(_paths, existingApiKey, out effectiveApiKey, out var decryptError))
            {
                Status.Value = new ConfigStatusMessage(decryptError, ConfigStatusTone.Error);
                RequestRedraw();
                return false;
            }
        }

        var fingerprint = $"{externalDirectory}|{feedUrl}|{effectiveApiKey?.Length ?? 0}";
        if (feedUrl is not null && _saveAnywayFingerprint != fingerprint)
        {
            var probeResult = _probe.Probe(feedUrl, effectiveApiKey, DefaultFeedTimeoutSeconds);
            if (!probeResult.Success)
            {
                _saveAnywayFingerprint = fingerprint;
                Status.Value = new ConfigStatusMessage($"{probeResult.Message} Press Enter again to save anyway.", ConfigStatusTone.Warning);
                RequestRedraw();
                return false;
            }
        }

        root["configVersion"] = EmbeddedSchemaLoader.CurrentSchemaVersion;

        var externalConfig = LoadSection<ExternalSkillsConfig>(root, "ExternalSkills");
        if (externalDirectory is not null)
        {
            externalConfig.Sources.RemoveAll(static s => string.Equals(s.Name, CustomExternalSourceName, StringComparison.OrdinalIgnoreCase));
            externalConfig.Sources.Add(new ExternalSkillSource
            {
                Name = CustomExternalSourceName,
                Path = externalDirectory,
                Enabled = true,
                AllowSymlinks = false
            });
            root["ExternalSkills"] = BuildExternalSkillsSection(externalConfig);
        }

        if (feedUrl is not null)
        {
            feedsConfig.Feeds.RemoveAll(static f => string.Equals(f.Name, CustomFeedName, StringComparison.OrdinalIgnoreCase));
            feedsConfig.Feeds.Add(new SkillFeedConfigEntry
            {
                Name = CustomFeedName,
                Url = feedUrl,
                Enabled = true,
                TimeoutSeconds = existingFeed?.TimeoutSeconds ?? DefaultFeedTimeoutSeconds,
                ApiKey = !string.IsNullOrWhiteSpace(apiKeyDraft)
                    ? ProtectApiKeyForConfig(_paths, apiKeyDraft)
                    : existingFeed?.ApiKey
            });
            root["SkillFeeds"] = BuildSkillFeedsSection(feedsConfig);
        }

        ConfigFileHelper.WriteConfigFile(_paths.NetclawConfigPath, root);

        var state = LoadState(_paths);
        ExternalSourceCount.Value = state.ExternalSourceCount;
        SkillFeedCount.Value = state.SkillFeedCount;
        HasPersistedFeedApiKey.Value = state.HasPersistedFeedApiKey;
        ExternalDirectoryDraft.Value = string.Empty;
        SkillFeedUrlDraft.Value = string.Empty;
        SkillFeedApiKeyDraft.Value = string.Empty;
        _saveAnywayFingerprint = null;
        IsSaved.Value = true;
        Status.Value = new ConfigStatusMessage("Skill Sources settings saved.", ConfigStatusTone.Success);
        RequestRedraw();
        return true;
    }

    public void ActivateSelected()
    {
        Save();
    }

    public void GoBack()
    {
        RouteRequested?.Invoke("/config");
        Navigate?.Invoke("/config");
    }

    public void RequestQuit()
    {
        ShutdownRequestedForTest = true;
        Shutdown();
    }

    public override void Dispose()
    {
        ExternalSourceCount.Dispose();
        SkillFeedCount.Dispose();
        HasPersistedFeedApiKey.Dispose();
        ExternalDirectoryDraft.Dispose();
        SkillFeedUrlDraft.Dispose();
        SkillFeedApiKeyDraft.Dispose();
        SelectedRow.Dispose();
        Status.Dispose();
        IsSaved.Dispose();
        base.Dispose();
    }

    private void MarkDirty()
    {
        IsSaved.Value = false;
        _saveAnywayFingerprint = null;
        ClearStatus();
        RequestRedraw();
    }

    private void ClearStatus()
    {
        if (!string.IsNullOrWhiteSpace(Status.Value.Text))
            Status.Value = new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral);
    }

    private static bool TryNormalizeExternalDirectory(string value, out string? fullPath, out string error)
    {
        fullPath = null;
        error = string.Empty;

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            error = "External skill directory must be a local filesystem path, not a URL.";
            return false;
        }

        try
        {
            var expanded = PathExpansion.ExpandHome(value) ?? value;
            fullPath = Path.GetFullPath(expanded);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"External skill directory is not a valid path: {ex.Message}";
            return false;
        }

        if (!Directory.Exists(fullPath))
        {
            error = "External skill directory must already exist so runtime skill scanning can consume it.";
            return false;
        }

        return true;
    }

    private static bool TryNormalizeFeedUrl(string value, out string? url, out string error)
    {
        url = null;
        error = string.Empty;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            error = "Skill feed URL must be an absolute HTTP or HTTPS URI.";
            return false;
        }

        url = uri.ToString().TrimEnd('/');
        return true;
    }

    private static bool TryValidateApiKeyDraft(string value, out string error)
    {
        error = string.Empty;
        if (value.Contains('\r') || value.Contains('\n'))
        {
            error = "Skill feed API key must be a single-line bearer token.";
            return false;
        }

        return true;
    }

    private static (int ExternalSourceCount, int SkillFeedCount, bool HasPersistedFeedApiKey) LoadState(NetclawPaths paths)
    {
        var root = ConfigFileHelper.LoadJsonDict(paths.NetclawConfigPath);
        var external = LoadSection<ExternalSkillsConfig>(root, "ExternalSkills");
        var feeds = LoadSkillFeedsSection(root);
        return (external.Sources.Count, feeds.Feeds.Count, feeds.Feeds.Any(static f => !string.IsNullOrWhiteSpace(f.ApiKey)));
    }

    private static T LoadSection<T>(Dictionary<string, object> root, string sectionName) where T : new()
    {
        if (!root.TryGetValue(sectionName, out var raw) || raw is null)
            return new T();

        var json = raw is JsonElement element
            ? element.GetRawText()
            : JsonSerializer.Serialize(raw, JsonDefaults.ConfigFile);
        return JsonSerializer.Deserialize<T>(json, JsonDefaults.ConfigRead) ?? new T();
    }

    private static Dictionary<string, object> BuildExternalSkillsSection(ExternalSkillsConfig config)
        => new()
        {
            ["Sources"] = config.Sources.Select(static source =>
            {
                var item = new Dictionary<string, object>
                {
                    ["Name"] = source.Name,
                    ["Enabled"] = source.Enabled,
                    ["AllowSymlinks"] = source.AllowSymlinks
                };

                if (!string.IsNullOrWhiteSpace(source.WellKnown))
                    item["WellKnown"] = source.WellKnown;
                if (!string.IsNullOrWhiteSpace(source.Path))
                    item["Path"] = source.Path;

                return (object)item;
            }).ToArray()
        };

    private static SkillFeedsConfigDocument LoadSkillFeedsSection(Dictionary<string, object> root)
    {
        if (!root.TryGetValue("SkillFeeds", out var raw) || raw is null)
            return new SkillFeedsConfigDocument();

        var json = raw is JsonElement element
            ? element.GetRawText()
            : JsonSerializer.Serialize(raw, JsonDefaults.ConfigFile);
        return JsonSerializer.Deserialize<SkillFeedsConfigDocument>(json, JsonDefaults.ConfigRead) ?? new SkillFeedsConfigDocument();
    }

    private static bool TryDecryptExistingApiKey(NetclawPaths paths, string apiKey, out string? plaintext, out string error)
    {
        plaintext = null;
        error = string.Empty;

        if (!ISecretsProtector.IsEncrypted(apiKey))
        {
            plaintext = apiKey;
            return true;
        }

        try
        {
            plaintext = SecretsProtection.CreateProtector(paths).Unprotect(apiKey);
        }
        catch (Exception ex) when (ex is ArgumentException or System.Security.Cryptography.CryptographicException or FormatException)
        {
            error = $"Existing skill feed API key could not be decrypted: {ex.Message}";
            return false;
        }

        return true;
    }

    private static string ProtectApiKeyForConfig(NetclawPaths paths, string apiKey)
        => SecretsProtection.CreateProtector(paths).Protect(apiKey);

    private static Dictionary<string, object> BuildSkillFeedsSection(SkillFeedsConfigDocument config)
        => new()
        {
            ["SyncIntervalMinutes"] = config.SyncIntervalMinutes,
            ["Feeds"] = config.Feeds.Select(static feed =>
            {
                var item = new Dictionary<string, object>
                {
                    ["Name"] = feed.Name,
                    ["Url"] = feed.Url,
                    ["Enabled"] = feed.Enabled,
                    ["TimeoutSeconds"] = feed.TimeoutSeconds
                };

                if (!string.IsNullOrWhiteSpace(feed.ApiKey))
                    item["ApiKey"] = feed.ApiKey;

                return (object)item;
            }).ToArray()
        };

    private sealed class SkillFeedsConfigDocument
    {
        public int SyncIntervalMinutes { get; set; } = 60;

        public List<SkillFeedConfigEntry> Feeds { get; set; } = [];
    }

    private sealed class SkillFeedConfigEntry
    {
        public string Name { get; set; } = string.Empty;

        public string Url { get; set; } = string.Empty;

        public string? ApiKey { get; set; }

        public bool Enabled { get; set; } = true;

        public int TimeoutSeconds { get; set; } = DefaultFeedTimeoutSeconds;
    }
}

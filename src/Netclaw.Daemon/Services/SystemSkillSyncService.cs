using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Skills;
using Netclaw.Actors.Text;
using Netclaw.Configuration;
using Netclaw.Configuration.Feeds;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Syncs system skills from the feed CDN at daemon startup, then enriches
/// registered skills with keyword indexes for deterministic auto-loading.
/// Runs after <see cref="Program.CopyBuiltInSkills"/> seeds offline defaults.
/// Never blocks startup on network — if the feed is unreachable, the daemon
/// starts with whatever skills are already on disk.
/// </summary>
internal sealed class SystemSkillSyncService : IHostedService
{
    private readonly HttpClient _httpClient;
    private readonly NetclawPaths _paths;
    private readonly SkillSyncConfig _skillSyncConfig;
    private readonly SkillRegistry _skillRegistry;
    private readonly SkillIndexContextLayer _skillIndexLayer;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SystemSkillSyncService> _logger;
    private readonly string _daemonVersion;
    private readonly IChatClientProvider? _chatClientProvider;

    private static readonly TimeSpan EnrichmentTimeout = TimeSpan.FromSeconds(30);
    private static readonly HashSet<string> GenericKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "check", "checking", "status", "support", "supported", "new", "good", "best",
        "help", "use", "using", "work", "working", "change", "changes", "run", "running",
        "view", "show", "find", "look", "issue", "issues", "problem", "problems"
    };

    private static readonly HashSet<string> GenericPhraseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "check", "status", "new", "good", "best", "help", "use", "run", "show", "view"
    };

    public SystemSkillSyncService(
        HttpClient httpClient,
        NetclawPaths paths,
        SkillSyncConfig skillSyncConfig,
        SkillRegistry skillRegistry,
        SkillIndexContextLayer skillIndexLayer,
        TimeProvider timeProvider,
        ILogger<SystemSkillSyncService> logger,
        IChatClientProvider? chatClientProvider = null)
        : this(httpClient, paths, skillSyncConfig, skillRegistry, skillIndexLayer,
            timeProvider, logger, BuildInfo.Version, chatClientProvider)
    {
    }

    // Internal constructor for testing — allows injecting a fake daemon version
    internal SystemSkillSyncService(
        HttpClient httpClient,
        NetclawPaths paths,
        SkillSyncConfig skillSyncConfig,
        SkillRegistry skillRegistry,
        SkillIndexContextLayer skillIndexLayer,
        TimeProvider timeProvider,
        ILogger<SystemSkillSyncService> logger,
        string daemonVersion,
        IChatClientProvider? chatClientProvider = null)
    {
        _httpClient = httpClient;
        _paths = paths;
        _skillSyncConfig = skillSyncConfig;
        _skillRegistry = skillRegistry;
        _skillIndexLayer = skillIndexLayer;
        _timeProvider = timeProvider;
        _logger = logger;
        _daemonVersion = daemonVersion;
        _chatClientProvider = chatClientProvider;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_skillSyncConfig.DisableSystemSkillSync)
        {
            _logger.LogInformation(
                "System skill sync disabled via SkillSync.DisableSystemSkillSync; using on-disk built-in skills only");
            RescanAndUpdateIndex();
            return;
        }

        try
        {
            Directory.CreateDirectory(_paths.SystemSkillsDirectory);

            var syncState = ReadSyncState();
            var manifest = await FetchManifestAsync(cancellationToken);

            if (manifest is not null)
            {
                await SyncSkillsAsync(manifest, syncState, cancellationToken);
            }

            RescanAndUpdateIndex();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "System skill sync failed — continuing with on-disk skills");
            // Still re-scan on-disk skills even if sync failed
            RescanAndUpdateIndex();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private SkillSyncState ReadSyncState()
    {
        if (!File.Exists(_paths.SkillSyncStatePath))
            return new SkillSyncState();

        try
        {
            var json = File.ReadAllText(_paths.SkillSyncStatePath);
            return JsonSerializer.Deserialize<SkillSyncState>(json) ?? new SkillSyncState();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read sync state — starting fresh");
            return new SkillSyncState();
        }
    }

    private void WriteSyncState(SkillSyncState state)
    {
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_paths.SkillSyncStatePath, json);
    }

    private async Task<SkillFeedManifest?> FetchManifestAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(FeedConstants.FeedHttpTimeout);

            var response = await _httpClient.GetAsync(
                FeedConstants.SystemSkillsManifestUrl, cts.Token);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            var manifest = JsonSerializer.Deserialize<SkillFeedManifest>(json);

            if (manifest is null)
            {
                _logger.LogWarning("Feed manifest deserialized to null");
                return null;
            }

            if (manifest.SchemaVersion != 1)
            {
                _logger.LogWarning("Unsupported feed schema version {Version} — skipping sync",
                    manifest.SchemaVersion);
                return null;
            }

            _logger.LogInformation("Fetched skill feed manifest ({SkillCount} skills, updated {UpdatedAt})",
                manifest.Skills.Count, manifest.UpdatedAt);
            return manifest;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Feed manifest fetch timed out — using on-disk skills");
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Feed manifest fetch failed: {Message} — using on-disk skills", ex.Message);
            return null;
        }
    }

    private async Task SyncSkillsAsync(
        SkillFeedManifest manifest, SkillSyncState syncState, CancellationToken cancellationToken)
    {
        var updated = false;
        var now = _timeProvider.GetUtcNow();

        foreach (var entry in manifest.Skills)
        {
            // Skip skills that require a newer daemon
            if (!string.IsNullOrEmpty(entry.MinimumDaemonVersion)
                && !IsVersionSatisfied(_daemonVersion, entry.MinimumDaemonVersion))
            {
                _logger.LogDebug(
                    "Skipping skill {SkillName} v{Version} — requires daemon >= {MinVersion} (current: {Current})",
                    entry.Name, entry.Version, entry.MinimumDaemonVersion, _daemonVersion);
                continue;
            }

            // Check if we already have this version
            if (syncState.Skills.TryGetValue(entry.Name, out var existing)
                && existing.Version == entry.Version
                && existing.Sha256 == entry.Sha256)
            {
                continue;
            }

            // Download skill into its directory (skill-name/SKILL.md)
            try
            {
                var skillDir = Path.Combine(_paths.SystemSkillsDirectory, entry.Name);
                Directory.CreateDirectory(skillDir);

                // Download main SKILL.md
                var mainContent = await DownloadAndVerifyAsync(entry.Url, entry.Sha256, entry.Name, cancellationToken);
                if (mainContent is null)
                    continue;
                await File.WriteAllTextAsync(Path.Combine(skillDir, "SKILL.md"), mainContent, cancellationToken);

                // Download resource files if present
                if (entry.Files is { Count: > 0 })
                {
                    var allFilesOk = true;
                    foreach (var file in entry.Files)
                    {
                        var fileContent = await DownloadAndVerifyAsync(file.Url, file.Sha256, $"{entry.Name}/{file.Path}", cancellationToken);
                        if (fileContent is null)
                        {
                            allFilesOk = false;
                            break;
                        }

                        var filePath = Path.Combine(skillDir, file.Path.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                        await File.WriteAllTextAsync(filePath, fileContent, cancellationToken);
                    }

                    if (!allFilesOk)
                        continue;
                }

                syncState.Skills[entry.Name] = new SyncedSkillState
                {
                    Version = entry.Version,
                    Sha256 = entry.Sha256,
                    SyncedAtUtc = now
                };

                _logger.LogInformation("Synced skill {SkillName} v{Version}", entry.Name, entry.Version);
                updated = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to sync skill {SkillName} — keeping existing version", entry.Name);
            }
        }

        // Check for skills removed from manifest — log but don't delete
        foreach (var (name, _) in syncState.Skills)
        {
            if (!manifest.Skills.Exists(s => s.Name == name))
            {
                _logger.LogInformation(
                    "Skill {SkillName} is in sync state but not in manifest — keeping on disk", name);
            }
        }

        if (updated)
        {
            syncState.LastSyncUtc = now;
            WriteSyncState(syncState);
        }
    }

    private async Task<string?> DownloadAndVerifyAsync(
        string url, string expectedSha256, string label, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(FeedConstants.FeedHttpTimeout);

            var content = await _httpClient.GetStringAsync(url, cts.Token);

            // Verify SHA-256
            var hash = ComputeSha256(content);
            if (!string.Equals(hash, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "SHA-256 mismatch for {Label}: expected {Expected}, got {Actual}",
                    label, expectedSha256, hash);
                return null;
            }

            return content;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Download timed out for {Label}", label);
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Download failed for {Label}: {Message}", label, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Re-scans the entire skills directory and rebuilds the registry + context layer.
    /// Called after sync completes (or fails) to ensure the agent sees current skills.
    /// Then enriches all skills with keyword indexes for deterministic auto-loading.
    /// </summary>
    private void RescanAndUpdateIndex()
    {
        _skillRegistry.Clear();
        foreach (var skill in SkillScanner.Scan(_paths.SkillsDirectory))
            _skillRegistry.Register(skill);

        _skillIndexLayer.Update(_skillRegistry.GenerateCompressedIndex());
        _logger.LogInformation("Skill index updated ({SkillCount} skills)", _skillRegistry.GetAll().Count);

        // Always apply fallback keywords first so skills are matchable immediately.
        // LLM-enriched keywords replace these when enrichment completes.
        foreach (var skill in _skillRegistry.GetAll())
            ApplyFallbackKeywords(skill);

        _logger.LogInformation(
            "Fallback keyword index loaded for {SkillCount} skills", _skillRegistry.GetEnrichedKeywords().Count);

        PurgeStaleKeywordCache();

        // Enrich keywords via LLM sidecar (fire and forget).
        // Enriched keywords replace the fallback set when they arrive.
        if (_chatClientProvider is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await EnrichAllSkillsAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Skill keyword enrichment failed");
                }
            });
        }
    }

    // ── Skill keyword enrichment ──

    private async Task EnrichAllSkillsAsync()
    {
        var skills = _skillRegistry.GetAll();
        if (skills.Count == 0)
            return;

        _logger.LogInformation("Starting skill keyword enrichment ({Count} skills)", skills.Count);
        Directory.CreateDirectory(_paths.SkillKeywordCacheDirectory);

        foreach (var skill in skills)
        {
            try
            {
                await EnrichSkillAsync(skill);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Enrichment failed for {SkillName}, using fallback", skill.Name);
                ApplyFallbackKeywords(skill);
            }
        }

        _logger.LogInformation("Skill enrichment complete: {EnrichedCount}/{TotalCount} skills enriched",
            _skillRegistry.GetEnrichedKeywords().Count, skills.Count);
    }

    private async Task EnrichSkillAsync(SkillEntry skill)
    {
        if (!File.Exists(skill.FilePath))
        {
            _logger.LogWarning("Skill file not found: {FilePath}", skill.FilePath);
            ApplyFallbackKeywords(skill);
            return;
        }

        var content = await File.ReadAllTextAsync(skill.FilePath);
        var contentHash = ComputeSha256(content);

        // Check cache
        var cached = LoadCachedKeywords(skill.Name, skill.Version, contentHash);
        if (cached is not null)
        {
            _skillRegistry.SetEnrichedKeywords(skill.Name, cached.Keywords, cached.Phrases);
            _logger.LogDebug("Loaded cached keywords for {SkillName} ({Count} keywords, {PhraseCount} phrases)",
                skill.Name, cached.Keywords.Count, cached.Phrases.Count);
            return;
        }

        // Try sidecar LLM
        var generated = await GenerateKeywordsAsync(skill.Name, content);
        if (generated is null || (generated.Keywords.Count == 0 && generated.Phrases.Count == 0))
        {
            _logger.LogWarning("Sidecar returned no keywords for {SkillName}, using fallback", skill.Name);
            ApplyFallbackKeywords(skill);
            return;
        }

        _skillRegistry.SetEnrichedKeywords(skill.Name, generated.Keywords, generated.Phrases);
        SaveCachedKeywords(skill.Name, skill.Version, contentHash, generated);
        _logger.LogInformation("Enriched {SkillName} with {Count} keywords and {PhraseCount} phrases via LLM",
            skill.Name, generated.Keywords.Count, generated.Phrases.Count);
    }

    private async Task<GeneratedSkillKeywords?> GenerateKeywordsAsync(string skillName, string content)
    {
        if (_chatClientProvider is null)
            return null;

        try
        {
            var client = _chatClientProvider.GetClient(ModelRole.Compaction);
            using var cts = new CancellationTokenSource(EnrichmentTimeout);

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, """
                    You are a keyword extraction assistant. Given a skill document that
                    describes when and how an AI agent should behave, generate a comprehensive
                    list of single words and short phrases (2-3 words max) that a USER might
                    say in a message when they would benefit from this skill's guidance.

                    Focus on USER INTENT, not internal implementation terminology.
                    Prefer phrases that answer: "what is the user trying to get Netclaw to do?"

                    Include:
                    - Domain-specific action verbs users would use (buy, compare, diagnose, cite)
                    - Domain nouns the skill covers (price, product, flight, timeout, memory)
                    - High-signal user phrasings that are specific to this skill's domain

                    Avoid:
                    - generic workflow words that could apply to many skills (check, status, help, use, run, support)
                    - adjectives or filler words unless they are domain-specific
                    - keywords that only describe the skill authoring process rather than user intent
                    - internal category labels unless users actually say them

                    Return ONLY the keywords, one per line, lowercase. No numbering, no
                    explanations, no categories. Favor precise keywords over broad ones.
                    """),
                new(ChatRole.User, content)
            };

            // Disable reasoning/thinking tokens — keyword extraction is a simple task
            // and reasoning bloats the response time from <1s to 25s+ on Qwen models.
            var options = new ChatOptions
            {
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["chat_template_kwargs"] = new Dictionary<string, object>
                    {
                        ["enable_thinking"] = false
                    }
                }
            };

            var response = await client.GetResponseAsync(messages, options, cts.Token);
            var text = response.Messages[^1].Text ?? string.Empty;

            if (string.IsNullOrWhiteSpace(text))
                return null;

            var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var phrases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var tokens = TextTokenizer.Tokenize(line);
                if (tokens.Count == 0)
                    continue;

                if (tokens.Count >= 2)
                {
                    foreach (var phrase in TextTokenizer.MakeBigrams(tokens))
                    {
                        if (!IsGenericPhrase(phrase))
                            phrases.Add(phrase);
                    }
                }

                foreach (var token in tokens)
                {
                    if (!GenericKeywords.Contains(token))
                        keywords.Add(token);
                }
            }

            if (keywords.Count == 0 && phrases.Count == 0)
                return null;

            return new GeneratedSkillKeywords(keywords, phrases);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Enrichment LLM timed out for {SkillName}", skillName);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Enrichment LLM call failed for {SkillName}", skillName);
            return null;
        }
    }

    private void ApplyFallbackKeywords(SkillEntry skill)
    {
        var combined = new List<string>();
        if (!string.IsNullOrWhiteSpace(skill.Triggers))
            combined.Add(skill.Triggers);
        if (!string.IsNullOrWhiteSpace(skill.Description))
            combined.Add(skill.Description);

        var tokens = TextTokenizer.Tokenize(string.Join(' ', combined));
        var keywords = tokens
            .Where(token => !GenericKeywords.Contains(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var phrases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(skill.Triggers))
        {
            foreach (var trigger in skill.Triggers.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var triggerTokens = TextTokenizer.Tokenize(trigger);
                foreach (var phrase in TextTokenizer.MakeBigrams(triggerTokens))
                {
                    if (!IsGenericPhrase(phrase))
                        phrases.Add(phrase);
                }
            }
        }

        if (keywords.Count > 0 || phrases.Count > 0)
            _skillRegistry.SetEnrichedKeywords(skill.Name, keywords, phrases);
    }

    /// <summary>
    /// Removes keyword cache files for skill versions that no longer match any
    /// registered skill. Prevents stale caches from accumulating after upgrades.
    /// </summary>
    private void PurgeStaleKeywordCache()
    {
        if (!Directory.Exists(_paths.SkillKeywordCacheDirectory))
            return;

        var currentKeys = _skillRegistry.GetAll()
            .Select(s => Path.GetFileName(GetKeywordCachePath(s.Name, s.Version)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var purged = 0;
        foreach (var file in Directory.GetFiles(_paths.SkillKeywordCacheDirectory, "*.json"))
        {
            if (!currentKeys.Contains(Path.GetFileName(file)))
            {
                try { File.Delete(file); purged++; }
                catch (IOException ex) { _logger.LogDebug(ex, "Could not delete stale cache file {File}", file); }
            }
        }

        if (purged > 0)
            _logger.LogInformation("Purged {Count} stale keyword cache file(s)", purged);
    }

    // ── Keyword cache I/O ──

    private sealed record GeneratedSkillKeywords(HashSet<string> Keywords, HashSet<string> Phrases);

    private record KeywordCacheEntry(
        string SkillName,
        string? Version,
        string ContentHash,
        string[] Keywords,
        string[]? Phrases = null);

    private string GetKeywordCachePath(string skillName, string? version)
    {
        var fileName = version is not null ? $"{skillName}-{version}.json" : $"{skillName}.json";
        return Path.Combine(_paths.SkillKeywordCacheDirectory, fileName);
    }

    private GeneratedSkillKeywords? LoadCachedKeywords(string skillName, string? version, string contentHash)
    {
        var path = GetKeywordCachePath(skillName, version);
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            var entry = JsonSerializer.Deserialize<KeywordCacheEntry>(json);
            if (entry is null || entry.ContentHash != contentHash)
                return null;

            return new GeneratedSkillKeywords(
                new HashSet<string>(entry.Keywords, StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(entry.Phrases ?? [], StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load keyword cache for {SkillName}, treating as miss", skillName);
            return null;
        }
    }

    private void SaveCachedKeywords(string skillName, string? version, string contentHash, GeneratedSkillKeywords generated)
    {
        try
        {
            var entry = new KeywordCacheEntry(
                skillName,
                version,
                contentHash,
                generated.Keywords.ToArray(),
                generated.Phrases.ToArray());
            var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(GetKeywordCachePath(skillName, version), json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save keyword cache for {SkillName}", skillName);
        }
    }

    internal static string ComputeSha256(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Returns true if <paramref name="current"/> >= <paramref name="minimum"/>.
    /// Uses simple semver major.minor.patch comparison.
    /// </summary>
    internal static bool IsVersionSatisfied(string current, string minimum)
    {
        if (Version.TryParse(current, out var currentVersion)
            && Version.TryParse(minimum, out var minimumVersion))
        {
            return currentVersion >= minimumVersion;
        }

        // If parsing fails, assume satisfied to avoid blocking skills unnecessarily
        return true;
    }

    private static bool IsGenericPhrase(string phrase)
    {
        var tokens = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length > 0 && tokens.All(GenericPhraseTokens.Contains);
    }
}

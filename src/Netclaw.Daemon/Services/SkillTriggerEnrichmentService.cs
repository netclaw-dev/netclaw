using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Skills;
using Netclaw.Actors.Text;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Generates enriched keyword lists from skill content using a sidecar LLM call
/// at scan time. Keywords bridge developer-facing language in SKILL.md to
/// user-facing language for deterministic skill auto-loading.
/// Results are cached per skill version + content hash.
/// </summary>
public sealed class SkillTriggerEnrichmentService : IHostedService
{
    private readonly IChatClientProvider _clientProvider;
    private readonly SkillRegistry _skillRegistry;
    private readonly NetclawPaths _paths;
    private readonly ILogger<SkillTriggerEnrichmentService> _logger;

    private static readonly TimeSpan SidecarTimeout = TimeSpan.FromSeconds(30);

    public SkillTriggerEnrichmentService(
        IChatClientProvider clientProvider,
        SkillRegistry skillRegistry,
        NetclawPaths paths,
        ILogger<SkillTriggerEnrichmentService> logger)
    {
        _clientProvider = clientProvider;
        _skillRegistry = skillRegistry;
        _paths = paths;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Don't block startup — fire and forget
        _ = EnrichAllAsync(cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Enrich all registered skills. Can be called after feed sync to
    /// re-enrich updated skills.
    /// </summary>
    public async Task EnrichAllAsync(CancellationToken ct = default)
    {
        var skills = _skillRegistry.GetAll();
        if (skills.Count == 0)
        {
            _logger.LogDebug("No skills registered, skipping enrichment");
            return;
        }

        Directory.CreateDirectory(_paths.SkillKeywordCacheDirectory);

        foreach (var skill in skills)
        {
            if (ct.IsCancellationRequested)
                break;

            try
            {
                await EnrichSkillAsync(skill, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skill enrichment failed for {SkillName}, using fallback", skill.Name);
                ApplyFallbackKeywords(skill);
            }
        }

        var enrichedCount = _skillRegistry.GetEnrichedKeywords().Count;
        _logger.LogInformation("Skill enrichment complete: {EnrichedCount}/{TotalCount} skills enriched",
            enrichedCount, skills.Count);
    }

    private async Task EnrichSkillAsync(SkillEntry skill, CancellationToken ct)
    {
        // Read skill content
        if (!File.Exists(skill.FilePath))
        {
            _logger.LogWarning("Skill file not found: {FilePath}", skill.FilePath);
            ApplyFallbackKeywords(skill);
            return;
        }

        var content = await File.ReadAllTextAsync(skill.FilePath, ct);
        var contentHash = ComputeHash(content);

        // Check cache
        var cached = LoadCachedKeywords(skill.Name, skill.Version, contentHash);
        if (cached is not null)
        {
            _skillRegistry.SetEnrichedKeywords(skill.Name, cached);
            _logger.LogDebug("Loaded cached keywords for {SkillName} ({Count} keywords)",
                skill.Name, cached.Count);
            return;
        }

        // Call sidecar LLM
        var keywords = await GenerateKeywordsAsync(skill.Name, content, ct);
        if (keywords is null || keywords.Count == 0)
        {
            _logger.LogWarning("Sidecar returned no keywords for {SkillName}, using fallback", skill.Name);
            ApplyFallbackKeywords(skill);
            return;
        }

        _skillRegistry.SetEnrichedKeywords(skill.Name, keywords);
        SaveCachedKeywords(skill.Name, skill.Version, contentHash, keywords);
        _logger.LogInformation("Enriched {SkillName} with {Count} keywords via sidecar",
            skill.Name, keywords.Count);
    }

    private async Task<HashSet<string>?> GenerateKeywordsAsync(
        string skillName, string content, CancellationToken ct)
    {
        try
        {
            var client = _clientProvider.GetClient(ModelRole.Compaction);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(SidecarTimeout);

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, """
                    You are a keyword extraction assistant. Given a skill document that
                    describes when and how an AI agent should behave, generate a comprehensive
                    list of single words and short phrases (2-3 words max) that a USER might
                    say in a message when they would benefit from this skill's guidance.

                    Include:
                    - Action verbs users would use (buy, find, compare, book, etc.)
                    - Domain nouns the skill covers (price, product, flight, restaurant, etc.)
                    - Common user phrasings (how much, best deal, where to, etc.)
                    - Adjectives users might use (cheap, expensive, best, good, etc.)

                    Return ONLY the keywords, one per line, lowercase. No numbering, no
                    explanations, no categories. Just the words.
                    """),
                new(ChatRole.User, content)
            };

            var response = await client.GetResponseAsync(messages, cancellationToken: cts.Token);
            var text = response.Messages[^1].Text ?? string.Empty;

            if (string.IsNullOrWhiteSpace(text))
                return null;

            var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // Tokenize each line to normalize (lowercase, strip stopwords, normalize plurals)
                foreach (var token in TextTokenizer.Tokenize(line))
                    keywords.Add(token);
            }

            return keywords.Count > 0 ? keywords : null;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Sidecar timed out for {SkillName}", skillName);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sidecar call failed for {SkillName}", skillName);
            return null;
        }
    }

    /// <summary>
    /// Fallback: tokenize the skill's triggers and description as basic keywords.
    /// </summary>
    private void ApplyFallbackKeywords(SkillEntry skill)
    {
        var text = (skill.Triggers ?? string.Empty) + " " + skill.Description;
        var keywords = TextTokenizer.Tokenize(text).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (keywords.Count > 0)
            _skillRegistry.SetEnrichedKeywords(skill.Name, keywords);
    }

    // ── Cache I/O ──

    private record CacheEntry(string SkillName, string? Version, string ContentHash, string[] Keywords);

    private string GetCachePath(string skillName, string? version)
    {
        var fileName = version is not null ? $"{skillName}-{version}.json" : $"{skillName}.json";
        return Path.Combine(_paths.SkillKeywordCacheDirectory, fileName);
    }

    private HashSet<string>? LoadCachedKeywords(string skillName, string? version, string contentHash)
    {
        var path = GetCachePath(skillName, version);
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            var entry = JsonSerializer.Deserialize<CacheEntry>(json);
            if (entry is null || entry.ContentHash != contentHash)
                return null;

            return new HashSet<string>(entry.Keywords, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load keyword cache for {SkillName}, treating as miss", skillName);
            return null;
        }
    }

    private void SaveCachedKeywords(string skillName, string? version, string contentHash, HashSet<string> keywords)
    {
        try
        {
            var entry = new CacheEntry(skillName, version, contentHash, keywords.ToArray());
            var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(GetCachePath(skillName, version), json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save keyword cache for {SkillName}", skillName);
        }
    }

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
    }
}

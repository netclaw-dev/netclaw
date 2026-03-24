using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Skills;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Generates short trigger phrases for skills via a single LLM sidecar call at scan time.
/// Results are cached to disk and used in the compressed skill index.
/// This bridges operator language (how skills are written) to user language
/// (how users actually talk) for better LLM-driven skill activation.
/// </summary>
internal sealed class SkillIndexEnrichmentService : IHostedService
{
    private static readonly TimeSpan SidecarTimeout = TimeSpan.FromSeconds(30);

    private const string SystemPrompt =
        "You generate concise trigger phrases for an AI agent's skill index. " +
        "For each skill, produce a single phrase (5-15 words) that describes when a user " +
        "would need this skill. Use everyday user language, not technical jargon. " +
        "Return a JSON object mapping skill names to trigger phrases. Example: " +
        "{\"my-skill\": \"when the user wants to do something\"}";

    private readonly SkillRegistry _skillRegistry;
    private readonly SkillIndexContextLayer _skillIndexLayer;
    private readonly NetclawPaths _paths;
    private readonly IChatClientProvider? _clientProvider;
    private readonly ILogger<SkillIndexEnrichmentService> _logger;

    public SkillIndexEnrichmentService(
        SkillRegistry skillRegistry,
        SkillIndexContextLayer skillIndexLayer,
        NetclawPaths paths,
        ILogger<SkillIndexEnrichmentService> logger,
        IChatClientProvider? clientProvider = null)
    {
        _skillRegistry = skillRegistry;
        _skillIndexLayer = skillIndexLayer;
        _paths = paths;
        _clientProvider = clientProvider;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() => RunEnrichmentAsync(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    private async Task RunEnrichmentAsync(CancellationToken cancellationToken)
    {
        try
        {
            var phrases = await EnrichAllSkillsAsync(cancellationToken);
            _skillRegistry.SetTriggerPhrases(phrases);
            _skillIndexLayer.Update(_skillRegistry.GenerateDescriptionMenu());

            var enrichedCount = phrases.Count;
            var totalCount = _skillRegistry.GetAll().Count;
            _logger.LogInformation(
                "Skill index enrichment complete ({EnrichedCount} enriched, {FallbackCount} fallback)",
                enrichedCount, totalCount - enrichedCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Skill enrichment cancelled — daemon shutting down");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Skill index enrichment failed — using truncated descriptions");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task<Dictionary<string, string>> EnrichAllSkillsAsync(
        CancellationToken cancellationToken)
    {
        var cacheDir = Path.Combine(_paths.CacheDirectory, "skill-index");
        Directory.CreateDirectory(cacheDir);

        var skills = _skillRegistry.GetAll();
        var phrases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var uncached = new List<SkillEntry>();

        // Phase 1: load from cache
        foreach (var skill in skills)
        {
            var cached = TryReadCache(cacheDir, skill);
            if (cached is not null)
                phrases[skill.Name] = cached;
            else
                uncached.Add(skill);
        }

        // Phase 2: single LLM call for all uncached skills
        if (uncached.Count > 0)
        {
            var generated = await TryGenerateBatchAsync(uncached, cancellationToken);
            if (generated is not null)
            {
                foreach (var (name, phrase) in generated)
                {
                    phrases[name] = phrase;
                    var skill = uncached.FirstOrDefault(
                        s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (skill is not null)
                        WriteCache(cacheDir, skill, phrase);
                }
            }
        }

        return phrases;
    }

    private async Task<Dictionary<string, string>?> TryGenerateBatchAsync(
        IReadOnlyList<SkillEntry> skills,
        CancellationToken cancellationToken)
    {
        if (_clientProvider is null)
            return null;

        try
        {
            var client = _clientProvider.GetClient(ModelRole.Compaction);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(SidecarTimeout);

            var skillList = string.Join("\n", skills.Select(
                s => $"- {s.Name}: \"{s.Description}\""));

            var userPrompt = $"Generate a trigger phrase for each skill:\n\n{skillList}";
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, SystemPrompt),
                new(ChatRole.User, userPrompt)
            };

            var response = await client.GetResponseAsync(messages, cancellationToken: cts.Token);
            var text = response.Messages[^1].Text?.Trim();

            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogDebug("Sidecar returned empty response for batch enrichment");
                return null;
            }

            return ParseBatchResponse(text, skills);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Sidecar timed out for batch enrichment ({SkillCount} skills)", skills.Count);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Sidecar failed for batch enrichment");
            return null;
        }
    }

    private Dictionary<string, string>? ParseBatchResponse(
        string text, IReadOnlyList<SkillEntry> skills)
    {
        // Strip markdown fences if present
        var cleaned = text.Trim();
        if (cleaned.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = cleaned.IndexOf('\n', StringComparison.Ordinal);
            if (firstNewline >= 0)
            {
                cleaned = cleaned[(firstNewline + 1)..];
                var lastFence = cleaned.LastIndexOf("```", StringComparison.Ordinal);
                if (lastFence >= 0)
                    cleaned = cleaned[..lastFence];
            }
        }

        try
        {
            var doc = JsonDocument.Parse(cleaned);
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var skill in skills)
            {
                // Try exact match, then case-insensitive scan
                if (doc.RootElement.TryGetProperty(skill.Name, out var prop))
                {
                    var phrase = prop.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(phrase))
                        result[skill.Name] = phrase.Length > 200 ? phrase[..197] + "..." : phrase;
                }
                else
                {
                    // Case-insensitive fallback
                    foreach (var jsonProp in doc.RootElement.EnumerateObject())
                    {
                        if (jsonProp.Name.Equals(skill.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            var phrase = jsonProp.Value.GetString()?.Trim();
                            if (!string.IsNullOrWhiteSpace(phrase))
                                result[skill.Name] = phrase.Length > 200 ? phrase[..197] + "..." : phrase;
                            break;
                        }
                    }
                }
            }

            return result.Count > 0 ? result : null;
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Failed to parse batch enrichment response as JSON");
            return null;
        }
    }

    // ── Cache ────────────────────────────────────────────────────────

    private static string? TryReadCache(string cacheDir, SkillEntry skill)
    {
        if (skill.Version is null)
            return null;

        var cachePath = GetCachePath(cacheDir, skill);
        if (!File.Exists(cachePath))
            return null;

        try
        {
            var json = File.ReadAllText(cachePath);
            var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("triggerPhrase", out var prop)
                ? prop.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteCache(string cacheDir, SkillEntry skill, string phrase)
    {
        if (skill.Version is null)
            return;

        var cachePath = GetCachePath(cacheDir, skill);
        var json = JsonSerializer.Serialize(new { triggerPhrase = phrase });
        File.WriteAllText(cachePath, json);
    }

    private static string GetCachePath(string cacheDir, SkillEntry skill)
        => Path.Combine(cacheDir, $"{skill.Name}-{skill.Version}.json");
}

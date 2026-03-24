using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Skills;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Generates short trigger phrases for skills via an LLM sidecar call at scan time.
/// Results are cached to disk and used in the compressed skill index.
/// This bridges operator language (how skills are written) to user language
/// (how users actually talk) for better LLM-driven skill activation.
/// </summary>
internal sealed class SkillIndexEnrichmentService : IHostedService
{
    private static readonly TimeSpan SidecarTimeout = TimeSpan.FromSeconds(10);

    private const string SystemPrompt =
        "You generate concise trigger phrases for an AI agent's skill index. " +
        "Given a skill's name and description, produce a single phrase (5-15 words) " +
        "that describes when a user would need this skill. Use everyday user language, " +
        "not technical jargon or operator language. Output ONLY the phrase, nothing else.";

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
        // Fire and forget — enrichment runs in the background so it never
        // blocks daemon startup. Fallback descriptions are already in place.
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
            _logger.LogInformation(
                "Skill index enrichment complete ({EnrichedCount} enriched, {FallbackCount} fallback)",
                phrases.Count(p => !string.IsNullOrEmpty(p.Value)),
                _skillRegistry.GetAll().Count - phrases.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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

        foreach (var skill in skills)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Check disk cache
            var cached = TryReadCache(cacheDir, skill);
            if (cached is not null)
            {
                phrases[skill.Name] = cached;
                continue;
            }

            // Try LLM sidecar
            var generated = await TryGeneratePhraseAsync(skill, cancellationToken);
            if (generated is not null)
            {
                phrases[skill.Name] = generated;
                WriteCache(cacheDir, skill, generated);
            }
            // else: no cache, no LLM — fallback to truncated description (handled by registry)
        }

        return phrases;
    }

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
            return null; // Corrupt cache — will re-generate
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

    private async Task<string?> TryGeneratePhraseAsync(
        SkillEntry skill,
        CancellationToken cancellationToken)
    {
        if (_clientProvider is null)
            return null;

        try
        {
            var client = _clientProvider.GetClient(ModelRole.Compaction);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(SidecarTimeout);

            var userPrompt = $"Skill name: {skill.Name}\nDescription: {skill.Description}";
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, SystemPrompt),
                new(ChatRole.User, userPrompt)
            };

            var response = await client.GetResponseAsync(messages, cancellationToken: cts.Token);
            var text = response.Messages[^1].Text?.Trim();

            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogDebug("Sidecar returned empty phrase for {SkillName}", skill.Name);
                return null;
            }

            // Sanity: phrase should be reasonably short
            return text.Length > 200 ? text[..197] + "..." : text;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Sidecar timed out for {SkillName}", skill.Name);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Sidecar failed for {SkillName}", skill.Name);
            return null;
        }
    }
}

using Netclaw.Actors.Memory;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Automatic recall coordinator over SQLite-backed durable memory.
/// </summary>
public sealed class SQLiteMemoryRecallCoordinator(
    SQLiteMemoryStore store,
    ILogger<SQLiteMemoryRecallCoordinator> logger,
    IChatClientProvider? clientProvider = null,
    SidecarRecallPlanner? sidecarPlanner = null,
    RecallPlanGate? recallPlanGate = null,
    SessionConfig? sessionConfig = null) : IMemoryRecallCoordinator
{
    private readonly SidecarRecallPlanner _sidecarPlanner = sidecarPlanner ?? new SidecarRecallPlanner();
    private readonly RecallPlanGate _recallPlanGate = recallPlanGate ?? new RecallPlanGate();
    private readonly SessionConfig _sessionConfig = sessionConfig ?? new SessionConfig();
    private readonly DeterministicRetrievalRequestPlanner _deterministicPlanner = new();
    private readonly DeterministicCandidateSelector _candidateSelector = new();

    public async Task<AutomaticRecallResult> RecallAsync(AutomaticRecallRequest request, CancellationToken ct = default)
    {
        try
        {
            if (_sessionConfig.DeterministicRetrievalEnabled)
            {
                DeterministicRetrievalRequestPlan deterministicPlan;
                try
                {
                    deterministicPlan = _deterministicPlanner.Plan(request);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "memory_recall_degraded stage=planning reason={Reason}", ex.Message);
                    return new AutomaticRecallResult([], true, ex.Message, "planning");
                }

                logger.LogInformation(
                    "memory_retrieval_request_plan hardScope={HardScope} mode={Mode} candidateLimit={CandidateLimit} facets={Facets} softScopes={SoftScopes} anchorHints={AnchorHints} lexicalTerms={LexicalTerms}",
                    deterministicPlan.HardScope,
                    deterministicPlan.RetrievalMode,
                    deterministicPlan.CandidateLimit,
                    string.Join("|", deterministicPlan.Facets),
                    string.Join("|", deterministicPlan.SoftScopes),
                    string.Join("|", deterministicPlan.AnchorHints),
                    string.Join("|", deterministicPlan.LexicalTerms));

                var rawCandidates = await store.SearchByPlanAsync(
                    deterministicPlan.LexicalTerms.Count > 0 ? deterministicPlan.LexicalTerms : [request.Query],
                    deterministicPlan.HardScope,
                    deterministicPlan.AllowedMemoryClasses,
                    deterministicPlan.CandidateLimit,
                    allowExpiredEvidence: false,
                    ct);

                var widened = false;
                if (rawCandidates.Count == 0 && ShouldWidenAcrossDomains(deterministicPlan))
                {
                    rawCandidates = await store.SearchAcrossDomainsByPlanAsync(
                        deterministicPlan.LexicalTerms.Count > 0 ? deterministicPlan.LexicalTerms : [request.Query],
                        deterministicPlan.AllowedMemoryClasses,
                        deterministicPlan.CandidateLimit,
                        allowExpiredEvidence: false,
                        ct);
                    widened = true;
                }

                var candidates = _candidateSelector.Select(deterministicPlan, rawCandidates);
                logger.LogInformation(
                    "memory_retrieval_candidate_selection hardScope={HardScope} widenedAcrossDomains={WidenedAcrossDomains} rawCount={RawCount} selectedCount={SelectedCount} ids={Ids}",
                    deterministicPlan.HardScope,
                    widened,
                    rawCandidates.Count,
                    candidates.Count,
                    string.Join("|", candidates.Select(x => x.Id)));

                var deterministicItems = candidates
                    .OrderByDescending(RecallRank)
                    .Take(request.MaxItems <= 0 ? 3 : request.MaxItems)
                    .Select(d => new AutomaticRecallItem(
                        d.Id,
                        d.Title,
                        d.Content,
                        d.Domain,
                        d.Sensitivity,
                        RecallRank(d)))
                    .ToArray();

                return new AutomaticRecallResult(deterministicItems);
            }

            if (!_sessionConfig.MemorySidecarsEnabled)
                return new AutomaticRecallResult([]);

            var domain = ResolveDomain(request.SessionId);
            var maxItems = request.MaxItems <= 0 ? 3 : request.MaxItems;
            var effectiveQuery = string.IsNullOrWhiteSpace(request.Query)
                ? request.RecentUserMessages.LastOrDefault() ?? string.Empty
                : request.Query;

            var fallbackRequest = _sidecarPlanner.BuildRequest(
                request.SessionId,
                domain,
                effectiveQuery,
                request.RecentUserMessages,
                request.RecentAssistantMessages ?? [],
                request.RecentEntities ?? [],
                "automatic",
                8,
                maxItems);

            var plan = await BuildPlanAsync(request, domain, effectiveQuery, maxItems, ct)
                ?? _recallPlanGate.Clamp(new RecallQueryPlan(
                    "automatic",
                    "fallback",
                    request.RecentEntities ?? [],
                    [],
                    FallbackSearchTerms(effectiveQuery, request.RecentUserMessages),
                    ["durable_fact"],
                    maxItems,
                    false),
                    fallbackRequest);

            logger.LogInformation(
                "memory_recall_plan_resolved mode={Mode} intent={Intent} classes={Classes} allowExpiredEvidence={AllowExpiredEvidence} searchTerms={SearchTerms}",
                plan.Mode,
                plan.Intent,
                string.Join("|", plan.MemoryClasses),
                plan.AllowExpiredEvidence,
                string.Join("|", plan.SearchTerms));

            var searchQuery = string.Join(' ', plan.SearchTerms);

            var primary = await store.SearchByPlanAsync(
                plan.SearchTerms,
                domain,
                plan.MemoryClasses,
                Math.Max(maxItems * 3, 12),
                plan.AllowExpiredEvidence,
                ct);

            var documents = primary;
            string? fallbackQuery = null;
            if (documents.Count == 0 && request.RecentUserMessages.Count > 0)
            {
                fallbackQuery = request.RecentUserMessages[^1];
                documents = await store.SearchByPlanAsync(
                    fallbackQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    domain,
                    plan.MemoryClasses,
                    Math.Max(maxItems * 3, 12),
                    plan.AllowExpiredEvidence,
                    ct);
            }

            LogRecallTrace(
                searchQuery,
                fallbackQuery,
                domain,
                maxItems,
                primary.Count,
                documents.Count,
                documents.Select(d => d.Id));

            var items = documents
                .OrderByDescending(RecallRank)
                .Take(maxItems)
                .Select(d => new AutomaticRecallItem(
                    d.Id,
                    d.Title,
                    d.Content,
                    d.Domain,
                    d.Sensitivity,
                    RecallRank(d)))
                .ToArray();

            return new AutomaticRecallResult(items);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "memory_recall_degraded stage=execution reason={Reason}", ex.Message);
            return new AutomaticRecallResult([], true, ex.Message, "execution");
        }
    }

    private static string ResolveDomain(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return "project:default";

        var slash = sessionId.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0)
            return "project:default";

        var prefix = sessionId[..slash].Trim();
        return string.IsNullOrWhiteSpace(prefix)
            ? "project:default"
            : $"project:{prefix.ToLowerInvariant()}";
    }

    private static bool ShouldWidenAcrossDomains(DeterministicRetrievalRequestPlan plan)
        => plan.AnchorHints.Count > 0
           || plan.Facets.Contains("project_fact", StringComparer.OrdinalIgnoreCase);

    private async Task<RecallQueryPlan?> BuildPlanAsync(
        AutomaticRecallRequest request,
        string domain,
        string effectiveQuery,
        int maxItems,
        CancellationToken ct)
    {
        if (clientProvider is null)
            return null;

        if (!_sessionConfig.MemorySidecarsEnabled)
            return null;

        var plannerRequest = _sidecarPlanner.BuildRequest(
            request.SessionId,
            domain,
            effectiveQuery,
            request.RecentUserMessages,
            request.RecentAssistantMessages ?? [],
            request.RecentEntities ?? [],
            "automatic",
            8,
            maxItems);

        var timeout = TimeSpan.FromSeconds(Math.Max(1, _sessionConfig.SidecarLlmTimeoutSeconds));
        var plan = await SessionSidecarRunner.RunJsonAsync<RecallQueryPlan>(
            clientProvider.GetClient(Configuration.ModelRole.Compaction),
            MemorySidecarPromptBuilder.BuildRecallPlanningSystemPrompt(),
            MemorySidecarPromptBuilder.BuildRecallPlanningUserPrompt(plannerRequest),
            timeout,
            message => logger.LogWarning("Recall planner sidecar failed: {Message}", message));

        if (plan is null)
        {
            logger.LogWarning(
                "memory_recall_plan_fallback reason=sidecar_null_or_invalid session={SessionId} domain={Domain}",
                request.SessionId,
                domain);

            return _recallPlanGate.Clamp(new RecallQueryPlan(
                    "automatic",
                    "fallback",
                    request.RecentEntities ?? [],
                    [],
                    FallbackSearchTerms(effectiveQuery, request.RecentUserMessages),
                    ["durable_fact"],
                    maxItems,
                    false),
                plannerRequest);
        }

        return _recallPlanGate.Clamp(plan, plannerRequest);
    }

    private void LogRecallTrace(
        string query,
        string? fallbackQuery,
        string domain,
        int maxItems,
        int primaryCount,
        int selectedCount,
        IEnumerable<string> selectedDocumentIds)
    {
        var queryTerms = TokenizeTerms(query);
        var fallbackTerms = string.IsNullOrWhiteSpace(fallbackQuery)
            ? Array.Empty<string>()
            : TokenizeTerms(fallbackQuery);
        var selectedIds = string.Join(",", selectedDocumentIds.Take(maxItems));

        logger.LogInformation(
            "memory_recall_query_trace domain={Domain} maxItems={MaxItems} primaryCount={PrimaryCount} selectedCount={SelectedCount} queryTerms={QueryTerms} fallbackTerms={FallbackTerms} selectedIds={SelectedIds}",
            domain,
            maxItems,
            primaryCount,
            selectedCount,
            string.Join("|", queryTerms),
            string.Join("|", fallbackTerms),
            string.IsNullOrWhiteSpace(selectedIds) ? "-" : selectedIds);
    }

    private static string[] TokenizeTerms(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        return value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
    }

    private static IReadOnlyList<string> FallbackSearchTerms(string query, IReadOnlyList<string> recentUserMessages)
    {
        var combined = new List<string>();
        if (!string.IsNullOrWhiteSpace(query))
            combined.Add(query);
        combined.AddRange(recentUserMessages);

        return combined
            .SelectMany(x => x.Split(new[] { ' ', '\t', '\n', '\r', '.', ',', ':', ';', '!', '?', '(', ')', '[', ']', '{', '}', '/', '\\', '"', '\'' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(x => x.Trim().ToLowerInvariant())
            .Where(x => x.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
    }

    private static int RecallRank(SQLiteMemoryHydratedItem document)
    {
        var score = 0;

        // Prefer deterministic durable classes and explicit/inferred semantics.
        if (string.Equals(document.MemoryClass, "durable_fact", StringComparison.OrdinalIgnoreCase))
            score += 120;
        else if (string.Equals(document.MemoryClass, "evidence", StringComparison.OrdinalIgnoreCase))
            score += 40;
        else if (string.Equals(document.MemoryClass, "trace", StringComparison.OrdinalIgnoreCase))
            score -= 400;

        if (string.Equals(document.UpdateSemantics, "merge-document", StringComparison.OrdinalIgnoreCase))
            score += 80;
        else if (string.Equals(document.UpdateSemantics, "append-document", StringComparison.OrdinalIgnoreCase))
            score += 60;
        else if (string.Equals(document.UpdateSemantics, "conversation_trace", StringComparison.OrdinalIgnoreCase))
            score -= 300;

        if (string.Equals(document.UpdateSemantics, "immutable-record", StringComparison.OrdinalIgnoreCase))
            score += 30;

        if (string.Equals(document.Title, "turn-completion", StringComparison.OrdinalIgnoreCase))
            score -= 200;

        if (string.Equals(document.Title, "verified-tool-finding", StringComparison.OrdinalIgnoreCase))
            score += 25;

        if (document.ExpiresAtMs.HasValue)
            score += 5;

        return score;
    }
}

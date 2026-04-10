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
    SessionTuning? sessionTuning = null,
    SessionConfig? sessionConfig = null) : IMemoryRecallCoordinator
{
    private readonly SidecarRecallPlanner _sidecarPlanner = sidecarPlanner ?? new SidecarRecallPlanner();
    private readonly RecallPlanGate _recallPlanGate = recallPlanGate ?? new RecallPlanGate();
    private readonly SessionTuning _sessionTuning = sessionTuning ?? new SessionTuning();
    private readonly SessionConfig _sessionConfig = sessionConfig ?? new SessionConfig();
    private readonly DeterministicRetrievalRequestPlanner _deterministicPlanner = new();
    private readonly DeterministicCandidateSelector _candidateSelector = new();

    /// <summary>
    /// Default minimum composite score a candidate must reach to survive
    /// recall. Chosen so that a single lexical-token collision against an
    /// out-of-domain durable fact (selector ~5, composite ~7) is rejected,
    /// while a legitimate two-lexical match in a durable fact (selector ~9,
    /// composite ~11) still passes. Callers who need stricter or looser
    /// filtering can override via <see cref="SessionTuning.MinimumRecallCompositeScore"/>.
    /// See <see cref="MemoryRecallScenarioTests"/> and issue #582 for the
    /// pollution patterns this guards against.
    /// </summary>
    private const double DefaultMinimumRecallCompositeScore = 10.0;

    public async Task<AutomaticRecallResult> RecallAsync(AutomaticRecallRequest request, CancellationToken ct = default)
    {
        try
        {
            var normalizedRequest = NormalizeRequest(request);

            if (_sessionTuning.DeterministicRetrievalEnabled)
            {
                DeterministicRetrievalRequestPlan deterministicPlan;
                try
                {
                    deterministicPlan = _deterministicPlanner.Plan(normalizedRequest);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "memory_recall_degraded session={SessionId} stage=planning reason={Reason}", normalizedRequest.SessionId, ex.Message);
                    return new AutomaticRecallResult([], true, ex.Message, "planning");
                }

                logger.LogInformation(
                    "memory_retrieval_request_plan session={SessionId} hardScope={HardScope} mode={Mode} candidateLimit={CandidateLimit} facets={Facets} softScopes={SoftScopes} anchorHints={AnchorHints} lexicalTerms={LexicalTerms}",
                    normalizedRequest.SessionId,
                    deterministicPlan.HardScope,
                    deterministicPlan.RetrievalMode,
                    deterministicPlan.CandidateLimit,
                    string.Join("|", deterministicPlan.Facets),
                    string.Join("|", deterministicPlan.SoftScopes),
                    string.Join("|", deterministicPlan.AnchorHints),
                    string.Join("|", deterministicPlan.LexicalTerms));

                var effectiveBoundary = ResolveBoundary(normalizedRequest, deterministicPlan.HardScope);

                // Audience-primary recall: audience+boundary are the SQL
                // security filters. Domain is not used as a ranking preference
                // (see #584 — the affinity concept was disabled because
                // ToMemoryDomain() always resolves to project:default).
                var rawCandidates = await store.SearchAcrossDomainsByPlanAsync(
                    deterministicPlan.LexicalTerms.Count > 0 ? deterministicPlan.LexicalTerms : [normalizedRequest.Query],
                    deterministicPlan.AllowedMemoryClasses,
                    deterministicPlan.CandidateLimit,
                    effectiveBoundary,
                    normalizedRequest.Audience,
                    allowExpiredEvidence: false,
                    ct);

                var scoredCandidates = _candidateSelector.SelectWithScores(deterministicPlan, rawCandidates);
                logger.LogInformation(
                    "memory_retrieval_candidate_selection session={SessionId} hardScope={HardScope} rawCount={RawCount} selectedCount={SelectedCount} scored={Scored}",
                    normalizedRequest.SessionId,
                    deterministicPlan.HardScope,
                    rawCandidates.Count,
                    scoredCandidates.Count,
                    string.Join("|", scoredCandidates.Select(x => $"{x.Item.Id}={x.SelectorScore:F1}")));

                // RecallRank dampened by 100x so it acts as a tiebreaker (~2 points
                // for DurableFact+MergeDocument) rather than overriding SelectorScore
                // (~4 points per lexical match).
                const double RecallRankDampeningFactor = 100.0;
                var deterministicMaxItems = normalizedRequest.MaxItems <= 0 ? 3 : normalizedRequest.MaxItems;
                var minimumCompositeScore = _sessionTuning.MinimumRecallCompositeScore ?? DefaultMinimumRecallCompositeScore;
                var rankedCandidates = scoredCandidates
                    .Select(x => (x.Item, x.SelectorScore, Composite: x.SelectorScore + (RecallRank(x.Item) / RecallRankDampeningFactor)))
                    .OrderByDescending(x => x.Composite)
                    .ToArray();
                var aboveFloor = rankedCandidates
                    .Where(x => x.Composite >= minimumCompositeScore)
                    .ToArray();
                var deterministicItems = aboveFloor
                    .Take(deterministicMaxItems)
                    .Select(x => new AutomaticRecallItem(
                        x.Item.Id,
                        x.Item.Title,
                        x.Item.Content,
                        x.Item.Domain,
                        x.Item.Sensitivity,
                        x.Composite))
                    .ToArray();

                logger.LogInformation(
                    "memory_retrieval_final session={SessionId} injectedCount={InjectedCount} filteredByFloor={FilteredByFloor} appliedFloor={AppliedFloor:F1} items={Items}",
                    normalizedRequest.SessionId,
                    deterministicItems.Length,
                    rankedCandidates.Length - aboveFloor.Length,
                    minimumCompositeScore,
                    string.Join("|", deterministicItems.Select(i => $"{i.Id}=score{i.Score:F1}")));

                logger.LogDebug(
                    "memory_retrieval_final_detail session={SessionId} items={Items}",
                    normalizedRequest.SessionId,
                    string.Join("|", deterministicItems.Select(i => $"{i.Id}={i.Title}")));

                return new AutomaticRecallResult(deterministicItems);
            }

            if (!_sessionTuning.MemorySidecarsEnabled)
                return new AutomaticRecallResult([]);

            var domain = string.IsNullOrWhiteSpace(normalizedRequest.HardScopeOverride)
                ? new Protocol.SessionId(normalizedRequest.SessionId).ToMemoryDomain()
                : normalizedRequest.HardScopeOverride!;
            var fallbackBoundary = ResolveBoundary(normalizedRequest, domain);
            var maxItems = normalizedRequest.MaxItems <= 0 ? 3 : normalizedRequest.MaxItems;
            var effectiveQuery = string.IsNullOrWhiteSpace(normalizedRequest.Query)
                ? normalizedRequest.RecentUserMessages.LastOrDefault() ?? string.Empty
                : normalizedRequest.Query;

            var fallbackRequest = _sidecarPlanner.BuildRequest(
                normalizedRequest.SessionId,
                domain,
                effectiveQuery,
                normalizedRequest.RecentUserMessages,
                normalizedRequest.RecentAssistantMessages ?? [],
                normalizedRequest.RecentEntities ?? [],
                "automatic",
                8,
                maxItems);

            var plan = await BuildPlanAsync(normalizedRequest, domain, effectiveQuery, maxItems, ct)
                ?? _recallPlanGate.Clamp(new RecallQueryPlan(
                    "automatic",
                    "fallback",
                    normalizedRequest.RecentEntities ?? [],
                    [],
                    FallbackSearchTerms(effectiveQuery, normalizedRequest.RecentUserMessages),
                    [Memory.MemoryClass.DurableFact.ToWireValue()],
                    maxItems,
                    false),
                    fallbackRequest);

            logger.LogInformation(
                "memory_recall_plan_resolved session={SessionId} mode={Mode} intent={Intent} classes={Classes} allowExpiredEvidence={AllowExpiredEvidence} searchTerms={SearchTerms}",
                normalizedRequest.SessionId,
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
                fallbackBoundary,
                normalizedRequest.Audience,
                plan.AllowExpiredEvidence,
                ct);

            var documents = primary;
            string? fallbackQuery = null;
            if (documents.Count == 0 && normalizedRequest.RecentUserMessages.Count > 0)
            {
                fallbackQuery = normalizedRequest.RecentUserMessages[^1];
                documents = await store.SearchByPlanAsync(
                    fallbackQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    domain,
                    plan.MemoryClasses,
                    Math.Max(maxItems * 3, 12),
                    fallbackBoundary,
                    normalizedRequest.Audience,
                    plan.AllowExpiredEvidence,
                    ct);
            }

            LogRecallTrace(
                normalizedRequest.SessionId,
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
            logger.LogWarning(ex, "memory_recall_degraded session={SessionId} stage=execution reason={Reason}", request.SessionId, ex.Message);
            return new AutomaticRecallResult([], true, ex.Message, "execution");
        }
    }

    private static AutomaticRecallRequest NormalizeRequest(AutomaticRecallRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.HardScopeOverride))
            return request;

        var hardScope = string.IsNullOrWhiteSpace(request.SessionId)
            ? SecurityPolicyDefaults.DefaultMemoryDomain
            : new Protocol.SessionId(request.SessionId).ToMemoryDomain();

        return request with { HardScopeOverride = hardScope };
    }

    private static string ResolveBoundary(AutomaticRecallRequest request, string domain)
        => !string.IsNullOrWhiteSpace(request.Boundary)
            ? request.Boundary!
            : SecurityPolicyDefaults.InferLegacyBoundaryFromDomain(domain);

    private async Task<RecallQueryPlan?> BuildPlanAsync(
        AutomaticRecallRequest request,
        string domain,
        string effectiveQuery,
        int maxItems,
        CancellationToken ct)
    {
        if (clientProvider is null)
            return null;

        if (!_sessionTuning.MemorySidecarsEnabled)
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

        var timeout = _sessionConfig.SidecarLlmTimeout;
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
                    [Memory.MemoryClass.DurableFact.ToWireValue()],
                    maxItems,
                    false),
                plannerRequest);
        }

        return _recallPlanGate.Clamp(plan, plannerRequest);
    }

    private void LogRecallTrace(
        string sessionId,
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
            "memory_recall_query_trace session={SessionId} domain={Domain} maxItems={MaxItems} primaryCount={PrimaryCount} selectedCount={SelectedCount} queryTerms={QueryTerms} fallbackTerms={FallbackTerms} selectedIds={SelectedIds}",
            sessionId,
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
        if (string.Equals(document.MemoryClass, Memory.MemoryClass.DurableFact.ToWireValue(), StringComparison.OrdinalIgnoreCase))
            score += 120;
        else if (string.Equals(document.MemoryClass, Memory.MemoryClass.Evidence.ToWireValue(), StringComparison.OrdinalIgnoreCase))
            score += 40;
        else if (string.Equals(document.MemoryClass, Memory.MemoryClass.Trace.ToWireValue(), StringComparison.OrdinalIgnoreCase))
            score -= 400;

        if (string.Equals(document.UpdateSemantics, Memory.MemoryUpdateSemantics.MergeDocument.ToWireValue(), StringComparison.OrdinalIgnoreCase))
            score += 80;
        else if (string.Equals(document.UpdateSemantics, Memory.MemoryUpdateSemantics.AppendDocument.ToWireValue(), StringComparison.OrdinalIgnoreCase))
            score += 60;
        else if (string.Equals(document.UpdateSemantics, Memory.MemoryUpdateSemantics.ConversationTrace.ToWireValue(), StringComparison.OrdinalIgnoreCase))
            score -= 300;

        if (string.Equals(document.UpdateSemantics, Memory.MemoryUpdateSemantics.ImmutableRecord.ToWireValue(), StringComparison.OrdinalIgnoreCase))
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

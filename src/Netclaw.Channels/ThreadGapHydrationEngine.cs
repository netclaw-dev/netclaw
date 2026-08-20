// -----------------------------------------------------------------------
// <copyright file="ThreadGapHydrationEngine.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Threading.Channels;
using Akka.Event;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Telemetry;
using Netclaw.Security;

namespace Netclaw.Channels;

/// <summary>
/// Shared thread-gap hydration for the channel binding actors. The engine
/// fetches thread history, filters it against the session cursor, classifies
/// each candidate for prompt-injection risk, merges the survivors as adopted
/// context, and enqueues the turn. Slack, Discord, and Mattermost supply the
/// transport lookups; the algorithm has one implementation here.
/// </summary>
/// <remarks>
/// The engine holds no Akka state. It does not persist an event and it does not
/// touch the actor context. Each actor-owned effect (cursor read, input-queue
/// lookup, turn-enqueue bookkeeping, hydration re-arm) goes through a callback
/// that the binding actor supplies at construction. All callbacks run on the
/// actor's own message-processing path, so they are safe to read and write
/// actor state.
/// </remarks>
public sealed class ThreadGapHydrationEngine
{
    // Both timeouts match what every binding actor used before the extraction:
    // 30 seconds for the history fetch plus classification, 10 seconds for the
    // input-queue write.
    private static readonly TimeSpan HistoryFetchTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EnqueueTimeout = TimeSpan.FromSeconds(10);

    private readonly SessionId _sessionId;
    private readonly ChannelType _channelType;
    private readonly IThreadHistoryFetcher _historyFetcher;
    private readonly IPromptInjectionDetector _injectionDetector;
    private readonly string _classifierSourceContext;
    private readonly IComparer<string> _cursorComparer;
    private readonly Func<string?, string?> _cursorKeySelector;
    private readonly Func<string, bool> _isAuthorizedSender;
    private readonly ILoggingAdapter _log;
    private readonly Func<string?> _readCursor;
    private readonly Func<ChannelWriter<ChannelInput>?> _readInputQueue;
    private readonly Func<string?> _readIngressClosedReason;
    private readonly Func<Task> _warnBackfillDetectorUnavailableAsync;
    private readonly Action<string?> _onBackfillEnqueued;
    private readonly Action<bool> _setHydrationPending;

    /// <summary>
    /// Creates the engine. Every dependency is required: the injection detector
    /// and the authorization callback are security inputs, so the engine cannot
    /// run with either of them absent.
    /// </summary>
    /// <param name="sessionId">The session this binding serves.</param>
    /// <param name="channelType">Telemetry category for the channel.</param>
    /// <param name="historyFetcher">Transport lookup for thread history.</param>
    /// <param name="injectionDetector">Prompt-injection detector for gap messages.</param>
    /// <param name="classifierSourceContext">Detector source tag, for example <c>slack-backfill</c>.</param>
    /// <param name="cursorComparer">Orders two cursor keys of this channel.</param>
    /// <param name="cursorKeySelector">
    /// Maps a raw message id to its cursor key. A <c>null</c> result means the
    /// message has no usable ordering key, so the engine skips it.
    /// </param>
    /// <param name="isAuthorizedSender">Channel authorization basis for adopted context.</param>
    /// <param name="log">The binding actor's logger, with its adapter context fields.</param>
    /// <param name="readCursor">Reads the recovered session cursor.</param>
    /// <param name="readInputQueue">Reads the session pipeline input queue.</param>
    /// <param name="readIngressClosedReason">Reads the restart-drain reason, or <c>null</c> when ingress is open.</param>
    /// <param name="warnBackfillDetectorUnavailableAsync">Posts the channel's detector-unavailable warning.</param>
    /// <param name="onBackfillEnqueued">
    /// Reports a completed backfill enqueue with the trigger's cursor key. The
    /// actor marks the turn in flight and advances its pending cursor.
    /// </param>
    /// <param name="setHydrationPending">Arms or disarms the deferred-hydration flag.</param>
    public ThreadGapHydrationEngine(
        SessionId sessionId,
        ChannelType channelType,
        IThreadHistoryFetcher historyFetcher,
        IPromptInjectionDetector injectionDetector,
        string classifierSourceContext,
        IComparer<string> cursorComparer,
        Func<string?, string?> cursorKeySelector,
        Func<string, bool> isAuthorizedSender,
        ILoggingAdapter log,
        Func<string?> readCursor,
        Func<ChannelWriter<ChannelInput>?> readInputQueue,
        Func<string?> readIngressClosedReason,
        Func<Task> warnBackfillDetectorUnavailableAsync,
        Action<string?> onBackfillEnqueued,
        Action<bool> setHydrationPending)
    {
        _sessionId = sessionId;
        _channelType = channelType;
        _historyFetcher = historyFetcher;
        _injectionDetector = injectionDetector;
        _classifierSourceContext = classifierSourceContext;
        _cursorComparer = cursorComparer;
        _cursorKeySelector = cursorKeySelector;
        _isAuthorizedSender = isAuthorizedSender;
        _log = log;
        _readCursor = readCursor;
        _readInputQueue = readInputQueue;
        _readIngressClosedReason = readIngressClosedReason;
        _warnBackfillDetectorUnavailableAsync = warnBackfillDetectorUnavailableAsync;
        _onBackfillEnqueued = onBackfillEnqueued;
        _setHydrationPending = setHydrationPending;
    }

    /// <summary>
    /// One-shot thread history hydration. Runs once per actor lifetime, in the
    /// Hydrating behavior immediately after pipeline initialization. Fetches
    /// thread history, computes the gap relative to the recovered cursor, and
    /// if there is an authorized message in the gap, synthesizes one backfill
    /// <see cref="ChannelInput"/> using that message as the trigger and older
    /// gap messages as adopted context. Hands the synthesized input to the
    /// session pipeline through the normal input-queue path.
    /// </summary>
    public async Task PerformOneShotHydrationAsync()
    {
        using var cts = new CancellationTokenSource(HistoryFetchTimeout);

        IReadOnlyList<ChannelInput> history;
        try
        {
            history = await _historyFetcher.FetchThreadHistoryAsync(_sessionId, cts.Token);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Thread history fetch failed for session {SessionId}", _sessionId.Value);
            return;
        }

        var cursor = _readCursor();

        if (history.Count == 0)
        {
            _log.Info(
                "Thread history hydration: empty thread, no backfill cursor={Cursor} session={Session}",
                cursor ?? "none", _sessionId.Value);
            return;
        }

        var candidates = new List<ChannelInput>(history.Count);
        foreach (var item in history)
        {
            if (_cursorKeySelector(item.MessageId) is not { } itemKey)
                continue;

            // Strict: only include messages newer than the cursor. PR #733's
            // "cursor advances only on TurnCompleted" guarantees that
            // itemKey == cursor means the session already has that message
            // persisted — re-including it here on a restart hydration would
            // duplicate it. The in-flight-crash case is handled too: a turn
            // that didn't complete leaves the cursor un-advanced, so the
            // message has itemKey > cursor and is correctly included in the gap.
            if (cursor is { } c && _cursorComparer.Compare(itemKey, c) <= 0)
                continue;

            candidates.Add(item);
        }

        if (candidates.Count == 0)
        {
            _log.Info(
                "Thread history hydration: cursor already at thread head fetched={FetchedCount} cursor={Cursor} session={Session}",
                history.Count, cursor ?? "none", _sessionId.Value);
            return;
        }

        var classified = await ClassifyGapAsync(candidates, cts.Token);
        var gap = classified.Gap;

        _log.Info(
            "Thread history hydration fetched={FetchedCount} gapCount={GapCount} allowed={AllowedCount} blockedHighRisk={BlockedHighRiskCount} cursor={Cursor} session={Session}",
            history.Count, candidates.Count, gap.Count, classified.BlockedForRisk, cursor ?? "none", _sessionId.Value);

        if (classified.DetectorUnavailable)
            await _warnBackfillDetectorUnavailableAsync();

        if (gap.Count == 0)
            return;

        // Locate the most recent authorized message in the gap — it plays the
        // role of the "current authorized message" that adopted-context normally
        // anchors around. Without one we have no authorized trigger to enqueue;
        // the actor transitions to Active and the next live authorized inbound
        // becomes the trigger (the cursor stays put, so staleness still drops
        // anything already seen).
        AdoptedContextMessage? trigger = null;
        for (var i = gap.Count - 1; i >= 0; i--)
        {
            if (gap[i].AuthorityAtInclusion == AdoptedMessageAuthority.Authorized)
            {
                trigger = gap[i];
                break;
            }
        }

        if (trigger is null)
        {
            // Deferred: a non-empty gap with no authorized trigger. The
            // proactive-thread case — the binding actor's lifetime began when
            // the agent posted the thread root, so this hydration ran before
            // any authorized human inbound existed. Re-arm so the first
            // authorized inbound performs this hydration (adopting the gap,
            // e.g. the bot-authored root) instead of taking the fetch-free path.
            _setHydrationPending(true);
            _log.Info(
                "Thread history hydration: no authorized message in gap; re-armed for next authorized inbound session={Session}",
                _sessionId.Value);
            return;
        }

        var adoptedContext = new List<AdoptedContextMessage>();
        foreach (var item in gap)
        {
            if (ReferenceEquals(item, trigger))
                break;
            adoptedContext.Add(item);
        }

        // Build the synthesized inbound. The trigger's ChannelInput already
        // carries its own text and attachment contents (loaded by the history
        // fetcher), so those are the "live" contents for the merge.
        var triggerInput = trigger.Input;
        var triggerKey = _cursorKeySelector(triggerInput.MessageId);
        var backfillInput = MergeAdoptedContext(triggerInput, adoptedContext, cursor);

        if (_readIngressClosedReason() is { } ingressClosedReason)
        {
            _log.Info("Skipping hydration backfill enqueue while restart drain is active: {Reason}", ingressClosedReason);
            return;
        }

        var writer = _readInputQueue();
        if (writer is null)
        {
            _log.Warning("Input queue is not initialized; skipping hydration backfill");
            return;
        }

        try
        {
            using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            writeCts.CancelAfter(EnqueueTimeout);
            await writer.WriteAsync(backfillInput, writeCts.Token);
            // A hydration backfill is an in-flight turn too; the actor mirrors the
            // live-inbound enqueue so a mention arriving before this turn completes
            // does not re-arm.
            _onBackfillEnqueued(triggerKey);

            _log.Info(
                "hydration_backfill_enqueued trigger={TriggerMessageId} adoptedCount={AdoptedCount} session={Session}",
                triggerInput.MessageId, adoptedContext.Count, _sessionId.Value);
            ChannelTelemetry.For(_channelType).RecordMessageEnqueued();
        }
        catch (OperationCanceledException ex)
        {
            _log.Warning(ex, "Timed out enqueueing hydration backfill for session {SessionId}", _sessionId.Value);
        }
        catch (ChannelClosedException ex)
        {
            _log.Warning(ex, "Input queue closed while enqueueing hydration backfill for session {SessionId}", _sessionId.Value);
        }
    }

    /// <summary>
    /// Completes a thread-history hydration that <see cref="PerformOneShotHydrationAsync"/>
    /// deferred for lack of an authorized trigger. The caller's authorized inbound
    /// is the executable trigger; the thread gap strictly before it — most
    /// importantly a proactively-posted bot-authored root — is fetched,
    /// classified, and merged as its adopted-context window. On fetch failure the
    /// turn proceeds un-enriched and hydration stays re-armed, so a later
    /// authorized inbound retries.
    /// </summary>
    /// <param name="baseInput">The live inbound, already built by the actor.</param>
    /// <param name="liveMessageId">The live inbound's raw message id.</param>
    /// <param name="cancellationToken">The actor's inbound-processing token.</param>
    /// <returns>
    /// The input to enqueue: <paramref name="baseInput"/> when there is nothing
    /// to adopt, otherwise the same input with its adopted-context window merged.
    /// </returns>
    public async Task<ChannelInput> ApplyDeferredHydrationAsync(
        ChannelInput baseInput,
        string? liveMessageId,
        CancellationToken cancellationToken)
    {
        // Without the live message's ordering key the gap below it cannot be
        // bounded; leave hydration re-armed and take the fetch-free path.
        if (_cursorKeySelector(liveMessageId) is not { } liveKey)
            return baseInput;

        IReadOnlyList<ChannelInput> history;
        try
        {
            history = await _historyFetcher.FetchThreadHistoryAsync(_sessionId, cancellationToken);
        }
        catch (Exception ex)
        {
            // Non-fatal: execute the turn without an adopted window and keep
            // hydration re-armed so a later authorized inbound retries.
            _log.Warning(ex, "Re-armed thread history fetch failed for session {SessionId}", _sessionId.Value);
            return baseInput;
        }

        // Fetch succeeded: hydration is complete. Only a fetch failure (caught
        // above) keeps the flag armed — classify/merge outcomes never re-arm.
        _setHydrationPending(false);

        var cursor = _readCursor();
        var candidates = new List<ChannelInput>(history.Count);
        foreach (var item in history)
        {
            if (_cursorKeySelector(item.MessageId) is not { } itemKey)
                continue;

            // Strictly above the watermark and strictly below the live inbound:
            // the live inbound is the executable message, not adopted context.
            if (cursor is { } c && _cursorComparer.Compare(itemKey, c) <= 0)
                continue;
            if (_cursorComparer.Compare(itemKey, liveKey) >= 0)
                continue;

            candidates.Add(item);
        }

        if (candidates.Count == 0)
            return baseInput;

        var classified = await ClassifyGapAsync(candidates, cancellationToken);
        if (classified.DetectorUnavailable)
            await _warnBackfillDetectorUnavailableAsync();

        if (classified.Gap.Count == 0)
            return baseInput;

        _log.Info(
            "deferred_hydration_adopted gapCount={GapCount} trigger={TriggerMessageId} session={Session}",
            classified.Gap.Count,
            baseInput.MessageId,
            _sessionId.Value);

        return MergeAdoptedContext(baseInput, classified.Gap, cursor);
    }

    private Task<Classification> ClassifyGapMessageAsync(ChannelInput input, CancellationToken cancellationToken)
    {
        var text = string.Join("\n", input.Contents
            .OfType<TextContent>()
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t)));

        return PromptClassifier.ClassifyAsync(
            _injectionDetector, text, _classifierSourceContext, _log, cancellationToken);
    }

    private readonly record struct GapClassification(
        List<AdoptedContextMessage> Gap,
        int BlockedForRisk,
        bool DetectorUnavailable);

    /// <summary>
    /// Runs prompt-injection classification over candidate gap messages and
    /// captures each surviving message's authority-at-inclusion. Blocked
    /// messages are dropped; detector-unavailable messages are also dropped
    /// and surface a caller-visible flag.
    /// </summary>
    private async Task<GapClassification> ClassifyGapAsync(
        IReadOnlyList<ChannelInput> candidates,
        CancellationToken cancellationToken)
    {
        var classifications = await Task.WhenAll(
            candidates.Select(c => ClassifyGapMessageAsync(c, cancellationToken)));

        var gap = new List<AdoptedContextMessage>(candidates.Count);
        var blockedForRisk = 0;
        var detectorUnavailable = false;
        for (var i = 0; i < candidates.Count; i++)
        {
            switch (classifications[i].Outcome)
            {
                case ClassificationOutcome.Allow:
                    var authority = _isAuthorizedSender(candidates[i].SenderId.Value)
                        ? AdoptedMessageAuthority.Authorized
                        : AdoptedMessageAuthority.Pending;
                    gap.Add(new AdoptedContextMessage(candidates[i], authority));
                    break;

                case ClassificationOutcome.Block:
                    blockedForRisk++;
                    _log.Warning(
                        "Dropped backfill message due to prompt injection risk sender={SenderId} messageId={MessageId} reason={Reason}",
                        candidates[i].SenderId,
                        candidates[i].MessageId ?? "none",
                        classifications[i].Reason ?? "high-risk pattern detected");
                    break;

                case ClassificationOutcome.DetectorUnavailable:
                    blockedForRisk++;
                    detectorUnavailable = true;
                    break;
            }
        }

        return new GapClassification(gap, blockedForRisk, detectorUnavailable);
    }

    /// <summary>
    /// Merges <paramref name="adoptedContext"/> as the adopted-context window
    /// preceding <paramref name="triggerInput"/> (the executable message) and
    /// returns the trigger input with adopted-context metadata populated.
    /// </summary>
    private static ChannelInput MergeAdoptedContext(
        ChannelInput triggerInput,
        List<AdoptedContextMessage> adoptedContext,
        string? cursor)
    {
        var merged = AdoptedContextContentBuilder.MergeWithCurrentMessage(
            adoptedContext,
            triggerInput.Contents,
            triggerInput.SenderId.Value,
            triggerInput.ReceivedAt);

        return triggerInput with
        {
            Contents = merged.Contents,
            HasThirdPartyAdoptedContext = merged.SpeakerIds.Any(
                id => !string.Equals(id, triggerInput.SenderId.Value, StringComparison.Ordinal)),
            AdoptedSpeakerIds = merged.SpeakerIds,
            AdoptedContextProjection = merged.Projection,
            AdoptedContextLowerBound = cursor,
            AdoptedContextUpperBound = triggerInput.MessageId,
            AdoptedContextEntries = merged.Entries
        };
    }
}

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using Akka.Persistence;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Actors.Tools;
using Netclaw.Tools;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Per-session persistent actor managing LLM conversation state.
/// Receives <see cref="SendUserMessage"/>, invokes <see cref="IChatClient"/>,
/// persists <see cref="TurnRecorded"/> events, and sends strongly-typed
/// <see cref="SessionOutput"/> events to subscribers filtered by <see cref="OutputFilter"/>.
///
/// Conversation state is held in an immutable <see cref="SessionState"/> record.
/// The actor owns only transient concerns: subscribers, message buffer, and behavior.
///
/// Uses three command behaviors:
/// - Ready: accepts user messages and fires async LLM call
/// - Processing: buffers incoming messages while LLM call is in flight
/// - Compacting: runs tiered context compaction when usage exceeds threshold
/// </summary>
public sealed class LlmSessionActor : ReceivePersistentActor
{
    private readonly SessionId _sessionId;
    private readonly IChatClient _chatClient;
    private readonly IChatClient _compactionClient;
    private readonly SessionConfig _config;
    private readonly ISystemPromptProvider _promptProvider;
    private readonly IReadOnlyList<IContextLayerProvider> _contextLayers;
    private readonly IToolExecutor? _toolExecutor;
    private readonly IToolAuditLogger? _auditLogger;
    private readonly IMemoryExtractor _memoryExtractor;
    private readonly IChatReducer _chatReducer;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggingAdapter _log;

    // Transient state (not persisted)
    private readonly List<SendUserMessage> _buffer = new();
    private readonly Dictionary<IActorRef, OutputFilter> _subscribers = new();
    private readonly List<AITool> _availableTools = new();
    private readonly ToolRegistry? _fullRegistry;
    private int _baseToolCount; // count of always-loaded tools; dynamic tools appended after this

    // Last observed input token count from LLM response (for compaction trigger)
    private long _lastInputTokenCount;

    // Tool iteration counter (reset per turn, incremented on each ToolExecutionCompleted)
    private int _toolIterationCount;

    // Persistent state (immutable — replaced on each event)
    private SessionState _state = SessionState.Empty;

    // Track whether system prompt was recovered from journal
    private bool _systemPromptRecovered;

    public override string PersistenceId { get; }

    public LlmSessionActor(
        string entityId,
        IChatClientProvider clientProvider,
        SessionConfig config,
        ISystemPromptProvider promptProvider,
        IToolExecutor? toolExecutor = null,
        IToolAuditLogger? auditLogger = null,
        ToolRegistry? toolRegistry = null,
        IMemoryExtractor? memoryExtractor = null,
        IChatReducer? chatReducer = null,
        TimeProvider? timeProvider = null,
        IReadOnlyList<IContextLayerProvider>? contextLayers = null)
    {
        _sessionId = new SessionId(entityId);
        _chatClient = clientProvider.GetClient(ModelRole.Main);
        _compactionClient = config.CompactionModelId is not null
            ? clientProvider.GetClient(ModelRole.Compaction)
            : _chatClient;
        _config = config;
        _promptProvider = promptProvider;
        _contextLayers = contextLayers ?? [];
        _toolExecutor = toolExecutor;
        _auditLogger = auditLogger;
        _memoryExtractor = memoryExtractor ?? NullMemoryExtractor.Instance;
        _chatReducer = chatReducer ?? new ExtractiveSessionReducer(config.KeepRecentMessages);
        _timeProvider = timeProvider ?? TimeProvider.System;
        PersistenceId = $"session-{entityId}";

        // Enrich logger with session context — all log messages automatically include SessionId
        _log = Context.GetLogger().WithContext("SessionId", _sessionId.Value);

        // Load all non-MCP tools for initial LLM calls.
        // MCP tools are loaded dynamically via search_tools meta-tool and reset each turn.
        _fullRegistry = toolRegistry;
        if (toolRegistry is not null)
        {
            _availableTools.AddRange(toolRegistry.GetAlwaysLoadedTools());
        }
        _baseToolCount = _availableTools.Count;

        // ── Recovery handlers ──
        Recover<SystemPromptSet>(evt =>
        {
            _state = _state.Apply(evt);
            _systemPromptRecovered = true;
        });
        Recover<TurnRecorded>(evt => _state = _state.Apply(evt));
        Recover<SessionTitleSet>(evt => _state = _state.Apply(evt));
        Recover<SessionCompacted>(evt => _state = _state.Apply(evt));
        Recover<SnapshotOffer>(offer =>
        {
            if (offer.Snapshot is SessionSnapshot snapshot)
            {
                _state = SessionState.FromSnapshot(snapshot);
                _systemPromptRecovered = _state.History.Count > 0
                    && _state.History[0].Role == Protocol.ChatRole.System;
                _log.Info("Recovered from snapshot (turns={TurnCount})", _state.TurnCount);
            }
        });
        Recover<RecoveryCompleted>(_ =>
        {
            _log.Info("Recovery complete (turns={TurnCount}, history={HistoryCount})",
                _state.TurnCount, _state.History.Count);

            if (!_systemPromptRecovered)
            {
                SetSystemPrompt();
            }

            Become(Ready);
        });
    }

    // ── Command behaviors ──

    private void Ready()
    {
        CommandSubscriptionMessages();
        CommandSnapshotMessages();

        // Passivation: stop after idle timeout, snapshot first for fast recovery
        if (_config.IdleTimeout > TimeSpan.Zero)
        {
            Context.SetReceiveTimeout(_config.IdleTimeout);
        }

        Command<ReceiveTimeout>(_ =>
        {
            _log.Info("Session idle, passivating (timeout={Timeout})", _config.IdleTimeout);
            SaveSnapshot(_state.ToSnapshot());
            Context.Stop(Self);
        });

        Command<SendUserMessage>(cmd =>
        {
            _log.Info("Received user message");

            _toolIterationCount = 0;

            // Reset dynamically-loaded MCP tools — the LLM can re-discover via search_tools
            if (_availableTools.Count > _baseToolCount)
                _availableTools.RemoveRange(_baseToolCount, _availableTools.Count - _baseToolCount);

            // Modality gate: strip unsupported media references
            var mediaRefs = cmd.MediaReferences;
            if (mediaRefs.Count > 0 && !_config.InputModalities.HasFlag(Configuration.ModelModality.Image))
            {
                var imageRefs = mediaRefs.Where(r => r.Modality == (int)MediaModality.Image).ToList();
                if (imageRefs.Count > 0)
                {
                    _log.Info("Stripping {Count} image reference(s) — model does not support vision", imageRefs.Count);
                    mediaRefs = mediaRefs.Where(r => r.Modality != (int)MediaModality.Image).ToList();

                    EmitOutput(new TextOutput
                    {
                        SessionId = _sessionId,
                        Text = "[Images removed — the current model does not support vision input]"
                    }, OutputFilter.Text);
                }
            }

            // If ALL content is images (no text) and model doesn't support vision, skip entirely
            if (string.IsNullOrWhiteSpace(cmd.Content) && mediaRefs.Count == 0
                && cmd.MediaReferences.Count > 0)
            {
                _log.Info("Skipping LLM call — message contained only unsupported media");
                EmitOutput(new TextOutput
                {
                    SessionId = _sessionId,
                    Text = "Your message contained only images, but the current model doesn't support vision. Please send a text message instead."
                }, OutputFilter.Text);
                EmitOutput(new TurnCompleted
                {
                    SessionId = _sessionId,
                    TurnNumber = _state.TurnCount
                });
                TryReplyAck();
                return;
            }

            _state = _state.AddUserMessage(cmd.Content, mediaRefs.Count > 0 ? mediaRefs : null);
            TryReplyAck();
            FireLlmCall();
            Become(Processing);
        });
    }

    private void Processing()
    {
        // Disable idle timeout while processing — re-enabled in Become(Ready)
        Context.SetReceiveTimeout(null);
        CommandSubscriptionMessages();
        CommandSnapshotMessages();

        Command<SendUserMessage>(cmd =>
        {
            _log.Info("Buffering user message (LLM call in progress)");
            _buffer.Add(cmd);
            TryReplyAck();
        });

        Command<LlmResponseReceived>(msg =>
        {
            var response = msg.Response;
            var lastMessage = response.Messages[^1];

            // Check for tool calls
            var toolCalls = lastMessage.Contents.OfType<FunctionCallContent>().ToList();
            if (toolCalls.Count > 0 && _toolExecutor is not null)
            {
                HandleToolCallResponse(lastMessage, toolCalls, response.Usage);
                return;
            }

            // Guard: if the LLM produced no text and no tool calls, it likely tried to
            // call an MCP tool that isn't in ChatOptions.Tools yet. Add a nudge and retry.
            var hasText = lastMessage.Contents.OfType<TextContent>().Any(t => !string.IsNullOrWhiteSpace(t.Text));
            if (!hasText && _toolIterationCount == 0)
            {
                _log.Warning("LLM produced empty response (no text, no tool calls) — retrying with nudge");
                _state = _state.AddSystemNudge(
                    "Your previous response was empty. If you need MCP tools, call search_tools(\"query\") first to load them. "
                    + "MCP tools listed in the index are NOT directly callable — you must use search_tools to load them before calling.");
                FireLlmCall();
                return;
            }

            // Normal text response — persist turn
            HandleTextResponse(lastMessage, response.Usage, msg.StreamedText, msg.StreamedThinking);
        });

        Command<LlmResponseDeltaReceived>(msg =>
        {
            switch (msg.Content)
            {
                case TextContent text when !string.IsNullOrEmpty(text.Text):
                    EmitOutput(new TextDeltaOutput
                    {
                        SessionId = _sessionId,
                        Delta = text.Text
                    }, OutputFilter.Text);
                    break;

                case TextReasoningContent thinking when !string.IsNullOrEmpty(thinking.Text):
                    EmitOutput(new ThinkingDeltaOutput
                    {
                        SessionId = _sessionId,
                        Delta = thinking.Text
                    }, OutputFilter.Thinking);
                    break;
            }
        });

        Command<ToolExecutionCompleted>(msg =>
        {
            // Add tool results to history and log each result
            foreach (var result in msg.ToolResults)
            {
                _state = _state with { History = _state.History.Add(result) };

                var preview = result.Content is { Length: > 200 }
                    ? result.Content[..200] + "..."
                    : result.Content ?? "(null)";
                _log.Info("Tool [{ToolName}] (call={CallId}) result: {Result}",
                    result.Name ?? "unknown", result.ToolCallId ?? "?", preview);
            }

            // Dynamic tool loading: if search_tools was called, load discovered tools
            // into the available tools list so they can be called in subsequent turns
            if (_fullRegistry is not null)
            {
                foreach (var result in msg.ToolResults)
                {
                    if (result.Name is "search_tools" && result.Content is not null)
                    {
                        LoadDiscoveredTools(result.Content);
                    }
                }
            }

            // Emit FileOutput for any file attachments registered by tools
            foreach (var file in msg.FileAttachments)
            {
                EmitOutput(new FileOutput
                {
                    SessionId = _sessionId,
                    TimestampMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                    FilePath = file.FilePath,
                    FileName = file.FileName,
                    MimeType = file.MimeType
                }, OutputFilter.Files);
            }

            _toolIterationCount++;

            // Safety circuit breaker: force text response when tool iteration limit reached
            if (_toolIterationCount >= _config.MaxToolIterationsPerTurn)
            {
                _log.Warning("Tool iteration limit reached ({Count}/{Max}), forcing text response",
                    _toolIterationCount, _config.MaxToolIterationsPerTurn);
                FireLlmCall(forceNoTools: true);
                return;
            }

            // Fire follow-up LLM call with tool results in context
            _log.Info("Tool execution complete ({Iteration}/{Max}), firing follow-up LLM call with {ResultCount} results",
                _toolIterationCount, _config.MaxToolIterationsPerTurn, msg.ToolResults.Count);
            FireLlmCall();
        });

        Command<ToolExecutionFailed>(msg =>
        {
            _log.Error(msg.Cause, "Tool execution failed");

            const string errorMessage = "I encountered an error executing a tool. Please try again.";
            _state = _state.AddErrorReply(errorMessage);

            EmitOutput(new ErrorOutput
            {
                SessionId = _sessionId,
                Message = errorMessage,
                Cause = msg.Cause
            });
            EmitOutput(new TurnCompleted
            {
                SessionId = _sessionId,
                TurnNumber = _state.TurnCount
            });

            _buffer.Clear();
            Become(Ready);
        });

        Command<LlmCallFailed>(msg =>
        {
            _log.Error(msg.Cause, "LLM call failed");

            const string errorMessage = "I encountered an error processing your message. Please try again.";
            _state = _state.AddErrorReply(errorMessage);

            EmitOutput(new ErrorOutput
            {
                SessionId = _sessionId,
                Message = errorMessage,
                Cause = msg.Cause
            });
            EmitOutput(new TurnCompleted
            {
                SessionId = _sessionId,
                TurnNumber = _state.TurnCount
            });

            _buffer.Clear();
            Become(Ready);
        });
    }

    private void Compacting()
    {
        // Disable idle timeout while compacting — re-enabled in Become(Ready)
        Context.SetReceiveTimeout(null);
        CommandSubscriptionMessages();
        CommandSnapshotMessages();

        // Buffer user messages during compaction (same as Processing)
        Command<SendUserMessage>(cmd =>
        {
            _log.Info("Buffering user message (compaction in progress)");
            _buffer.Add(cmd);
            TryReplyAck();
        });

        CommandAsync<CompactionTriggered>(async msg =>
        {
            var messagesBefore = _state.History.Count;

            // Phase 1: Clear old tool results
            var (newState, clearedCount) = _state.ClearOldToolResults(_config.KeepRecentToolResults);
            _state = newState;

            if (clearedCount > 0)
            {
                _log.Info("Phase 1: Cleared {ClearedCount} old tool result(s)", clearedCount);
            }

            // Phase 2: Extractive reduction via IChatReducer (no LLM call for the
            // default ExtractiveSessionReducer, but async reducers are supported).
            // Convert to MEAI ChatMessage without sessionDir (skip media file I/O —
            // the reducer only needs message structure, not media bytes).
            var meaiMessages = ChatMessageConverter.ToAiMessages(_state.History);
            var reduced = await _chatReducer.ReduceAsync(meaiMessages, CancellationToken.None);

            // Map reduced result back to original SerializableChatMessage objects.
            // The extractive reducer preserves order and only trims from the beginning,
            // so we count non-system messages in the result and take that many from the
            // tail of the original history. This avoids lossy ChatMessage→SerializableChatMessage
            // round-trip conversion (which would lose media references).
            var reducedList = reduced as IList<Microsoft.Extensions.AI.ChatMessage>
                ?? reduced.ToList();
            var keptNonSystemCount = reducedList
                .Count(m => m.Role != Microsoft.Extensions.AI.ChatRole.System);

            var systemOffset = _state.History.Count > 0
                && _state.History[0].Role == Protocol.ChatRole.System ? 1 : 0;
            var startIndex = _state.History.Count - keptNonSystemCount;

            var compactedMessages = new List<SerializableChatMessage>();
            for (var i = Math.Max(systemOffset, startIndex); i < _state.History.Count; i++)
            {
                compactedMessages.Add(_state.History[i]);
            }

            _log.Info("Phase 2: Extractive reduction (history={HistoryCount} → {KeptCount} messages)",
                _state.History.Count, compactedMessages.Count + systemOffset);

            var compactedEvent = new SessionCompacted
            {
                SessionId = _sessionId,
                Summary = string.Empty, // Extractive — no LLM summary
                CompactedMessages = compactedMessages,
                TurnCountBefore = _state.TurnCount,
                CompactedAtMs = NowMs()
            };

            Persist(compactedEvent, evt =>
            {
                _state = _state.Apply(evt);
                _lastInputTokenCount = 0; // Reset — next LLM call will provide fresh count

                // Always snapshot after compaction
                SaveSnapshot(_state.ToSnapshot());

                EmitOutput(new CompactionOutput
                {
                    SessionId = _sessionId,
                    MessagesBefore = messagesBefore,
                    MessagesAfter = _state.History.Count,
                    ToolResultsCleared = clearedCount > 0,
                    Summarized = false // Extractive, not summarized
                });

                _log.Info("Compaction complete (before={MessagesBefore}, after={MessagesAfter})",
                    messagesBefore, _state.History.Count);

                // Memory extraction is optional and best-effort.
                // If no extractor is configured, skip the async LLM call and drain immediately.
                if (_memoryExtractor is NullMemoryExtractor)
                {
                    DrainBufferOrReady();
                }
                else
                {
                    InvokeMemoryExtractionAsync();
                }
            });
        });

        Command<MemoryExtractionCompleted>(msg =>
        {
            // Persist extracted memories externally (fire-and-forget)
            var self = Self;
            var extractor = _memoryExtractor;
            var sessionId = _sessionId.Value;
            _ = PersistMemoriesAsync(extractor, sessionId, msg.ExtractedMemories, self);

            DrainBufferOrReady();
        });

        Command<CompactionFailed>(msg =>
        {
            _log.Warning(msg.Cause, "Compaction failed");

            EmitOutput(new ErrorOutput
            {
                SessionId = _sessionId,
                Message = "Context compaction encountered an error. The session will continue.",
                Cause = msg.Cause
            });

            // Compaction is best-effort — drain buffer and continue
            DrainBufferOrReady();
        });
    }

    private void DrainBufferOrReady()
    {
        if (_buffer.Count > 0)
        {
            _log.Info("Post-compaction: draining {BufferCount} buffered message(s)", _buffer.Count);
            foreach (var buffered in _buffer)
            {
                var refs = buffered.MediaReferences.Count > 0 ? buffered.MediaReferences : null;
                _state = _state.AddUserMessage(buffered.Content, refs);
            }
            _buffer.Clear();
            FireLlmCall();
            Become(Processing);
        }
        else
        {
            Become(Ready);
        }
    }

    /// <summary>
    /// Fire an async memory extraction LLM call with a 30-second timeout.
    /// Results come back as <see cref="MemoryExtractionCompleted"/> or
    /// <see cref="CompactionFailed"/> if the call fails or times out.
    /// </summary>
    private void InvokeMemoryExtractionAsync()
    {
        var history = _state.History;
        var self = Self;
        var client = _compactionClient;

        _ = InvokeMemoryExtractionCoreAsync(client, history, self);
    }

    private static async Task InvokeMemoryExtractionCoreAsync(
        IChatClient client,
        IReadOnlyList<SerializableChatMessage> history,
        IActorRef self)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var extractionMessages = new List<AiChatMessage>
            {
                new(Microsoft.Extensions.AI.ChatRole.System,
                    CompactionPromptBuilder.BuildMemoryExtractionSystemPrompt()),
                new(Microsoft.Extensions.AI.ChatRole.User,
                    CompactionPromptBuilder.BuildMemoryExtractionUserPrompt(history))
            };
            var extractionResponse = await client.GetResponseAsync(extractionMessages,
                cancellationToken: cts.Token);
            var extractedText = extractionResponse.Messages[^1].Text ?? string.Empty;
            self.Tell(new MemoryExtractionCompleted { ExtractedMemories = extractedText });
        }
        catch (Exception ex)
        {
            self.Tell(new CompactionFailed { Cause = ex });
        }
    }

    private static async Task PersistMemoriesAsync(
        IMemoryExtractor extractor, string sessionId, string memories, IActorRef self)
    {
        try
        {
            await extractor.PersistAsync(sessionId, memories);
        }
        catch (Exception ex)
        {
            // Memory persistence is best-effort — log and continue compaction
            Trace.TraceWarning("Memory persistence failed for session {0}: {1}", sessionId, ex.Message);
        }
    }

    private void HandleToolCallResponse(
        AiChatMessage lastMessage,
        List<FunctionCallContent> toolCalls,
        UsageDetails? usage)
    {
        // Add assistant message (with tool calls) to history
        var assistantMsg = ChatMessageConverter.FromAiMessage(lastMessage);
        _state = _state with { History = _state.History.Add(assistantMsg) };

        // Emit tool call outputs to subscribers
        foreach (var tc in toolCalls)
        {
            EmitOutput(new ToolCallOutput
            {
                SessionId = _sessionId,
                CallId = tc.CallId,
                ToolName = tc.Name,
                ArgumentsJson = tc.Arguments is not null
                    ? JsonSerializer.Serialize(tc.Arguments)
                    : null
            }, OutputFilter.ToolCalls);
        }

        // Emit usage if present (intermediate turn)
        if (usage is not null)
        {
            EmitUsageOutput(usage);
        }

        // Execute tools async — results come back as ToolExecutionCompleted
        foreach (var tc in toolCalls)
        {
            _log.Info("Invoking tool [{ToolName}] (call={CallId}) args={Args}",
                tc.Name, tc.CallId,
                tc.Arguments is not null ? JsonSerializer.Serialize(tc.Arguments) : "{}");
        }
        var self = Self;
        var executor = _toolExecutor!;
        var sessionId = _sessionId;
        var auditLogger = _auditLogger;
        var tp = _timeProvider;
        _ = ExecuteToolsAsync(executor, toolCalls, sessionId, auditLogger, tp, self);
    }

    private void HandleTextResponse(
        AiChatMessage lastMessage,
        UsageDetails? usage,
        bool streamedText,
        bool streamedThinking)
    {
        _toolIterationCount = 0; // Reset for potential buffer drain (new logical turn)

        var reply = ChatMessageConverter.FromAiMessage(lastMessage);
        var userMsg = _state.FindLastUserMessage();

        // Track input token count for compaction threshold check
        if (usage?.InputTokenCount is > 0)
        {
            _lastInputTokenCount = usage.InputTokenCount.Value;
        }

        var turnEvent = new TurnRecorded
        {
            SessionId = _sessionId,
            UserMessage = userMsg ?? new SerializableChatMessage
            {
                Role = Protocol.ChatRole.User,
                Content = string.Empty
            },
            AssistantReply = reply,
            RecordedAtMs = NowMs()
        };

        Persist(turnEvent, evt =>
        {
            _state = _state with
            {
                History = _state.History.Add(evt.AssistantReply),
                TurnCount = _state.TurnCount + 1
            };

            EmitResponseOutputs(lastMessage, usage, includeText: true, includeThinking: true);
            MaybeSnapshot();

            // Check if compaction should trigger
            if (ShouldCompact())
            {
                _log.Info("Compaction threshold reached ({InputTokens} tokens >= {Threshold} limit), starting compaction",
                    _lastInputTokenCount, _config.CompactionTokenLimit);
                Self.Tell(new CompactionTriggered { InputTokenCount = _lastInputTokenCount });
                Become(Compacting);
                return;
            }

            if (_buffer.Count > 0)
            {
                _log.Info("Draining {BufferCount} buffered message(s)", _buffer.Count);
                foreach (var buffered in _buffer)
                {
                    var refs = buffered.MediaReferences.Count > 0 ? buffered.MediaReferences : null;
                    _state = _state.AddUserMessage(buffered.Content, refs);
                }
                _buffer.Clear();
                FireLlmCall();
            }
            else
            {
                Become(Ready);
            }
        });
    }

    private bool ShouldCompact()
    {
        return _config.CompactionTokenLimit > 0
            && _lastInputTokenCount >= _config.CompactionTokenLimit;
    }

    private void CommandSubscriptionMessages()
    {
        Command<JoinSession>(cmd =>
        {
            _subscribers[cmd.Subscriber] = cmd.Filter;
            Context.WatchWith(cmd.Subscriber,
                new LeaveSession { SessionId = _sessionId, Subscriber = cmd.Subscriber });

            _log.Info("{Subscriber} joined (filter={Filter})", cmd.Subscriber, cmd.Filter);

            var joined = new SessionJoined
            {
                SessionId = _sessionId,
                Title = _state.Title,
                TurnCount = _state.TurnCount
            };

            cmd.Subscriber.Tell(joined);

            // Also reply to Sender so callers can use Ask<SessionJoined> for
            // deterministic confirmation that the join was processed.
            if (!Sender.IsNobody() && !Equals(Sender, Context.System.DeadLetters)
                                   && !Equals(Sender, cmd.Subscriber))
            {
                Sender.Tell(joined);
            }
        });

        Command<LeaveSession>(cmd =>
        {
            if (_subscribers.Remove(cmd.Subscriber))
            {
                _log.Info("{Subscriber} left", cmd.Subscriber);
            }
        });
    }

    private void CommandSnapshotMessages()
    {
        Command<SaveSnapshotSuccess>(msg =>
        {
            _log.Info("Snapshot saved (seqNr={SequenceNr})", msg.Metadata.SequenceNr);
        });

        Command<SaveSnapshotFailure>(msg =>
        {
            _log.Warning("Snapshot failed: {Reason}", msg.Cause.Message);
        });
    }

    protected override void PreRestart(Exception reason, object message)
    {
        foreach (var buffered in _buffer)
        {
            Self.Tell(buffered);
        }
        _buffer.Clear();

        base.PreRestart(reason, message);
    }

    // ── Helpers ──

    private long NowMs() => _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    private void SetSystemPrompt()
    {
        var content = _promptProvider.GetSystemPrompt();
        if (string.IsNullOrWhiteSpace(content))
        {
            _log.Info("No system prompt layers available");
            return;
        }

        var evt = new SystemPromptSet
        {
            SessionId = _sessionId,
            Content = content,
            SetAtMs = NowMs()
        };

        Persist(evt, e =>
        {
            _state = _state.Apply(e);
            _log.Info("System prompt set ({PromptLength} chars)", content.Length);
        });
    }

    private void FireLlmCall(bool forceNoTools = false)
    {
        var sessionDir = SessionDirectoryHelper.GetSessionDirectory(_sessionId);
        var messages = ChatMessageConverter.ToAiMessages(_state.History, sessionDir);

        // Inject dynamic context layers (e.g. tool index) as transient system messages.
        // These are NOT persisted — rebuilt on every call so rehydrated sessions stay fresh.
        InjectDynamicContextLayers(messages);
        var self = Self;
        var client = _chatClient;

        ChatOptions? options = null;
        if (!forceNoTools && _availableTools.Count > 0)
        {
            options = new ChatOptions
            {
                Tools = _availableTools.ToList()
            };
        }

        _ = InvokeLlmAsync(client, messages, options, self);
    }

    /// <summary>
    /// Inject dynamic context layers as system messages after the persisted system prompt
    /// but before user messages. This keeps context layers transient — they are regenerated
    /// on every LLM call rather than being part of the persisted journal.
    /// </summary>
    private void InjectDynamicContextLayers(List<AiChatMessage> messages)
    {
        if (_contextLayers.Count == 0) return;

        var parts = new List<string>();
        foreach (var layer in _contextLayers)
        {
            var content = layer.GetContextLayer();
            if (!string.IsNullOrWhiteSpace(content))
                parts.Add(content.Trim());
        }

        if (parts.Count == 0) return;

        var contextMessage = new AiChatMessage(
            Microsoft.Extensions.AI.ChatRole.System,
            string.Join("\n\n", parts));

        // Insert after the last system message (the persisted prompt), before user messages
        var insertIndex = 0;
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Role == Microsoft.Extensions.AI.ChatRole.System)
                insertIndex = i + 1;
            else
                break;
        }

        messages.Insert(insertIndex, contextMessage);
    }

    private static async Task InvokeLlmAsync(
        IChatClient client, List<AiChatMessage> messages, ChatOptions? options, IActorRef self)
    {
        try
        {
            var response = await InvokeStreamingResponseAsync(client, messages, options, self);
            self.Tell(response);
        }
        catch (Exception ex)
        {
            self.Tell(new LlmCallFailed { Cause = ex });
        }
    }

    private static async Task<LlmResponseReceived> InvokeStreamingResponseAsync(
        IChatClient client,
        List<AiChatMessage> messages,
        ChatOptions? options,
        IActorRef self)
    {
        var contents = new List<AIContent>();
        var updates = new List<ChatResponseUpdate>();
        var textBuilder = new StringBuilder();
        var thinkingBuilder = new StringBuilder();
        string? pendingTextDelta = null;
        string? pendingThinkingDelta = null;
        var textDeltaCount = 0;
        var thinkingDeltaCount = 0;

        await foreach (var update in client.GetStreamingResponseAsync(messages, options))
        {
            updates.Add(update);

            if (update.Contents is not null)
            {
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent text when !string.IsNullOrEmpty(text.Text):
                            textBuilder.Append(text.Text);
                            textDeltaCount++;
                            if (textDeltaCount == 1)
                            {
                                pendingTextDelta = text.Text;
                            }
                            else
                            {
                                if (textDeltaCount == 2 && !string.IsNullOrEmpty(pendingTextDelta))
                                {
                                    self.Tell(new LlmResponseDeltaReceived
                                    {
                                        Content = new TextContent(pendingTextDelta)
                                    });
                                }

                                self.Tell(new LlmResponseDeltaReceived { Content = content });
                            }
                            break;

                        case TextReasoningContent thinking when !string.IsNullOrEmpty(thinking.Text):
                            thinkingBuilder.Append(thinking.Text);
                            thinkingDeltaCount++;
                            if (thinkingDeltaCount == 1)
                            {
                                pendingThinkingDelta = thinking.Text;
                            }
                            else
                            {
                                if (thinkingDeltaCount == 2 && !string.IsNullOrEmpty(pendingThinkingDelta))
                                {
                                    self.Tell(new LlmResponseDeltaReceived
                                    {
                                        Content = new TextReasoningContent(pendingThinkingDelta)
                                    });
                                }

                                self.Tell(new LlmResponseDeltaReceived { Content = content });
                            }
                            break;

                        case FunctionCallContent:
                            contents.Add(content);
                            break;
                    }
                }
            }

        }

        if (thinkingBuilder.Length > 0)
            contents.Add(new TextReasoningContent(thinkingBuilder.ToString()));

        if (textBuilder.Length > 0)
            contents.Add(new TextContent(textBuilder.ToString()));

        var response = updates.Count > 0
            ? updates.ToChatResponse()
            : new ChatResponse(new AiChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, contents));

        if (response.Messages.Count == 0)
            response.Messages.Add(new AiChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, contents));

        return new LlmResponseReceived
        {
            Response = response,
            StreamedText = textDeltaCount > 1,
            StreamedThinking = thinkingDeltaCount > 1
        };
    }

    private sealed record ToolCallResult(
        SerializableChatMessage Message,
        IReadOnlyList<FileAttachmentInfo> FileAttachments);

    private static async Task ExecuteToolsAsync(
        IToolExecutor executor,
        List<FunctionCallContent> toolCalls,
        SessionId sessionId,
        IToolAuditLogger? auditLogger,
        TimeProvider timeProvider,
        IActorRef self)
    {
        try
        {
            // Execute all tool calls in parallel — each is independent
            var tasks = toolCalls.Select(tc => ExecuteSingleToolAsync(executor, tc, sessionId, auditLogger, timeProvider));
            var results = await Task.WhenAll(tasks);

            var fileAttachments = results.SelectMany(r => r.FileAttachments).ToList();
            self.Tell(new ToolExecutionCompleted
            {
                ToolResults = results.Select(r => r.Message).ToList(),
                FileAttachments = fileAttachments
            });
        }
        catch (Exception ex)
        {
            self.Tell(new ToolExecutionFailed { Cause = ex });
        }
    }

    private static async Task<ToolCallResult> ExecuteSingleToolAsync(
        IToolExecutor executor,
        FunctionCallContent tc,
        SessionId sessionId,
        IToolAuditLogger? auditLogger,
        TimeProvider timeProvider)
    {
        var sw = Stopwatch.StartNew();
        string resultText;
        var context = CreateExecutionContext(sessionId);
        try
        {
            resultText = await executor.ExecuteAsync(tc, context);
            sw.Stop();

            auditLogger?.Log(new ToolAuditEntry
            {
                SessionId = sessionId.Value,
                ToolName = tc.Name,
                CallId = tc.CallId,
                Timestamp = timeProvider.GetUtcNow(),
                Allowed = true,
                Duration = sw.Elapsed
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            resultText = $"Error executing tool: {ex.Message}";

            auditLogger?.Log(new ToolAuditEntry
            {
                SessionId = sessionId.Value,
                ToolName = tc.Name,
                CallId = tc.CallId,
                Timestamp = timeProvider.GetUtcNow(),
                Allowed = true,
                Duration = sw.Elapsed
            });
        }

        var message = new SerializableChatMessage
        {
            Role = Protocol.ChatRole.Tool,
            Content = resultText,
            ToolCallId = tc.CallId,
            Name = tc.Name
        };

        return new ToolCallResult(message, context.FileAttachments);
    }

    /// <summary>
    /// Parse tool names from search_tools output and add their AITool definitions
    /// to <see cref="_availableTools"/> so the LLM can call them in subsequent iterations.
    /// </summary>
    private void LoadDiscoveredTools(string searchToolOutput)
    {
        if (_fullRegistry is null) return;

        // Parse tool names from the search output format: "  server/toolname — description"
        foreach (var line in searchToolOutput.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.Contains(" — "))
                continue;

            var toolName = trimmed.Split(" — ")[0].Trim();
            if (string.IsNullOrEmpty(toolName))
                continue;

            var tool = _fullRegistry.GetByName(toolName);
            if (tool is null)
                continue;

            // Don't add duplicates
            var aiTool = tool.ToAITool();
            if (_availableTools.Any(existing =>
                existing is AIFunction ef && aiTool is AIFunction nf && ef.Name == nf.Name))
                continue;

            _availableTools.Add(aiTool);
            _log.Info("Dynamically loaded tool '{ToolName}' into session", toolName);
        }
    }

    private void MaybeSnapshot()
    {
        if (_config.SnapshotInterval > 0 && LastSequenceNr % _config.SnapshotInterval == 0)
        {
            SaveSnapshot(_state.ToSnapshot());
        }
    }

    private void EmitResponseOutputs(
        AiChatMessage message,
        UsageDetails? usage,
        bool includeText = true,
        bool includeThinking = true)
    {
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent text when includeText:
                    EmitOutput(new TextOutput
                    {
                        SessionId = _sessionId,
                        Text = text.Text ?? string.Empty
                    }, OutputFilter.Text);
                    break;

                case TextReasoningContent thinking when includeThinking:
                    EmitOutput(new ThinkingOutput
                    {
                        SessionId = _sessionId,
                        Text = thinking.Text ?? string.Empty
                    }, OutputFilter.Thinking);
                    break;

                case FunctionCallContent toolCall:
                    EmitOutput(new ToolCallOutput
                    {
                        SessionId = _sessionId,
                        CallId = toolCall.CallId,
                        ToolName = toolCall.Name,
                        ArgumentsJson = toolCall.Arguments is not null
                            ? JsonSerializer.Serialize(toolCall.Arguments)
                            : null
                    }, OutputFilter.ToolCalls);
                    break;
            }
        }

        if (usage is not null)
        {
            EmitUsageOutput(usage);
        }

        EmitOutput(new TurnCompleted
        {
            SessionId = _sessionId,
            TurnNumber = _state.TurnCount
        });
    }

    private void EmitUsageOutput(UsageDetails usage)
    {
        var contextWindow = _config.ContextWindowTokens;
        double? usagePercent = usage.InputTokenCount.HasValue && contextWindow > 0
            ? (double)usage.InputTokenCount.Value / contextWindow
            : null;

        EmitOutput(new UsageOutput
        {
            SessionId = _sessionId,
            InputTokens = usage.InputTokenCount,
            OutputTokens = usage.OutputTokenCount,
            TotalTokens = usage.TotalTokenCount,
            CachedInputTokens = usage.CachedInputTokenCount,
            ReasoningTokens = usage.ReasoningTokenCount,
            ContextWindowTokens = contextWindow,
            UsagePercent = usagePercent
        }, OutputFilter.Usage);
    }

    private void EmitOutput(SessionOutput output, OutputFilter requiredFlag = OutputFilter.None)
    {
        foreach (var (subscriber, filter) in _subscribers)
        {
            if (requiredFlag == OutputFilter.None || filter.HasFlag(requiredFlag))
            {
                subscriber.Tell(output);
            }
        }
    }

    private void TryReplyAck()
    {
        if (Sender.IsNobody() || Equals(Sender, Context.System.DeadLetters))
            return;

        Sender.Tell(CommandAck.For(_sessionId));
    }

    private static Netclaw.Tools.ToolExecutionContext CreateExecutionContext(SessionId sessionId)
    {
        var sessionDir = SessionDirectoryHelper.GetSessionDirectory(sessionId);
        return new Netclaw.Tools.ToolExecutionContext(sessionId.Value, sessionDir);
    }

    internal void SetTitle(string title)
    {
        var evt = new SessionTitleSet
        {
            SessionId = _sessionId,
            Title = title,
            SetAtMs = NowMs()
        };

        Persist(evt, e =>
        {
            _state = _state.Apply(e);
            EmitOutput(new SessionTitleOutput
            {
                SessionId = _sessionId,
                Title = title
            });
        });
    }
}

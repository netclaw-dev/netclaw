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
    private readonly IToolExecutor? _toolExecutor;
    private readonly IToolAuditLogger? _auditLogger;
    private readonly IMemoryExtractor _memoryExtractor;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggingAdapter _log;

    // Transient state (not persisted)
    private readonly List<SendUserMessage> _buffer = new();
    private readonly Dictionary<IActorRef, OutputFilter> _subscribers = new();
    private IReadOnlyList<AITool> _availableTools = [];

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
        TimeProvider? timeProvider = null)
    {
        _sessionId = new SessionId(entityId);
        _chatClient = clientProvider.GetClient(ModelRole.Main);
        _compactionClient = config.CompactionModelId is not null
            ? clientProvider.GetClient(ModelRole.Compaction)
            : _chatClient;
        _config = config;
        _promptProvider = promptProvider;
        _toolExecutor = toolExecutor;
        _auditLogger = auditLogger;
        _memoryExtractor = memoryExtractor ?? NullMemoryExtractor.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        PersistenceId = $"session-{entityId}";

        // Enrich logger with session context — all log messages automatically include SessionId
        _log = Context.GetLogger().WithContext("SessionId", _sessionId.Value);

        // Load available tools from registry (all tools for now; policy filtering comes in Task 1.5)
        if (toolRegistry is not null)
        {
            _availableTools = toolRegistry.GetAllTools();
        }

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

        Command<SendUserMessage>(cmd =>
        {
            _log.Info("Received user message");

            _toolIterationCount = 0;
            _state = _state.AddUserMessage(cmd.Content);
            TryReplyAck();
            FireLlmCall();
            Become(Processing);
        });
    }

    private void Processing()
    {
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
            // Add tool results to history
            foreach (var result in msg.ToolResults)
            {
                _state = _state with { History = _state.History.Add(result) };
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
        CommandSubscriptionMessages();
        CommandSnapshotMessages();

        // Buffer user messages during compaction (same as Processing)
        Command<SendUserMessage>(cmd =>
        {
            _log.Info("Buffering user message (compaction in progress)");
            _buffer.Add(cmd);
            TryReplyAck();
        });

        Command<CompactionTriggered>(msg =>
        {
            var messagesBefore = _state.History.Count;

            // Phase 1: Clear old tool results
            var (newState, clearedCount) = _state.ClearOldToolResults(_config.KeepRecentToolResults);
            _state = newState;

            if (clearedCount > 0)
            {
                _log.Info("Phase 1: Cleared {ClearedCount} old tool result(s)", clearedCount);
            }

            // Phase 2: Fire memory extraction + summarization LLM calls
            _log.Info("Phase 2: Starting summarization (history={HistoryCount} messages)", _state.History.Count);
            FireMemoryExtractionCall(messagesBefore, clearedCount > 0);
        });

        Command<MemoryExtractionCompleted>(msg =>
        {
            // Persist extracted memories externally
            var self = Self;
            var extractor = _memoryExtractor;
            var sessionId = _sessionId.Value;
            _ = PersistMemoriesAsync(extractor, sessionId, msg.ExtractedMemories, self);
        });

        Command<SummarizationCompleted>(msg =>
        {
            var messagesBefore = _state.History.Count;

            // Build compacted history: system prompt + summary as assistant message
            var compactedMessages = new List<SerializableChatMessage>
            {
                new()
                {
                    Role = Protocol.ChatRole.Assistant,
                    Content = msg.Summary
                }
            };

            var compactedEvent = new SessionCompacted
            {
                SessionId = _sessionId,
                Summary = msg.Summary,
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
                    ToolResultsCleared = true,
                    Summarized = true
                });

                _log.Info("Compaction complete (before={MessagesBefore}, after={MessagesAfter})",
                    messagesBefore, _state.History.Count);

                DrainBufferOrReady();
            });
        });

        Command<CompactionFailed>(msg =>
        {
            _log.Warning(msg.Cause, "Compaction failed, continuing without compaction");

            // Compaction is best-effort — if it fails, drain buffer and continue
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
                _state = _state.AddUserMessage(buffered.Content);
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

    private void FireMemoryExtractionCall(int messagesBefore, bool toolResultsCleared)
    {
        var history = _state.History;
        var self = Self;
        var client = _compactionClient;

        _ = InvokeCompactionSequenceAsync(client, history, self, messagesBefore, toolResultsCleared);
    }

    private static async Task InvokeCompactionSequenceAsync(
        IChatClient client,
        IReadOnlyList<SerializableChatMessage> history,
        IActorRef self,
        int messagesBefore,
        bool toolResultsCleared)
    {
        try
        {
            // Step 1: Memory extraction (optional — if it fails, we still summarize)
            try
            {
                var extractionMessages = new List<AiChatMessage>
                {
                    new(Microsoft.Extensions.AI.ChatRole.System,
                        CompactionPromptBuilder.BuildMemoryExtractionSystemPrompt()),
                    new(Microsoft.Extensions.AI.ChatRole.User,
                        CompactionPromptBuilder.BuildMemoryExtractionUserPrompt(history))
                };
                var extractionResponse = await client.GetResponseAsync(extractionMessages);
                var extractedText = extractionResponse.Messages[^1].Text ?? string.Empty;
                self.Tell(new MemoryExtractionCompleted { ExtractedMemories = extractedText });
            }
            catch (Exception ex)
            {
                // Memory extraction is best-effort — log and continue to summarization
                Trace.TraceWarning("Memory extraction failed during compaction: {0}", ex.Message);
            }

            // Step 2: Summarization
            var summaryMessages = new List<AiChatMessage>
            {
                new(Microsoft.Extensions.AI.ChatRole.System,
                    CompactionPromptBuilder.BuildSummarizationSystemPrompt()),
                new(Microsoft.Extensions.AI.ChatRole.User,
                    CompactionPromptBuilder.BuildSummarizationUserPrompt(history))
            };
            var summaryResponse = await client.GetResponseAsync(summaryMessages);
            var summaryText = summaryResponse.Messages[^1].Text ?? string.Empty;

            self.Tell(new SummarizationCompleted { Summary = summaryText });
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
        _log.Info("Executing {ToolCount} tool call(s)", toolCalls.Count);
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
                    _state = _state.AddUserMessage(buffered.Content);
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

            cmd.Subscriber.Tell(new SessionJoined
            {
                SessionId = _sessionId,
                Title = _state.Title,
                TurnCount = _state.TurnCount
            });
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
        var messages = ChatMessageConverter.ToAiMessages(_state.History);
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

            self.Tell(new ToolExecutionCompleted { ToolResults = results.ToList() });
        }
        catch (Exception ex)
        {
            self.Tell(new ToolExecutionFailed { Cause = ex });
        }
    }

    private static async Task<SerializableChatMessage> ExecuteSingleToolAsync(
        IToolExecutor executor,
        FunctionCallContent tc,
        SessionId sessionId,
        IToolAuditLogger? auditLogger,
        TimeProvider timeProvider)
    {
        var sw = Stopwatch.StartNew();
        string resultText;
        try
        {
            resultText = await executor.ExecuteAsync(tc);
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

        return new SerializableChatMessage
        {
            Role = Protocol.ChatRole.Tool,
            Content = resultText,
            ToolCallId = tc.CallId,
            Name = tc.Name
        };
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

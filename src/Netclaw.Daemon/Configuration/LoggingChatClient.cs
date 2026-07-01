// -----------------------------------------------------------------------
// <copyright file="LoggingChatClient.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Sessions;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Decorates an <see cref="IChatClient"/> with logging for elapsed time, token usage, and
/// errors. The decorator is session-agnostic but tags each line with the owning
/// <c>SessionId</c> via a logging scope — read from <see cref="SessionScopedChatOptions"/> on
/// the call (<see cref="ChatClientSessionScope"/>). The file-logger partitions scoped lines into
/// that session's <c>session.log</c> (and they carry <c>SessionId</c> as a Seq/OTLP field); a
/// call with no session scope falls through to <c>daemon.log</c>. Stateless and safe to share
/// across sessions. Netclaw issues only streaming requests, so only the streaming path is
/// instrumented; the inherited non-streaming pass-through is unused.
/// </summary>
public sealed class LoggingChatClient : DelegatingChatClient
{
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    public LoggingChatClient(
        IChatClient innerClient,
        ILogger logger,
        TimeProvider? timeProvider = null)
        : base(innerClient)
    {
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        // Tag every line this call emits (prompt summary, timing, token usage, errors)
        // with the owning session so they correlate in Seq. No-op for sidecar calls.
        using var sessionScope = ChatClientSessionScope.Begin(_logger, options);
        var messageList = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        LogPromptDiagnostics(messageList, options);

        var start = _timeProvider.GetTimestamp();
        long inputTokens = 0;
        long outputTokens = 0;
        int textDeltaCount = 0;
        int textDeltaChars = 0;
        int thinkingDeltaCount = 0;
        int thinkingDeltaChars = 0;
        int toolCallDeltaCount = 0;
        ChatFinishReason? lastFinishReason = null;

        IAsyncEnumerable<ChatResponseUpdate> stream;
        try
        {
            stream = base.GetStreamingResponseAsync(messageList, options, cancellationToken);
        }
        catch (Exception ex)
        {
            var elapsed = _timeProvider.GetElapsedTime(start);
            _logger.LogError(ex, "LLM streaming call failed after {ElapsedMs:F0}ms", elapsed.TotalMilliseconds);
            throw;
        }

        await foreach (var update in stream)
        {
            foreach (var item in update.Contents)
            {
                switch (item)
                {
                    case TextContent tc:
                        textDeltaCount++;
                        textDeltaChars += tc.Text?.Length ?? 0;
                        break;
                    case TextReasoningContent rc:
                        thinkingDeltaCount++;
                        thinkingDeltaChars += rc.Text?.Length ?? 0;
                        break;
                    case FunctionCallContent:
                        toolCallDeltaCount++;
                        break;
                    case UsageContent usage:
                        inputTokens += usage.Details?.InputTokenCount ?? 0;
                        outputTokens += usage.Details?.OutputTokenCount ?? 0;
                        break;
                }
            }

            if (update.FinishReason is not null)
                lastFinishReason = update.FinishReason;

            yield return update;
        }

        var totalElapsed = _timeProvider.GetElapsedTime(start);

        _logger.LogDebug(
            "LLM streaming response content breakdown: textDeltas={TextDeltaCount} textChars={TextChars} thinkingDeltas={ThinkingDeltaCount} thinkingChars={ThinkingChars} toolCallDeltas={ToolCallDeltaCount} finishReason={FinishReason}",
            textDeltaCount, textDeltaChars, thinkingDeltaCount, thinkingDeltaChars, toolCallDeltaCount,
            lastFinishReason?.ToString() ?? "null");

        if (inputTokens > 0 || outputTokens > 0)
        {
            _logger.LogInformation(
                "LLM streaming call completed in {ElapsedMs:F0}ms (input: {InputTokens}, output: {OutputTokens})",
                totalElapsed.TotalMilliseconds, inputTokens, outputTokens);
        }
        else
        {
            _logger.LogInformation(
                "LLM streaming call completed in {ElapsedMs:F0}ms",
                totalElapsed.TotalMilliseconds);
        }
    }

    private void LogPromptDiagnostics(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        if (!_logger.IsEnabled(LogLevel.Debug) && !_logger.IsEnabled(LogLevel.Trace))
            return;

        var summary = BuildPromptSummary(messages, options);
        _logger.LogDebug(
            "LLM prompt summary: messages={MessageCount} system={SystemCount} user={UserCount} assistant={AssistantCount} tool={ToolCount} totalChars={TotalChars} toolOptions={ToolOptions} browserToolOptions={BrowserToolOptions} promptSha256={PromptHash}",
            summary.MessageCount,
            summary.SystemCount,
            summary.UserCount,
            summary.AssistantCount,
            summary.ToolCount,
            summary.TotalChars,
            summary.ToolOptionCount,
            summary.BrowserToolOptionCount,
            summary.PromptHash);

        if (!_logger.IsEnabled(LogLevel.Trace))
            return;

        _logger.LogTrace("LLM prompt dump:\n{PromptDump}", BuildPromptDump(messages, options));
    }

    private static PromptSummary BuildPromptSummary(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        var systemCount = 0;
        var userCount = 0;
        var assistantCount = 0;
        var toolCount = 0;
        var totalChars = 0;

        var fingerprintBuilder = new StringBuilder();

        foreach (var message in messages)
        {
            totalChars += EstimateMessageChars(message);

            if (message.Role == ChatRole.System) systemCount++;
            else if (message.Role == ChatRole.User) userCount++;
            else if (message.Role == ChatRole.Assistant) assistantCount++;
            else if (message.Role == ChatRole.Tool) toolCount++;

            fingerprintBuilder.Append("role=")
                .Append(message.Role)
                .Append('|');

            foreach (var content in message.Contents)
            {
                fingerprintBuilder.Append(content switch
                {
                    TextContent t => t.Text,
                    FunctionCallContent fc => $"{fc.Name}:{fc.Arguments}",
                    FunctionResultContent fr => fr.Result?.ToString(),
                    _ => content.ToString()
                });
                fingerprintBuilder.Append(';');
            }

            fingerprintBuilder.AppendLine();
        }

        var toolNames = options?.Tools?
            .Select(t => t is AIFunction f ? f.Name : t.GetType().Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList() ?? [];

        var browserToolCount = toolNames.Count(name =>
            name!.Contains("browser_", StringComparison.OrdinalIgnoreCase)
            || name.Contains("/browser_", StringComparison.OrdinalIgnoreCase));

        var hash = ComputeHashPrefix(fingerprintBuilder.ToString());

        return new PromptSummary(
            messages.Count,
            systemCount,
            userCount,
            assistantCount,
            toolCount,
            totalChars,
            toolNames.Count,
            browserToolCount,
            hash);
    }

    private static int EstimateMessageChars(ChatMessage message)
    {
        var chars = 0;
        foreach (var content in message.Contents)
        {
            chars += content switch
            {
                TextContent t => t.Text?.Length ?? 0,
                FunctionCallContent fc => (fc.Name?.Length ?? 0) + (fc.Arguments?.ToString()?.Length ?? 0),
                FunctionResultContent fr => fr.Result?.ToString()?.Length ?? 0,
                DataContent dc => dc.Data.Length,
                _ => content.ToString()?.Length ?? 0
            };
        }

        return chars;
    }

    private static string BuildPromptDump(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            sb.AppendLine($"[{i}] role={message.Role}");
            foreach (var content in message.Contents)
            {
                sb.Append("  - ");
                sb.AppendLine(content switch
                {
                    TextContent t => t.Text,
                    FunctionCallContent fc => $"FunctionCall {fc.Name} args={fc.Arguments}",
                    FunctionResultContent fr => $"FunctionResult {fr.CallId} result={fr.Result}",
                    DataContent dc => $"DataContent mediaType={dc.MediaType} bytes={dc.Data.Length}",
                    _ => content.ToString()
                });
            }
        }

        var toolNames = options?.Tools?
            .Select(t => t is AIFunction f ? f.Name : t.GetType().Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();

        if (toolNames is { Count: > 0 })
        {
            sb.AppendLine("Tools:");
            foreach (var name in toolNames)
                sb.AppendLine($"  - {name}");
        }

        return sb.ToString();
    }

    private static string ComputeHashPrefix(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes)[..12];
    }

    private sealed record PromptSummary(
        int MessageCount,
        int SystemCount,
        int UserCount,
        int AssistantCount,
        int ToolCount,
        int TotalChars,
        int ToolOptionCount,
        int BrowserToolOptionCount,
        string PromptHash);

}

// -----------------------------------------------------------------------
// <copyright file="OpenAiCompatibleChatClientTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Netclaw.Providers.SelfHosted;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class OpenAiCompatibleChatClientTests
{
    [Fact]
    public async Task UsesOfficialApiV1Paths_ForBareEndpoint()
    {
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":\"1\",\"model\":\"test\",\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"hi\"}}]}", Encoding.UTF8, "application/json")
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl("http://localhost:8000");
        var client = new OpenAiCompatibleChatClient(httpClient, endpoint, "test-model");

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("/v1/chat/completions", handler.Requests.Single().RequestUri!.AbsolutePath);
        Assert.Equal("hi", response.Text);
    }

    [Fact]
    public async Task StreamsReasoningAndTextDeltas_FromOfficialSpectrum()
    {
        const string sse = """
data: {"id":"abc","model":"Qwen","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"role":"assistant","content":null}}]}

data: {"id":"abc","model":"Qwen","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"reasoning_content":"Thinking"}}]}

data: {"id":"abc","model":"Qwen","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":"Hello"}}]}

data: {"id":"abc","model":"Qwen","object":"chat.completion.chunk","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

data: [DONE]

""";

        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl("http://localhost:8000/api/v1");
        var client = new OpenAiCompatibleChatClient(httpClient, endpoint, "test-model");

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")], cancellationToken: TestContext.Current.CancellationToken))
            updates.Add(update);

        Assert.Equal(4, updates.Count);
        Assert.Contains(updates, u => u.Contents.Count == 0 && u.FinishReason is null); // keepalive from content-less initial chunk
        Assert.Contains(updates, u => u.Contents.OfType<TextReasoningContent>().Any(c => c.Text == "Thinking"));
        Assert.Contains(updates, u => u.Contents.OfType<TextContent>().Any(c => c.Text == "Hello"));
        Assert.Contains(updates, u => u.FinishReason == ChatFinishReason.Stop);
    }

    [Fact]
    public async Task SerializesTools_InOpenAiFunctionFormat()
    {
        string? body = null;
        using var handler = new RecordingHandler(req =>
        {
            body = req.Content is null ? null : req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"1\",\"model\":\"test\",\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"hi\"}}]}", Encoding.UTF8, "application/json")
            };
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl("http://localhost:8000");
        var client = new OpenAiCompatibleChatClient(httpClient, endpoint, "test-model");

        var tool = AIFunctionFactory.CreateDeclaration(
            "search_tools",
            "Search tools",
            System.Text.Json.JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}},\"required\":[\"query\"]}").RootElement);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions { Tools = [tool] }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(body);
        Assert.Contains("\"tools\":[{\"type\":\"function\"", body, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"search_tools\"", body, StringComparison.Ordinal);
        Assert.Contains("\"required\":[\"query\"]", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoesNotLeakSessionId_FromSessionScopedChatOptions_ToWire()
    {
        // SessionScopedChatOptions carries the session id as a CLR property, NOT in
        // AdditionalProperties — which this client forwards verbatim as top-level JSON.
        // The session id must never appear in the outbound request body.
        string? body = null;
        using var handler = new RecordingHandler(req =>
        {
            body = req.Content is null ? null : req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"1\",\"model\":\"test\",\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"hi\"}}]}", Encoding.UTF8, "application/json")
            };
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl("http://localhost:8000");
        var client = new OpenAiCompatibleChatClient(httpClient, endpoint, "test-model");

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            new Netclaw.Actors.Sessions.SessionScopedChatOptions { SessionId = "C123/167.42" },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(body);
        Assert.DoesNotContain("SessionId", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C123/167.42", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CollapsesMultipleSystemMessages_IntoSingleLeadingSystemMessage()
    {
        string? body = null;
        using var handler = new RecordingHandler(req =>
        {
            body = req.Content is null ? null : req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"1\",\"model\":\"test\",\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"hi\"}}]}", Encoding.UTF8, "application/json")
            };
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl("http://localhost:8000");
        var client = new OpenAiCompatibleChatClient(httpClient, endpoint, "test-model");

        await client.GetResponseAsync(
        [
            new ChatMessage(ChatRole.System, "first system"),
            new ChatMessage(ChatRole.System, "second system"),
            new ChatMessage(ChatRole.User, "hello")
        ], cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(body);
        Assert.Contains("\"messages\":[{\"role\":\"system\",\"content\":\"first system\\n\\nsecond system\"},{\"role\":\"user\",\"content\":\"hello\"}]", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuffersFragmentedToolCallArguments_UntilFinishReason()
    {
        const string sse = """
data: {"id":"abc","model":"Qwen","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"role":"assistant","content":"I'll check. "}}]}

data: {"id":"abc","model":"Qwen","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"search_tools","arguments":"{\"Query\":\"what "}}]}}]}

data: {"id":"abc","model":"Qwen","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"type":"function","function":{"arguments":"is TextForge\"}"}}]}}]}

data: {"id":"abc","model":"Qwen","object":"chat.completion.chunk","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

data: [DONE]

""";

        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl("http://localhost:8000/api/v1");
        var client = new OpenAiCompatibleChatClient(httpClient, endpoint, "test-model");

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")], cancellationToken: TestContext.Current.CancellationToken))
            updates.Add(update);

        Assert.Contains(updates, u => u.Contents.OfType<TextContent>().Any(c => c.Text == "I'll check. "));

        var toolUpdate = Assert.Single(updates, u => u.FinishReason == ChatFinishReason.ToolCalls);
        var toolCall = Assert.Single(toolUpdate.Contents.OfType<FunctionCallContent>());
        Assert.Equal("call_1", toolCall.CallId);
        Assert.Equal("search_tools", toolCall.Name);
        Assert.Equal("what is TextForge", toolCall.Arguments!["Query"]?.ToString());
    }

    [Fact]
    public async Task MalformedToolCallArguments_CarrySentinel_InsteadOfNullArgs()
    {
        // Truncated mid-stream arguments JSON must not dispatch a null-args
        // call (silent intent discard) — the parse failure travels with the
        // call via the sentinel and the pipeline rejects it pre-dispatch
        // (tool-arg-validation spec).
        const string sse = """
data: {"id":"abc","model":"Qwen","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"shell_execute","arguments":"{\"Command\":\"ech"}}]}}]}

data: {"id":"abc","model":"Qwen","object":"chat.completion.chunk","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

data: [DONE]

""";

        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl("http://localhost:8000/api/v1");
        var client = new OpenAiCompatibleChatClient(httpClient, endpoint, "test-model");

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")], cancellationToken: TestContext.Current.CancellationToken))
            updates.Add(update);

        var toolUpdate = Assert.Single(updates, u => u.FinishReason == ChatFinishReason.ToolCalls);
        var toolCall = Assert.Single(toolUpdate.Contents.OfType<FunctionCallContent>());
        Assert.NotNull(toolCall.Arguments);
        var sentinel = Assert.Contains(
            Netclaw.Tools.ToolCallArgumentErrors.ArgsParseErrorKey,
            (IDictionary<string, object?>)toolCall.Arguments!);
        Assert.Contains("Raw arguments prefix:", sentinel?.ToString());
    }

    [Fact]
    public async Task SerializesAssistantToolCalls_AndToolResults_InConversationHistory()
    {
        string? body = null;
        using var handler = new RecordingHandler(req =>
        {
            body = req.Content is null ? null : req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"id\":\"1\",\"model\":\"test\",\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"The answer is 4.\"}}]}",
                    Encoding.UTF8, "application/json")
            };
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl("http://localhost:8000");
        var client = new OpenAiCompatibleChatClient(httpClient, endpoint, "test-model");

        // Simulate a conversation with tool call history
        var assistantWithToolCall = new ChatMessage(ChatRole.Assistant,
        [
            new FunctionCallContent("call_42", "calculator", new Dictionary<string, object?> { ["expression"] = "2+2" })
        ]);
        var toolResult = new ChatMessage(ChatRole.Tool,
        [
            new FunctionResultContent("call_42", "4")
        ]);

        await client.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "What is 2+2?"),
            assistantWithToolCall,
            toolResult,
            new ChatMessage(ChatRole.User, "Thanks, and what about 3+3?")
        ], cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(body);

        // Assistant message should include tool_calls array with id, function name, arguments
        Assert.Contains("\"tool_calls\":", body, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"call_42\"", body, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"calculator\"", body, StringComparison.Ordinal);

        // Tool result message should include tool_call_id
        Assert.Contains("\"tool_call_id\":\"call_42\"", body, StringComparison.Ordinal);
        Assert.Contains("\"role\":\"tool\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractsToolCalls_FromTextBasedFormat_WhenModelSkipsStructuredFormat()
    {
        var textWithToolCall = """
            Let me save that for you.
            <tool_call>
            <function=store_memory>
            <parameter=Content>Important note</parameter>
            <parameter=Domain>project:test</parameter>
            </function>
            </tool_call>
            """;

        var responseJson = $"{{\"id\":\"1\",\"model\":\"test\",\"choices\":[{{\"finish_reason\":\"stop\",\"message\":{{\"role\":\"assistant\",\"content\":{System.Text.Json.JsonSerializer.Serialize(textWithToolCall)}}}}}]}}";

        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl("http://localhost:8000");
        var client = new OpenAiCompatibleChatClient(httpClient, endpoint, "test-model");

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "save this")], cancellationToken: TestContext.Current.CancellationToken);

        var toolCall = Assert.Single(response.Messages[^1].Contents.OfType<FunctionCallContent>());
        Assert.Equal("store_memory", toolCall.Name);
        Assert.Equal("Important note", toolCall.Arguments!["Content"]?.ToString());
        Assert.Equal("project:test", toolCall.Arguments!["Domain"]?.ToString());
        Assert.Equal(ChatFinishReason.ToolCalls, response.FinishReason);

        var remainingText = response.Messages[^1].Contents.OfType<TextContent>().FirstOrDefault();
        Assert.NotNull(remainingText);
        Assert.Contains("Let me save that", remainingText.Text);
        Assert.DoesNotContain("<tool_call>", remainingText.Text);
    }

    [Fact]
    public void ToMessage_TextAndImage_ProducesContentArray()
    {
        var imageBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // PNG header stub
        var msg = new ChatMessage(ChatRole.User,
        [
            new TextContent("What is in this image?"),
            new DataContent(imageBytes, "image/png")
        ]);

        var result = OpenAiCompatibleChatClient.ToMessage(msg);

        Assert.Equal("user", result["role"]!.GetValue<string>());
        var content = result["content"]!.AsArray();
        Assert.Equal(2, content.Count);

        // First part should be text
        Assert.Equal("text", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("What is in this image?", content[0]!["text"]!.GetValue<string>());

        // Second part should be image_url with base64 data URI
        Assert.Equal("image_url", content[1]!["type"]!.GetValue<string>());
        var dataUri = content[1]!["image_url"]!["url"]!.GetValue<string>();
        Assert.StartsWith("data:image/png;base64,", dataUri);
        Assert.Equal(Convert.ToBase64String(imageBytes), dataUri["data:image/png;base64,".Length..]);
    }

    [Fact]
    public void ToMessage_ImageOnly_ProducesContentArrayWithoutText()
    {
        var imageBytes = new byte[] { 0xFF, 0xD8, 0xFF }; // JPEG header stub
        var msg = new ChatMessage(ChatRole.User,
        [
            new DataContent(imageBytes, "image/jpeg")
        ]);

        var result = OpenAiCompatibleChatClient.ToMessage(msg);

        var content = result["content"]!.AsArray();
        Assert.Single(content);
        Assert.Equal("image_url", content[0]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void ToMessage_NonImageDataContent_ThrowsBeforeImageUrlSerialization()
    {
        var msg = new ChatMessage(ChatRole.User,
        [
            new DataContent(new byte[] { 1, 2, 3 }, "application/pdf")
        ]);

        var ex = Assert.Throws<InvalidOperationException>(() => OpenAiCompatibleChatClient.ToMessage(msg));
        Assert.Contains("only supports image DataContent", ex.Message, StringComparison.Ordinal);
        Assert.Contains("application/pdf", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToMessage_TextOnly_ProducesSimpleStringContent()
    {
        var msg = new ChatMessage(ChatRole.User, "hello world");

        var result = OpenAiCompatibleChatClient.ToMessage(msg);

        Assert.Equal("user", result["role"]!.GetValue<string>());
        Assert.Equal("hello world", result["content"]!.GetValue<string>());
    }

    [Fact]
    public void SerializeToolResult_HandlesStringResult()
    {
        Assert.Equal("hello", OpenAiCompatibleChatClient.SerializeToolResult("hello"));
    }

    [Fact]
    public void SerializeToolResult_HandlesNullResult()
    {
        Assert.Equal(string.Empty, OpenAiCompatibleChatClient.SerializeToolResult(null));
    }

    [Fact]
    public void SerializeToolResult_HandlesDictionaryResult()
    {
        var dict = new Dictionary<string, object?> { ["key"] = "value" };
        var result = OpenAiCompatibleChatClient.SerializeToolResult(dict);
        Assert.Contains("\"key\"", result, StringComparison.Ordinal);
        Assert.Contains("\"value\"", result, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeToolResult_HandlesJsonElementResult()
    {
        using var doc = JsonDocument.Parse("{\"answer\":42}");
        var result = OpenAiCompatibleChatClient.SerializeToolResult(doc.RootElement);
        Assert.Equal("{\"answer\":42}", result);
    }

    [Fact]
    public void ToolCallTextFilter_SuppressesOnToolCallTag()
    {
        var filter = new OpenAiCompatibleChatClient.ToolCallTextFilter();

        Assert.False(filter.ShouldSuppress("Hello "));
        Assert.False(filter.IsActive);

        // Delta containing the marker triggers suppression
        Assert.True(filter.ShouldSuppress("<tool_call><function=test>"));
        Assert.True(filter.IsActive);

        // Subsequent deltas are also suppressed
        Assert.True(filter.ShouldSuppress("</function></tool_call>"));
    }

    [Fact]
    public void ToolCallTextFilter_ReturnsCleanedText()
    {
        var filter = new OpenAiCompatibleChatClient.ToolCallTextFilter();
        var accumulatedText = new StringBuilder();

        var delta1 = "Preamble text ";
        accumulatedText.Append(delta1);
        filter.ShouldSuppress(delta1);

        var delta2 = "<tool_call><function=search><parameter=Query>test</parameter></function></tool_call> Done";
        accumulatedText.Append(delta2);
        filter.ShouldSuppress(delta2);

        var cleaned = OpenAiCompatibleChatClient.ToolCallTextFilter.GetCleanedText(accumulatedText);
        Assert.Contains("Preamble text", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("<tool_call>", cleaned, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolCallTextFilter_DetectsMarkerSplitAcrossTwoDeltas()
    {
        var filter = new OpenAiCompatibleChatClient.ToolCallTextFilter();

        Assert.False(filter.ShouldSuppress("some text <tool_"));
        Assert.False(filter.IsActive);

        // Second delta completes the marker
        Assert.True(filter.ShouldSuppress("call><function=test>"));
        Assert.True(filter.IsActive);
    }

    [Fact]
    public void ToolCallTextFilter_DetectsMarkerSplitAcrossManySmallDeltas()
    {
        var filter = new OpenAiCompatibleChatClient.ToolCallTextFilter();
        var marker = "<tool_call";

        // Feed marker one character at a time
        for (var i = 0; i < marker.Length - 1; i++)
        {
            Assert.False(filter.ShouldSuppress(marker[i].ToString()));
            Assert.False(filter.IsActive);
        }

        // Last character completes the marker
        Assert.True(filter.ShouldSuppress(marker[^1].ToString()));
        Assert.True(filter.IsActive);
    }

    [Fact]
    public void ToolCallTextFilter_HandlesEmptyDeltas()
    {
        var filter = new OpenAiCompatibleChatClient.ToolCallTextFilter();

        Assert.False(filter.ShouldSuppress(""));
        Assert.False(filter.ShouldSuppress("Hello "));
        Assert.False(filter.ShouldSuppress(""));
        Assert.False(filter.ShouldSuppress(""));
        Assert.True(filter.ShouldSuppress("<tool_call>"));
        Assert.True(filter.IsActive);
    }

    [Fact]
    public void ToolCallTextFilter_MarkerAtStartOfStream()
    {
        var filter = new OpenAiCompatibleChatClient.ToolCallTextFilter();

        Assert.True(filter.ShouldSuppress("<tool_call><function=foo>"));
        Assert.True(filter.IsActive);
    }

    [Fact]
    public async Task StreamingSuppressesToolCallXml_WhenModelEmitsTextToolCalls()
    {
        // Simulate Qwen 3.5 emitting tool calls as XML text (no structured tool_calls)
        const string sse = """
data: {"id":"abc","model":"Qwen","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"role":"assistant","content":"<tool_call>\n<function=web_search>\n<parameter=Query>test</parameter>\n</function>\n</tool_call>"}}]}

data: {"id":"abc","model":"Qwen","object":"chat.completion.chunk","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

data: [DONE]

""";

        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl("http://localhost:8000/api/v1");
        var client = new OpenAiCompatibleChatClient(httpClient, endpoint, "test-model");

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "search test")], cancellationToken: TestContext.Current.CancellationToken))
            updates.Add(update);

        // Should NOT contain any TextContent with XML tool call tags
        var textUpdates = updates.SelectMany(u => u.Contents.OfType<TextContent>()).ToList();
        foreach (var tc in textUpdates)
        {
            Assert.DoesNotContain("<tool_call>", tc.Text ?? string.Empty, StringComparison.Ordinal);
        }

        // Should contain extracted tool call
        var toolCallUpdate = updates.FirstOrDefault(u => u.FinishReason == ChatFinishReason.ToolCalls);
        Assert.NotNull(toolCallUpdate);
        var toolCall = Assert.Single(toolCallUpdate.Contents.OfType<FunctionCallContent>());
        Assert.Equal("web_search", toolCall.Name);
    }

    [Fact]
    public async Task SerializesMultimodalMessage_InRequestPayload()
    {
        string? body = null;
        var imageBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 };

        using var handler = new RecordingHandler(req =>
        {
            body = req.Content is null ? null : req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"1\",\"model\":\"test\",\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"I see a PNG\"}}]}", Encoding.UTF8, "application/json")
            };
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl("http://localhost:8000");
        var client = new OpenAiCompatibleChatClient(httpClient, endpoint, "test-model");

        await client.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User,
            [
                new TextContent("What is this?"),
                new DataContent(imageBytes, "image/png")
            ])
        ], cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(body);
        Assert.Contains("\"type\":\"text\"", body, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"image_url\"", body, StringComparison.Ordinal);
        Assert.Contains("data:image/png;base64,", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractUserMessage_ParsesOpenAiErrorObject()
    {
        var body = """{"error":{"code":500,"message":"image input is not supported - hint: if this is unexpected, you may need to provide the mmproj","type":"server_error"}}""";
        var result = OpenAiCompatibleChatClient.ExtractUserMessage(body, 400);

        Assert.Contains("image input is not supported", result, StringComparison.Ordinal);
        Assert.Contains("400", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractUserMessage_ParsesSimpleErrorString()
    {
        var body = """{"error":"something went wrong"}""";
        var result = OpenAiCompatibleChatClient.ExtractUserMessage(body, 500);

        Assert.Contains("something went wrong", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractUserMessage_FallsBackOnInvalidJson()
    {
        var result = OpenAiCompatibleChatClient.ExtractUserMessage("not json", 502);

        Assert.Contains("502", result, StringComparison.Ordinal);
        Assert.Contains("not valid JSON", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExtractUserMessage_FallsBackOnNullBody()
    {
        var result = OpenAiCompatibleChatClient.ExtractUserMessage(null, 401);

        Assert.Contains("credentials", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ThrowsProviderException_OnHttpError()
    {
        var errorResponse = """{"error":{"message":"model not found"}}""";
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
        {
            Content = new StringContent(errorResponse, Encoding.UTF8, "application/json")
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl("http://localhost:8000");
        var client = new OpenAiCompatibleChatClient(httpClient, endpoint, "test-model");

        var ex = await Assert.ThrowsAsync<Netclaw.Configuration.ProviderException>(
            async () => await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("model not found", ex.UserMessage, StringComparison.Ordinal);
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task GetResponseAsync_ParsesUsageFromResponse()
    {
        const string json = """
            {"id":"1","model":"test","choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"hi"}}],
             "usage":{"prompt_tokens":100,"completion_tokens":25,"total_tokens":125}}
            """;

        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl("http://localhost:8000");
        var client = new OpenAiCompatibleChatClient(httpClient, endpoint, "test-model");

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(response.Usage);
        Assert.Equal(100, response.Usage!.InputTokenCount);
        Assert.Equal(25, response.Usage.OutputTokenCount);
        Assert.Equal(125, response.Usage.TotalTokenCount);
    }

    [Fact]
    public async Task StreamingResponse_EmitsUsageContent_WhenPresent()
    {
        const string sse = """
            data: {"id":"abc","model":"test","choices":[{"index":0,"delta":{"role":"assistant","content":"hi"}}]}

            data: {"id":"abc","model":"test","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":50,"completion_tokens":10,"total_tokens":60}}

            data: [DONE]

            """;

        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl("http://localhost:8000");
        var client = new OpenAiCompatibleChatClient(httpClient, endpoint, "test-model");

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")], cancellationToken: TestContext.Current.CancellationToken))
            updates.Add(update);

        var usageContents = updates.SelectMany(u => u.Contents.OfType<UsageContent>()).ToList();
        Assert.Single(usageContents);
        Assert.Equal(50, usageContents[0].Details.InputTokenCount);
        Assert.Equal(10, usageContents[0].Details.OutputTokenCount);
    }

    [Fact]
    public async Task StreamingResponse_EmitsUsageContent_WhenInSeparateEmptyChoicesChunk()
    {
        // Real-world OpenAI-compatible APIs send usage in a final chunk with empty choices array
        const string sse = """
            data: {"id":"abc","model":"test","choices":[{"index":0,"delta":{"role":"assistant","content":"hi"}}]}

            data: {"id":"abc","model":"test","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

            data: {"id":"abc","model":"test","choices":[],"usage":{"prompt_tokens":50,"completion_tokens":10,"total_tokens":60}}

            data: [DONE]

            """;

        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl("http://localhost:8000");
        var client = new OpenAiCompatibleChatClient(httpClient, endpoint, "test-model");

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")], cancellationToken: TestContext.Current.CancellationToken))
            updates.Add(update);

        var usageContents = updates.SelectMany(u => u.Contents.OfType<UsageContent>()).ToList();
        Assert.Single(usageContents);
        Assert.Equal(50, usageContents[0].Details.InputTokenCount);
        Assert.Equal(10, usageContents[0].Details.OutputTokenCount);
    }

    [Fact]
    public void ParseUsage_ReturnsNull_WhenUsageFieldMissing()
    {
        using var doc = JsonDocument.Parse("""{"id":"1","model":"test","choices":[]}""");
        Assert.Null(OpenAiCompatibleChatClient.ParseUsage(doc.RootElement));
    }

    [Fact]
    public void ParseUsage_TimingsSurviveUsageDetailsAdd()
    {
        // Simulate what ToChatResponse does: creates a new UsageDetails and calls Add()
        // with the parsed result. Verify CachedInputTokenCount and AdditionalCounts survive.
        using var doc = JsonDocument.Parse("""
        {
            "usage": { "prompt_tokens": 100, "completion_tokens": 20, "total_tokens": 120 },
            "timings": {
                "cache_n": 85,
                "prompt_ms": 139.655,
                "predicted_per_second": 31.241
            }
        }
        """);

        var parsed = OpenAiCompatibleChatClient.ParseUsage(doc.RootElement)!;

        // This is what ToChatResponse does internally: new UsageDetails().Add(parsed)
        var aggregated = new UsageDetails();
        aggregated.Add(parsed);

        Assert.Equal(100, aggregated.InputTokenCount);
        Assert.Equal(85, aggregated.CachedInputTokenCount);
        Assert.NotNull(aggregated.AdditionalCounts);
        Assert.Equal(139655, aggregated.AdditionalCounts["prompt_us"]);
        Assert.Equal(3124, aggregated.AdditionalCounts["predicted_tok_per_sec_x100"]);
    }

    [Fact]
    public void ParseUsage_ReadsTokenCounts_WithoutTimings()
    {
        using var doc = JsonDocument.Parse("""
        {
            "usage": { "prompt_tokens": 50, "completion_tokens": 10, "total_tokens": 60 }
        }
        """);

        var usage = OpenAiCompatibleChatClient.ParseUsage(doc.RootElement);

        Assert.NotNull(usage);
        Assert.Equal(50, usage.InputTokenCount);
        Assert.Equal(10, usage.OutputTokenCount);
        Assert.Equal(60, usage.TotalTokenCount);
        Assert.Null(usage.CachedInputTokenCount);
        Assert.Null(usage.AdditionalCounts);
    }

    [Fact]
    public void ParseUsage_ReadsLlamaCppTimings_WhenPresent()
    {
        using var doc = JsonDocument.Parse("""
        {
            "usage": { "prompt_tokens": 100, "completion_tokens": 20, "total_tokens": 120 },
            "timings": {
                "cache_n": 85,
                "prompt_n": 15,
                "prompt_ms": 139.655,
                "prompt_per_second": 78.766,
                "predicted_n": 20,
                "predicted_ms": 160.048,
                "predicted_per_token_ms": 8.002,
                "predicted_per_second": 31.241
            }
        }
        """);

        var usage = OpenAiCompatibleChatClient.ParseUsage(doc.RootElement);

        Assert.NotNull(usage);
        Assert.Equal(100, usage.InputTokenCount);
        Assert.Equal(20, usage.OutputTokenCount);
        Assert.Equal(85, usage.CachedInputTokenCount);

        Assert.NotNull(usage.AdditionalCounts);
        // prompt_ms stored as microseconds for integer precision
        Assert.Equal(139655, usage.AdditionalCounts["prompt_us"]);
        // predicted_per_second stored as x100 for integer precision
        Assert.Equal(3124, usage.AdditionalCounts["predicted_tok_per_sec_x100"]);
        // prompt_per_second stored as x100
        Assert.Equal(7876, usage.AdditionalCounts["prompt_tok_per_sec_x100"]);
        // predicted_ms stored as microseconds
        Assert.Equal(160048, usage.AdditionalCounts["predicted_us"]);
    }

    [Fact]
    public void ParseUsage_GracefullyIgnoresTimings_WhenTimingsObjectAbsent()
    {
        using var doc = JsonDocument.Parse("""
        {
            "usage": { "prompt_tokens": 50, "completion_tokens": 10, "total_tokens": 60 }
        }
        """);

        var usage = OpenAiCompatibleChatClient.ParseUsage(doc.RootElement);

        Assert.NotNull(usage);
        Assert.Equal(50, usage.InputTokenCount);
        Assert.Null(usage.CachedInputTokenCount);
        Assert.Null(usage.AdditionalCounts);
    }

    [Fact]
    public void ParseUsage_HandlesPartialTimings()
    {
        // Some fields present, others missing — should not throw
        using var doc = JsonDocument.Parse("""
        {
            "usage": { "prompt_tokens": 50, "completion_tokens": 10, "total_tokens": 60 },
            "timings": { "cache_n": 30 }
        }
        """);

        var usage = OpenAiCompatibleChatClient.ParseUsage(doc.RootElement);

        Assert.NotNull(usage);
        Assert.Equal(30, usage.CachedInputTokenCount);
        // No throughput fields → AdditionalCounts should be empty or null
        Assert.True(usage.AdditionalCounts is null || usage.AdditionalCounts.Count == 0);
    }

    [Fact]
    public async Task StreamingResponse_WithTimings_SurfacesCachedTokensAndThroughput()
    {
        // Simulate a llama.cpp streaming response where the final chunk includes timings
        const string sse = """
            data: {"id":"abc","model":"test","choices":[{"index":0,"delta":{"content":"Hi"},"finish_reason":"stop"}]}

            data: {"id":"abc","model":"test","choices":[],"usage":{"prompt_tokens":100,"completion_tokens":5,"total_tokens":105},"timings":{"cache_n":80,"prompt_n":20,"prompt_ms":50.5,"predicted_per_second":25.3}}

            data: [DONE]

            """;

        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl("http://localhost:8000");
        var client = new OpenAiCompatibleChatClient(httpClient, endpoint, "test-model");

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        var response = updates.ToChatResponse();

        Assert.NotNull(response.Usage);
        Assert.Equal(100, response.Usage.InputTokenCount);
        Assert.Equal(5, response.Usage.OutputTokenCount);
        Assert.Equal(80, response.Usage.CachedInputTokenCount);
        Assert.NotNull(response.Usage.AdditionalCounts);
        Assert.Equal(50500, response.Usage.AdditionalCounts["prompt_us"]);
        Assert.Equal(2530, response.Usage.AdditionalCounts["predicted_tok_per_sec_x100"]);
    }

    [Fact]
    public async Task StreamingRequest_IncludesStreamOptions()
    {
        const string sse = """
            data: {"id":"abc","model":"test","choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}]}

            data: [DONE]

            """;

        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl("http://localhost:8000");
        var client = new OpenAiCompatibleChatClient(httpClient, endpoint, "test-model");

        await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")], cancellationToken: TestContext.Current.CancellationToken))
        {
            // consume
        }

        using var doc = JsonDocument.Parse(handler.RequestBodies.Single());
        var root = doc.RootElement;
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.True(root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
    }

    [Fact]
    public async Task StreamingYieldsKeepaliveUpdates_WhenToolCallFilterSuppressesText()
    {
        // Simulate a model that emits normal text, then switches to <tool_call> XML.
        // After the filter activates, the client should yield empty keepalive updates
        // instead of silently dropping SSE events.
        const string sse = """
data: {"id":"abc","model":"test","choices":[{"index":0,"delta":{"role":"assistant","content":"Let me "}}]}

data: {"id":"abc","model":"test","choices":[{"index":0,"delta":{"content":"help. "}}]}

data: {"id":"abc","model":"test","choices":[{"index":0,"delta":{"content":"<tool_call>\n<function=search>\n"}}]}

data: {"id":"abc","model":"test","choices":[{"index":0,"delta":{"content":"<parameter=Query>test</parameter>\n"}}]}

data: {"id":"abc","model":"test","choices":[{"index":0,"delta":{"content":"</function>\n</tool_call>"}}]}

data: {"id":"abc","model":"test","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

data: [DONE]

""";

        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl("http://localhost:8000/api/v1");
        var client = new OpenAiCompatibleChatClient(httpClient, endpoint, "test-model");

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "help")],
            cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        // Should have received updates for every SSE event — not just the non-suppressed ones.
        // 2 normal text deltas + 3 suppressed (yielded as keepalives) + 1 finish + 1 tool call = 7
        Assert.True(updates.Count >= 5,
            $"Expected at least 5 updates (2 text + 3 keepalives), got {updates.Count}");

        // The first two streaming updates have visible text content.
        // The end-of-stream fallback also emits a cleaned text update (non-tool-call preamble),
        // so expect 3 total text updates.
        var textUpdates = updates
            .Where(u => u.Contents.OfType<TextContent>().Any(t => !string.IsNullOrEmpty(t.Text)))
            .ToList();
        Assert.Equal(3, textUpdates.Count);

        // Suppressed updates should be content-free keepalives (Role set, no text content)
        var keepaliveUpdates = updates
            .Where(u => u.Role == ChatRole.Assistant
                        && !u.Contents.OfType<TextContent>().Any(t => !string.IsNullOrEmpty(t.Text))
                        && !u.Contents.OfType<FunctionCallContent>().Any()
                        && u.FinishReason is null)
            .ToList();
        Assert.True(keepaliveUpdates.Count >= 2,
            $"Expected at least 2 keepalive updates for suppressed tool call text, got {keepaliveUpdates.Count}");
    }

    [Fact]
    public async Task StreamingKeepalive_ToolCallStillExtracted_AfterSuppression()
    {
        // Verify that the end-of-stream tool call extraction still works when text
        // was suppressed and keepalive updates were yielded instead.
        const string sse = """
data: {"id":"abc","model":"test","choices":[{"index":0,"delta":{"role":"assistant","content":"<tool_call>\n<function=store_memory>\n<parameter=Content>note</parameter>\n<parameter=Domain>test</parameter>\n</function>\n</tool_call>"}}]}

data: {"id":"abc","model":"test","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

data: [DONE]

""";

        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl("http://localhost:8000/api/v1");
        var client = new OpenAiCompatibleChatClient(httpClient, endpoint, "test-model");

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "save")],
            cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        // Tool call should still be extracted at end-of-stream
        var toolCallUpdate = updates.FirstOrDefault(u => u.FinishReason == ChatFinishReason.ToolCalls);
        Assert.NotNull(toolCallUpdate);
        var toolCall = Assert.Single(toolCallUpdate.Contents.OfType<FunctionCallContent>());
        Assert.Equal("store_memory", toolCall.Name);
    }

    // ─── Phase 0 diagnostic tests for residual KV cache prefix drift ─────────
    // These tests pin down byte-level invariants the existing logical
    // assertions (Role/Text comparison) don't catch. A failure here means
    // some serialization step between Assemble and the HTTP body is
    // introducing per-turn drift that NormalizeMessages alone can't see.

    [Fact]
    public async Task Outbound_payload_static_prefix_byte_identical_when_only_volatile_context_differs()
    {
        // Post-#1176: per-turn volatile context lives inside <context>...
        // </context> on the LAST User message rather than a trailing System
        // tail (vLLM rejects trailing System messages). The cache prefix
        // invariant becomes: leading System + every message except the last
        // are byte-identical across consecutive turns.
        var turn1Messages = new[]
        {
            new ChatMessage(ChatRole.System, "You are Netclaw, a helpful assistant.\n\n[session]\nid: test/cache-stability"),
            new ChatMessage(ChatRole.User, "<context>\n[memory-recall]\nstatus: healthy\nrecall-A payload\n</context>\n\nfirst question"),
        };
        var turn2Messages = new[]
        {
            new ChatMessage(ChatRole.System, "You are Netclaw, a helpful assistant.\n\n[session]\nid: test/cache-stability"),
            new ChatMessage(ChatRole.User, "first question"),
            new ChatMessage(ChatRole.Assistant, "first answer"),
            new ChatMessage(ChatRole.User, "<context>\n[memory-recall]\nstatus: healthy\nrecall-B payload completely different\n</context>\n\nsecond question"),
        };

        var bodies = await CaptureTwoRequestBodies(turn1Messages, turn2Messages);

        // Strip the trailing volatile-carrying User message from each and
        // assert byte equality position-by-position over what remains.
        var turn1Static = ExtractStaticPrefixMessages(bodies.body1);
        var turn2Static = ExtractStaticPrefixMessages(bodies.body2);
        Assert.True(turn2Static.Count >= turn1Static.Count,
            $"Turn 2 must have at least as many static messages as turn 1 (turn1={turn1Static.Count}, turn2={turn2Static.Count}).");
        for (var i = 0; i < turn1Static.Count; i++)
        {
            Assert.Equal(turn1Static[i], turn2Static[i]);
        }
    }

    [Fact]
    public async Task Outbound_payload_tools_array_byte_identical_across_calls_with_same_tool_set()
    {
        // Regression B canary: two consecutive calls with the same tool
        // collection (same order, same definitions) must serialize the
        // `tools` field to byte-identical JSON. A failure here means
        // tool-list serialization is non-deterministic, which would bust
        // the cache the moment any tool is added to a session.
        using var schemaDoc = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}},\"required\":[\"query\"]}");
        var toolA = AIFunctionFactory.CreateDeclaration("search_tools", "Search tools", schemaDoc.RootElement);
        using var storeSchemaDoc = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}},\"required\":[\"text\"]}");
        var toolB = AIFunctionFactory.CreateDeclaration("store_memory", "Store memory", storeSchemaDoc.RootElement);

        var sameMessages = new[] { new ChatMessage(ChatRole.User, "hello") };

        var capturedBodies = new List<string>();
        using var handler = new RecordingHandler(req =>
        {
            capturedBodies.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"id\":\"1\",\"model\":\"test\",\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"hi\"}}]}",
                    Encoding.UTF8, "application/json")
            };
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl("http://localhost:8000");
        var client = new OpenAiCompatibleChatClient(httpClient, endpoint, "test-model");

        var options = new ChatOptions { Tools = [toolA, toolB] };
        await client.GetResponseAsync(sameMessages, options, cancellationToken: TestContext.Current.CancellationToken);
        await client.GetResponseAsync(sameMessages, options, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, capturedBodies.Count);
        var tools1 = ExtractToolsJson(capturedBodies[0]);
        var tools2 = ExtractToolsJson(capturedBodies[1]);
        Assert.Equal(tools1, tools2);
    }

    private async Task<(string body1, string body2)> CaptureTwoRequestBodies(
        IReadOnlyList<ChatMessage> turn1, IReadOnlyList<ChatMessage> turn2)
    {
        var bodies = new List<string>();
        using var handler = new RecordingHandler(req =>
        {
            bodies.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"id\":\"1\",\"model\":\"test\",\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"hi\"}}]}",
                    Encoding.UTF8, "application/json")
            };
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl("http://localhost:8000");
        var client = new OpenAiCompatibleChatClient(httpClient, endpoint, "test-model");

        await client.GetResponseAsync(turn1, cancellationToken: TestContext.Current.CancellationToken);
        await client.GetResponseAsync(turn2, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, bodies.Count);
        return (bodies[0], bodies[1]);
    }

    private static List<string> ExtractStaticPrefixMessages(string requestBody)
    {
        // Return the messages array as a list of per-element JSON strings,
        // with the trailing message stripped. Post-#1176 the trailing
        // message is the volatile-carrying User turn (with <context>);
        // pre-#1176 it was a trailing System tail. Either way, that final
        // entry is the only one allowed to diverge between turns; the rest
        // must be byte-identical position-for-position for the KV cache
        // prefix to extend.
        var root = JsonNode.Parse(requestBody)!.AsObject();
        var messages = root["messages"]!.AsArray();
        var end = messages.Count > 0 ? messages.Count - 1 : 0;
        var result = new List<string>(end);
        for (var i = 0; i < end; i++)
            result.Add(messages[i]!.ToJsonString());
        return result;
    }

    private static string ExtractToolsJson(string requestBody)
    {
        using var doc = JsonDocument.Parse(requestBody);
        if (!doc.RootElement.TryGetProperty("tools", out var toolsElement))
            return "(none)";
        return toolsElement.GetRawText();
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Content is not null)
                RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            return _handler(request);
        }
    }
}

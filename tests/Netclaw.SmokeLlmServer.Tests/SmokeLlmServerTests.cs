// -----------------------------------------------------------------------
// <copyright file="SmokeLlmServerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Netclaw.SmokeLlmServer;
using Xunit;

namespace Netclaw.SmokeLlmServer.Tests;

public sealed class SmokeLlmServerTests : IAsyncLifetime
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"netclaw-smoke-llm-tests-{Guid.NewGuid():N}");
    private WebApplication? _app;
    private HttpClient? _client;
    private string _requestRecordPath = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_tempDirectory);
        _requestRecordPath = Path.Combine(_tempDirectory, "requests.jsonl");
        _app = await SmokeLlmServerHost.StartAsync(new SmokeLlmServerOptions(0, _requestRecordPath));
        _client = new HttpClient { BaseAddress = new Uri(SmokeLlmServerHost.GetBaseAddress(_app)) };
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
            await _app.DisposeAsync();
        Directory.Delete(_tempDirectory, recursive: true);
    }

    [Fact]
    public async Task Models_and_non_streaming_completion_use_the_smoke_contract()
    {
        var models = await Client.GetFromJsonAsync<JsonElement>("/v1/models", TestContext.Current.CancellationToken);
        Assert.Equal(SmokeLlmServerOptions.ModelId, models.GetProperty("data")[0].GetProperty("id").GetString());

        var response = await Client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = SmokeLlmServerOptions.ModelId,
            messages = new[] { new { role = "user", content = "secret prompt" } },
            tools = new[] { new { type = "function", function = new { name = "safe_tool" } } }
        }, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var completion = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Netclaw smoke response.", completion.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());

        var record = await File.ReadAllTextAsync(_requestRecordPath, TestContext.Current.CancellationToken);
        Assert.Contains("\"ToolsPresent\":true", record, StringComparison.Ordinal);
        Assert.DoesNotContain("secret prompt", record, StringComparison.Ordinal);
        Assert.DoesNotContain("safe_tool", record, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Streaming_completion_terminates_with_done_event()
    {
        var response = await Client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = SmokeLlmServerOptions.ModelId,
            messages = Array.Empty<object>(),
            stream = true
        }, TestContext.Current.CancellationToken);

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Netclaw smoke response.", body, StringComparison.Ordinal);
        Assert.Contains("data: [DONE]", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Skill_plugin_proof_requests_both_logical_skill_tools()
    {
        var messages = new List<object>
        {
            new { role = "user", content = SmokeLlmServerHost.SkillPluginProofPrompt },
        };
        var tools = new[]
        {
            new { type = "function", function = new { name = "skill_load" } },
            new { type = "function", function = new { name = "skill_read_resource" } },
        };

        var first = await PostStreamingAsync(messages, tools);
        Assert.Contains("\"name\":\"skill_load\"", first, StringComparison.Ordinal);

        messages.Add(new
        {
            role = "tool",
            tool_call_id = "call_skill_load",
            content = "# Akka.NET Best Practices\nAvailable: cluster-local-abstractions.md",
        });
        var second = await PostStreamingAsync(messages, tools);
        Assert.Contains("\"name\":\"skill_read_resource\"", second, StringComparison.Ordinal);

        messages.Add(new
        {
            role = "tool",
            tool_call_id = "call_skill_resource",
            content = "# Cluster/Local Mode Abstractions\nGenericChildPerEntityParent",
        });
        var third = await PostStreamingAsync(messages, tools);
        Assert.Contains(SmokeLlmServerHost.SkillPluginProofResponse, third, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bad_request_and_non_loopback_address_fail_loudly()
    {
        var response = await Client.PostAsJsonAsync("/v1/chat/completions", new { model = "unknown" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(SmokeLlmServerOptions.ModelId, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await SmokeLlmServerHost.StartAsync(new SmokeLlmServerOptions(0, _requestRecordPath, IPAddress.Any), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Skill_feed_changes_all_content_as_one_version()
    {
        using var phaseAResponse = await Client.PostAsync(
            "/test/skill-feed/phase/A",
            null,
            TestContext.Current.CancellationToken);
        phaseAResponse.EnsureSuccessStatusCode();

        var phaseAIndex = await Client.GetFromJsonAsync<JsonElement>(
            "/test/skill-feed/.well-known/agent-skills/index.json",
            TestContext.Current.CancellationToken);
        Assert.Equal("1.0.0", phaseAIndex.GetProperty("skills")[0].GetProperty("version").GetString());
        Assert.Equal(
            "native skill sync resource phase A\n",
            await Client.GetStringAsync("/test/skill-feed/proof.txt", TestContext.Current.CancellationToken));

        using var phaseBResponse = await Client.PostAsync(
            "/test/skill-feed/phase/B",
            null,
            TestContext.Current.CancellationToken);
        phaseBResponse.EnsureSuccessStatusCode();

        var phaseBIndex = await Client.GetFromJsonAsync<JsonElement>(
            "/test/skill-feed/.well-known/agent-skills/index.json",
            TestContext.Current.CancellationToken);
        Assert.Equal("2.0.0", phaseBIndex.GetProperty("skills")[0].GetProperty("version").GetString());
        Assert.Equal(
            "native skill sync resource phase B\n",
            await Client.GetStringAsync("/test/skill-feed/proof.txt", TestContext.Current.CancellationToken));

        using var invalidResponse = await Client.PostAsync(
            "/test/skill-feed/phase/C",
            null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
    }

    private HttpClient Client => _client ?? throw new InvalidOperationException("The test server is not initialized.");

    private async Task<string> PostStreamingAsync(
        IReadOnlyList<object> messages,
        object tools)
    {
        using var response = await Client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = SmokeLlmServerOptions.ModelId,
            messages,
            tools,
            stream = true,
        }, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }
}

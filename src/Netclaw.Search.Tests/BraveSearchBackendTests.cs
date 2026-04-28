// -----------------------------------------------------------------------
// <copyright file="BraveSearchBackendTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using Netclaw.Search;
using Xunit;

namespace Netclaw.Search.Tests;

public class BraveSearchBackendTests
{
    [Fact]
    public void ParseResults_extracts_results_from_fixture()
    {
        var json = LoadFixture("brave-search-akka-dotnet.json");
        var results = BraveSearchBackend.ParseResults(json, 30);

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void ParseResults_extracts_titles_and_urls()
    {
        var json = LoadFixture("brave-search-akka-dotnet.json");
        var results = BraveSearchBackend.ParseResults(json, 30);

        var first = results[0];
        Assert.Contains("akka.net", first.Url, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("akkadotnet", first.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseResults_extracts_descriptions()
    {
        var json = LoadFixture("brave-search-akka-dotnet.json");
        var results = BraveSearchBackend.ParseResults(json, 30);

        var first = results[0];
        Assert.NotEmpty(first.Snippet);
        Assert.Contains("Akka", first.Snippet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseResults_respects_max_results()
    {
        var json = LoadFixture("brave-search-akka-dotnet.json");
        var results = BraveSearchBackend.ParseResults(json, 2);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void ParseResults_returns_empty_for_missing_web_section()
    {
        var json = """{"query":{"original":"test"}}""";
        var results = BraveSearchBackend.ParseResults(json, 10);

        Assert.Empty(results);
    }

    [Fact]
    public void ParseResults_returns_empty_for_empty_results()
    {
        var json = """{"web":{"type":"search","results":[]}}""";
        var results = BraveSearchBackend.ParseResults(json, 10);

        Assert.Empty(results);
    }

    [Fact]
    public void ParseResults_strips_html_tags_and_decodes_entities()
    {
        var json = LoadFixture("brave-search-akka-dotnet.json");
        var results = BraveSearchBackend.ParseResults(json, 30);

        var first = results[0];
        // Fixture description contains "<strong>a .NET port...</strong>" and "&amp;"
        Assert.DoesNotContain("<strong>", first.Snippet, StringComparison.Ordinal);
        Assert.DoesNotContain("</strong>", first.Snippet, StringComparison.Ordinal);
        Assert.DoesNotContain("&amp;", first.Snippet, StringComparison.Ordinal);
        // Verify the decoded content is present
        Assert.Contains("a .NET port", first.Snippet, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseResults_skips_entries_missing_url()
    {
        var json = """
        {
          "web": {
            "results": [
              {"title": "No URL", "description": "Missing url field"},
              {"title": "Has URL", "url": "https://example.com", "description": "Valid"}
            ]
          }
        }
        """;
        var results = BraveSearchBackend.ParseResults(json, 10);

        Assert.Single(results);
        Assert.Equal("https://example.com", results[0].Url);
    }

    [Fact]
    public async Task SearchAsync_retries_on_429_then_succeeds()
    {
        var handler = new FakeHttpMessageHandler();
        var rateLimitResponse = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        rateLimitResponse.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
        handler.Enqueue(rateLimitResponse);
        handler.Enqueue(CreateSuccessResponse());

        var backend = new BraveSearchBackend("test-key", new HttpClient(handler));
        var result = await backend.SearchAsync("test", 10, CancellationToken.None);

        Assert.IsType<SearchBackendResult.Success>(result);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task SearchAsync_returns_error_after_max_retries_on_429()
    {
        var handler = new FakeHttpMessageHandler();
        for (var i = 0; i < 3; i++)
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            handler.Enqueue(r);
        }

        var backend = new BraveSearchBackend("test-key", new HttpClient(handler));
        var result = await backend.SearchAsync("test", 10, CancellationToken.None);

        var error = Assert.IsType<SearchBackendResult.Error>(result);
        Assert.Contains("rate limit", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public void ParseRetryAfter_returns_delta_from_header()
    {
        var header = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
        var delay = BraveSearchBackend.ParseRetryAfter(header, 0, TimeProvider.System);
        Assert.Equal(TimeSpan.FromSeconds(30), delay);
    }

    [Fact]
    public void ParseRetryAfter_returns_remaining_time_from_date_header()
    {
        var now = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var future = now.AddSeconds(45);
        var header = new RetryConditionHeaderValue(future);
        var fakeTime = new FixedTimeProvider(now);

        var delay = BraveSearchBackend.ParseRetryAfter(header, 0, fakeTime);

        Assert.Equal(TimeSpan.FromSeconds(45), delay);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(1, 10)]
    [InlineData(2, 20)]
    public void ParseRetryAfter_falls_back_to_exponential_backoff_when_header_absent(int attempt, int expectedSeconds)
    {
        var delay = BraveSearchBackend.ParseRetryAfter(null, attempt, TimeProvider.System);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Fact]
    public async Task SearchAsync_handles_gzip_encoded_response()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(CreateGzipSuccessResponse());

        var backend = new BraveSearchBackend("test-key", new HttpClient(handler));
        var result = await backend.SearchAsync("test", 10, CancellationToken.None);

        var success = Assert.IsType<SearchBackendResult.Success>(result);
        Assert.Single(success.Results);
        Assert.Equal("https://example.com", success.Results[0].Url);
    }

    [Fact]
    public async Task SearchAsync_returns_controlled_error_for_unexpected_binary_content()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(CreateBinaryResponse());

        var backend = new BraveSearchBackend("test-key", new HttpClient(handler));
        var result = await backend.SearchAsync("test", 10, CancellationToken.None);

        var error = Assert.IsType<SearchBackendResult.Error>(result);
        Assert.Contains("application/octet-stream", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("200", error.Message, StringComparison.Ordinal);
    }

    private static HttpResponseMessage CreateSuccessResponse()
    {
        const string json = """{"web":{"results":[{"title":"Test Result","url":"https://example.com","description":"A test result"}]}}""";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage CreateGzipSuccessResponse()
    {
        const string json = """{"web":{"results":[{"title":"Test Result","url":"https://example.com","description":"A test result"}]}}""";
        var ms = new MemoryStream();
        using (var gzip = new GZipStream(ms, CompressionMode.Compress, leaveOpen: true))
        using (var writer = new StreamWriter(gzip, System.Text.Encoding.UTF8))
            writer.Write(json);
        ms.Position = 0;

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(ms)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        response.Content.Headers.ContentEncoding.Add("gzip");
        return response;
    }

    private static HttpResponseMessage CreateBinaryResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 0x1F, 0x8B, 0x00, 0x01 })
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return response;
    }

    private static string LoadFixture(string filename)
    {
        var assembly = typeof(BraveSearchBackendTests).Assembly;
        var resourceName = $"Netclaw.Search.Tests.Fixtures.{filename}";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Fixture not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();
        public int RequestCount { get; private set; }

        public void Enqueue(HttpResponseMessage response) => _responses.Enqueue(response);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (_responses.Count == 0)
                throw new InvalidOperationException("No more responses queued.");
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

// -----------------------------------------------------------------------
// <copyright file="SearXngBackendTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Search;
using Xunit;

namespace Netclaw.Search.Tests;

public class SearXngBackendTests
{
    [Fact]
    public void ParseResults_extracts_results_from_fixture()
    {
        var json = LoadFixture("searxng-akka-dotnet.json");
        var results = SearXngBackend.ParseResults(json, 30);

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void ParseResults_extracts_titles_and_urls()
    {
        var json = LoadFixture("searxng-akka-dotnet.json");
        var results = SearXngBackend.ParseResults(json, 30);

        var first = results[0];
        Assert.Contains("akka.net", first.Url, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("akkadotnet", first.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseResults_extracts_content_as_snippet()
    {
        var json = LoadFixture("searxng-akka-dotnet.json");
        var results = SearXngBackend.ParseResults(json, 30);

        var first = results[0];
        Assert.NotEmpty(first.Snippet);
        Assert.Contains("Akka", first.Snippet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseResults_respects_max_results()
    {
        var json = LoadFixture("searxng-akka-dotnet.json");
        var results = SearXngBackend.ParseResults(json, 2);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void ParseResults_returns_empty_for_missing_results_array()
    {
        var json = """{"query":"test","number_of_results":0}""";
        var results = SearXngBackend.ParseResults(json, 10);

        Assert.Empty(results);
    }

    [Fact]
    public void ParseResults_returns_empty_for_empty_results()
    {
        var json = """{"query":"test","number_of_results":0,"results":[]}""";
        var results = SearXngBackend.ParseResults(json, 10);

        Assert.Empty(results);
    }

    [Fact]
    public void ParseResults_skips_entries_missing_url()
    {
        var json = """
        {
          "results": [
            {"title": "No URL", "content": "Missing url field"},
            {"title": "Has URL", "url": "https://example.com", "content": "Valid"}
          ]
        }
        """;
        var results = SearXngBackend.ParseResults(json, 10);

        Assert.Single(results);
        Assert.Equal("https://example.com", results[0].Url);
    }

    [Fact]
    public async Task SearchAsync_sends_user_agent_header()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(CreateSuccessResponse());

        var backend = new SearXngBackend("http://searxng.local", new HttpClient(handler));
        await backend.SearchAsync("test", 5, CancellationToken.None);

        // UA parses as a product + a comment, so there are 2 entries (Netclaw/x.y.z and "(+https://...)").
        var product = handler.LastRequest!.Headers.UserAgent.First(p => p.Product is not null).Product!;
        Assert.Equal("Netclaw", product.Name);
        Assert.False(string.IsNullOrEmpty(product.Version));
    }

    [Fact]
    public async Task SearchAsync_retries_on_429_then_succeeds()
    {
        var handler = new FakeHttpMessageHandler();
        var rateLimit = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        rateLimit.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
        handler.Enqueue(rateLimit);
        handler.Enqueue(CreateSuccessResponse());

        var backend = new SearXngBackend("http://searxng.local", new HttpClient(handler));
        var result = await backend.SearchAsync("test", 5, CancellationToken.None);

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

        var backend = new SearXngBackend("http://searxng.local", new HttpClient(handler));
        var result = await backend.SearchAsync("test", 5, CancellationToken.None);

        var error = Assert.IsType<SearchBackendResult.Error>(result);
        Assert.Contains("rate limit", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task SearchAsync_honors_retry_after_http_date()
    {
        var fakeNow = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var handler = new FakeHttpMessageHandler();
        var rateLimit = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        rateLimit.Headers.RetryAfter = new RetryConditionHeaderValue(fakeNow.AddMilliseconds(1));
        handler.Enqueue(rateLimit);
        handler.Enqueue(CreateSuccessResponse());

        var backend = new SearXngBackend(
            "http://searxng.local",
            new HttpClient(handler),
            new FakeTimeProvider(fakeNow));
        var result = await backend.SearchAsync("test", 5, CancellationToken.None);

        Assert.IsType<SearchBackendResult.Success>(result);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task SearchAsync_returns_error_for_html_response()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>...</body></html>", System.Text.Encoding.UTF8, "text/html"),
        });

        var backend = new SearXngBackend("http://searxng.local", new HttpClient(handler));
        var result = await backend.SearchAsync("test", 5, CancellationToken.None);

        var error = Assert.IsType<SearchBackendResult.Error>(result);
        Assert.Contains("settings.yml", error.Message, StringComparison.Ordinal);
        Assert.Contains("search.formats", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task SearchAsync_returns_error_for_403_format_not_enabled()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Forbidden));

        var backend = new SearXngBackend("http://searxng.local", new HttpClient(handler));
        var result = await backend.SearchAsync("test", 5, CancellationToken.None);

        var error = Assert.IsType<SearchBackendResult.Error>(result);
        Assert.Contains("settings.yml", error.Message, StringComparison.Ordinal);
        Assert.Contains("search.formats", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task SearchAsync_returns_error_for_malformed_json()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not json at all", System.Text.Encoding.UTF8, "application/json"),
        });

        var backend = new SearXngBackend("http://searxng.local", new HttpClient(handler));
        var result = await backend.SearchAsync("test", 5, CancellationToken.None);

        var error = Assert.IsType<SearchBackendResult.Error>(result);
        Assert.Contains("malformed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchAsync_returns_empty_results_for_empty_array()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"results":[]}""", System.Text.Encoding.UTF8, "application/json"),
        });

        var backend = new SearXngBackend("http://searxng.local", new HttpClient(handler));
        var result = await backend.SearchAsync("test", 5, CancellationToken.None);

        var success = Assert.IsType<SearchBackendResult.Success>(result);
        Assert.Empty(success.Results);
    }

    [Fact]
    public async Task SearchAsync_surfaces_timeout_as_error()
    {
        // Simulate hang-then-cancel by handing back a response only after the test cancels.
        // Using HttpClient.Timeout would require real wall-clock time; instead we cancel
        // via the response delegate to exercise the TaskCanceledException catch.
        using var cts = new CancellationTokenSource();
        var handler = new FakeHttpMessageHandler();
        handler.SetSendCallback((_, ct) =>
        {
            cts.Cancel();
            // Throw the same exception HttpClient would on internal-timeout.
            throw new TaskCanceledException("simulated timeout");
        });

        var backend = new SearXngBackend("http://searxng.local", new HttpClient(handler));
        var result = await backend.SearchAsync("test", 5, CancellationToken.None);

        var error = Assert.IsType<SearchBackendResult.Error>(result);
        Assert.Contains("timed out", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpResponseMessage CreateSuccessResponse()
    {
        const string json = """{"results":[{"title":"Test","url":"https://example.com","content":"snippet"}]}""";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
    }

    private static string LoadFixture(string filename)
    {
        var assembly = typeof(SearXngBackendTests).Assembly;
        var resourceName = $"Netclaw.Search.Tests.Fixtures.{filename}";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Fixture not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();
        private Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>? _callback;

        public int RequestCount { get; private set; }

        public HttpRequestMessage? LastRequest { get; private set; }

        public void Enqueue(HttpResponseMessage response) => _responses.Enqueue(response);

        public void SetSendCallback(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> callback)
            => _callback = callback;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequest = request;

            if (_callback is not null)
                return Task.FromResult(_callback(request, cancellationToken));

            if (_responses.Count == 0)
                throw new InvalidOperationException("No more responses queued.");

            return Task.FromResult(_responses.Dequeue());
        }
    }

}

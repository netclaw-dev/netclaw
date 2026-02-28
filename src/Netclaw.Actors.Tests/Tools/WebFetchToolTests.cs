using System.Text;
using Netclaw.Actors.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public class WebFetchToolTests : IDisposable
{
    private readonly string _tempDir;

    public WebFetchToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-fetch-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void ExtractTextFromHtml_strips_scripts_and_styles()
    {
        var html = """
            <html>
            <head><style>body { color: red; }</style></head>
            <body>
                <script>alert('xss');</script>
                <p>Hello world</p>
            </body>
            </html>
            """;

        var text = WebFetchTool.ExtractTextFromHtml(html);

        Assert.Contains("Hello world", text);
        Assert.DoesNotContain("alert", text);
        Assert.DoesNotContain("color: red", text);
    }

    [Fact]
    public void ExtractTextFromHtml_preserves_paragraph_structure()
    {
        var html = """
            <html><body>
                <h1>Title</h1>
                <p>First paragraph.</p>
                <p>Second paragraph.</p>
            </body></html>
            """;

        var text = WebFetchTool.ExtractTextFromHtml(html);

        Assert.Contains("Title", text);
        Assert.Contains("First paragraph.", text);
        Assert.Contains("Second paragraph.", text);

        // Should have line breaks between elements
        var titleIdx = text.IndexOf("Title", StringComparison.Ordinal);
        var firstIdx = text.IndexOf("First paragraph.", StringComparison.Ordinal);
        Assert.True(firstIdx > titleIdx);
    }

    [Fact]
    public void ExtractTextFromHtml_decodes_html_entities()
    {
        var html = "<html><body><p>Tom &amp; Jerry&#x27;s &lt;adventure&gt;</p></body></html>";

        var text = WebFetchTool.ExtractTextFromHtml(html);

        Assert.Contains("Tom & Jerry's <adventure>", text);
    }

    [Fact]
    public void ExtractTextFromHtml_handles_nested_elements()
    {
        var html = """
            <html><body>
                <div>
                    <p>Outer <strong>bold <em>italic</em></strong> text.</p>
                </div>
            </body></html>
            """;

        var text = WebFetchTool.ExtractTextFromHtml(html);

        Assert.Contains("Outer", text);
        Assert.Contains("bold", text);
        Assert.Contains("italic", text);
        Assert.Contains("text.", text);
    }

    [Fact]
    public void ExtractTextFromHtml_removes_nav_and_footer()
    {
        var html = """
            <html><body>
                <nav><a href="/">Home</a><a href="/about">About</a></nav>
                <main><p>Main content here.</p></main>
                <footer>Copyright 2025</footer>
            </body></html>
            """;

        var text = WebFetchTool.ExtractTextFromHtml(html);

        Assert.Contains("Main content here.", text);
        Assert.DoesNotContain("Copyright 2025", text);
    }

    [Fact]
    public void ExtractTextFromHtml_collapses_whitespace()
    {
        var html = """
            <html><body>
                <p>  lots   of    spaces  </p>
            </body></html>
            """;

        var text = WebFetchTool.ExtractTextFromHtml(html);

        // Should not have excessive blank lines
        Assert.DoesNotContain("\n\n\n", text);
    }

    [Fact]
    public void ExtractTextFromHtml_works_on_ddg_fixture()
    {
        var html = LoadFixture("ddg-lite-akka-dotnet.html");

        var text = WebFetchTool.ExtractTextFromHtml(html);

        // Should contain result text but not raw HTML
        Assert.Contains("Akka.NET", text);
        Assert.DoesNotContain("<td", text);
        Assert.DoesNotContain("class=", text);
    }

    [Fact]
    public void ExtractTextFromHtml_handles_empty_html()
    {
        var text = WebFetchTool.ExtractTextFromHtml("<html><body></body></html>");
        Assert.Equal("", text);
    }

    [Fact]
    public void ExtractTitle_returns_page_title()
    {
        var html = "<html><head><title>My Page Title</title></head><body></body></html>";
        Assert.Equal("My Page Title", WebFetchTool.ExtractTitle(html));
    }

    [Fact]
    public void ExtractTitle_returns_null_when_missing()
    {
        var html = "<html><head></head><body></body></html>";
        Assert.Null(WebFetchTool.ExtractTitle(html));
    }

    [Fact]
    public void ExtractTitle_decodes_entities()
    {
        var html = "<html><head><title>Tom &amp; Jerry</title></head><body></body></html>";
        Assert.Equal("Tom & Jerry", WebFetchTool.ExtractTitle(html));
    }

    [Fact]
    public void ExtractTitle_from_ddg_fixture()
    {
        var html = LoadFixture("ddg-lite-akka-dotnet.html");
        var title = WebFetchTool.ExtractTitle(html);

        Assert.NotNull(title);
        Assert.Contains("DuckDuckGo", title);
    }

    [Fact]
    public void SanitizeForFilename_replaces_special_chars()
    {
        var uri = new Uri("https://example.com/path/to/page?q=hello");
        var result = WebFetchTool.SanitizeForFilename(uri);

        Assert.DoesNotContain("/", result);
        Assert.DoesNotContain("?", result);
        Assert.DoesNotContain(":", result);
        Assert.Contains("example_com", result);
    }

    [Fact]
    public void SanitizeForFilename_limits_length()
    {
        var uri = new Uri("https://example.com/" + new string('a', 200));
        var result = WebFetchTool.SanitizeForFilename(uri);

        Assert.True(result.Length <= 60);
    }

    [Fact]
    public async Task ExecuteAsync_saves_html_to_file_and_returns_summary()
    {
        var html = """
            <html>
            <head><title>Test Page</title></head>
            <body>
                <h1>Welcome</h1>
                <p>This is the first paragraph of content.</p>
                <p>This is the second paragraph with more details.</p>
            </body>
            </html>
            """;

        var handler = new FakeHttpHandler(html, "text/html");
        var httpClient = new HttpClient(handler);
        var tool = new WebFetchTool(httpClient, _tempDir);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Url"] = "https://example.com/test" },
            CancellationToken.None);

        // Should contain summary info
        Assert.Contains("Fetched: https://example.com/test", result);
        Assert.Contains("Title: Test Page", result);
        Assert.Contains("Saved to:", result);
        Assert.Contains(".txt", result);
        Assert.Contains("Preview", result);
        Assert.Contains("Welcome", result);

        // File should exist on disk
        var files = Directory.GetFiles(_tempDir, "*.txt");
        Assert.Single(files);

        // File content should be the extracted text
        var fileContent = await File.ReadAllTextAsync(files[0]);
        Assert.Contains("Welcome", fileContent);
        Assert.Contains("first paragraph", fileContent);
        Assert.DoesNotContain("<html>", fileContent);
    }

    [Fact]
    public async Task ExecuteAsync_saves_plain_text_as_is()
    {
        var json = """{"name": "test", "value": 42}""";

        var handler = new FakeHttpHandler(json, "application/json");
        var httpClient = new HttpClient(handler);
        var tool = new WebFetchTool(httpClient, _tempDir);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Url"] = "https://api.example.com/data" },
            CancellationToken.None);

        Assert.Contains("Saved to:", result);

        var files = Directory.GetFiles(_tempDir, "*.txt");
        Assert.Single(files);

        var fileContent = await File.ReadAllTextAsync(files[0]);
        Assert.Contains("\"name\": \"test\"", fileContent);
    }

    [Fact]
    public async Task ExecuteAsync_rejects_invalid_url()
    {
        var tool = new WebFetchTool(fetchDirectory: _tempDir);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Url"] = "not-a-url" },
            CancellationToken.None);

        Assert.Contains("Error", result);
        Assert.Contains("Invalid URL", result);
    }

    [Fact]
    public async Task ExecuteAsync_rejects_ftp_url()
    {
        var tool = new WebFetchTool(fetchDirectory: _tempDir);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Url"] = "ftp://files.example.com/doc.txt" },
            CancellationToken.None);

        Assert.Contains("Error", result);
    }

    private static string LoadFixture(string filename)
    {
        var assembly = typeof(WebFetchToolTests).Assembly;
        var resourceName = $"Netclaw.Actors.Tests.Tools.Fixtures.{filename}";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Fixture not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Fake HTTP handler that returns a canned response.
    /// </summary>
    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly string _content;
        private readonly string _contentType;

        public FakeHttpHandler(string content, string contentType)
        {
            _content = content;
            _contentType = contentType;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(_content, Encoding.UTF8, new System.Net.Http.Headers.MediaTypeHeaderValue(_contentType))
            };
            return Task.FromResult(response);
        }
    }
}

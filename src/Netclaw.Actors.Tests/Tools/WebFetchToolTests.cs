using System.Text;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
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
    public void SanitizeHtml_removes_scripts_preserves_structure()
    {
        var html = """
            <html><body>
                <script>alert('xss');</script>
                <style>body { color: red; }</style>
                <nav><a href="/">Home</a></nav>
                <p>Content with <a href="/link">a link</a>.</p>
                <img src="photo.jpg" alt="Photo" />
                <footer>Copyright</footer>
            </body></html>
            """;

        var result = WebFetchTool.SanitizeHtml(html);

        Assert.DoesNotContain("<script>", result);
        Assert.DoesNotContain("alert", result);
        Assert.DoesNotContain("<style>", result);
        Assert.DoesNotContain("color: red", result);
        Assert.Contains("<nav>", result);
        Assert.Contains("<a href=", result);
        Assert.Contains("<img src=", result);
        Assert.Contains("<footer>", result);
    }

    [Fact]
    public void ExtractMetadataSummary_extracts_description_and_headings()
    {
        var html = """
            <html>
            <head>
                <meta name="description" content="A test page about things." />
            </head>
            <body>
                <h1>Main Title</h1>
                <h2>Section One</h2>
                <h3>Subsection</h3>
            </body>
            </html>
            """;

        var result = WebFetchTool.ExtractMetadataSummary(html);

        Assert.Contains("Description: A test page about things.", result);
        Assert.Contains("H1: Main Title", result);
        Assert.Contains("H2: Section One", result);
        Assert.Contains("H3: Subsection", result);
    }

    [Fact]
    public void ExtractMetadataSummary_handles_no_metadata()
    {
        var html = "<html><body><p>Just text.</p></body></html>";

        var result = WebFetchTool.ExtractMetadataSummary(html);

        Assert.Equal("", result);
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
                <script>alert('xss');</script>
            </body>
            </html>
            """;

        var handler = new FakeHttpHandler(html, "text/html");
        var httpClient = new HttpClient(handler);
        var tool = new WebFetchTool(httpClient: httpClient, fetchDirectory: _tempDir);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Url"] = "https://example.com/test" },
            CancellationToken.None);

        // Should contain summary info
        Assert.Contains("Fetched: https://example.com/test", result);
        Assert.Contains("Title: Test Page", result);
        Assert.Contains("Saved to:", result);
        Assert.Contains(".html", result);
        Assert.Contains("Preview", result);

        // File should exist on disk as .html (raw mode is default)
        var files = Directory.GetFiles(_tempDir, "*.html");
        Assert.Single(files);

        // File content should preserve HTML structure but strip scripts
        var fileContent = await File.ReadAllTextAsync(files[0], TestContext.Current.CancellationToken);
        Assert.Contains("<h1>Welcome</h1>", fileContent);
        Assert.Contains("<p>", fileContent);
        Assert.DoesNotContain("<script>", fileContent);
        Assert.DoesNotContain("alert", fileContent);
    }

    [Fact]
    public async Task ExecuteAsync_text_mode_saves_extracted_text()
    {
        var html = """
            <html>
            <head><title>Test Page</title></head>
            <body>
                <h1>Welcome</h1>
                <p>This is the first paragraph of content.</p>
            </body>
            </html>
            """;

        var handler = new FakeHttpHandler(html, "text/html");
        var httpClient = new HttpClient(handler);
        var tool = new WebFetchTool(httpClient: httpClient, fetchDirectory: _tempDir);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Url"] = "https://example.com/test", ["Format"] = "text" },
            CancellationToken.None);

        Assert.Contains(".txt", result);

        var files = Directory.GetFiles(_tempDir, "*.txt");
        Assert.Single(files);

        var fileContent = await File.ReadAllTextAsync(files[0], TestContext.Current.CancellationToken);
        Assert.Contains("Welcome", fileContent);
        Assert.Contains("first paragraph", fileContent);
        Assert.DoesNotContain("<html>", fileContent);
    }

    [Fact]
    public async Task ExecuteAsync_raw_mode_preserves_links_and_images()
    {
        var html = """
            <html><body>
                <nav><a href="/home">Home</a></nav>
                <p>Check out <a href="https://example.com">this link</a>.</p>
                <img src="https://example.com/photo.jpg" alt="Photo" />
                <footer>Copyright 2025</footer>
            </body></html>
            """;

        var handler = new FakeHttpHandler(html, "text/html");
        var httpClient = new HttpClient(handler);
        var tool = new WebFetchTool(httpClient: httpClient, fetchDirectory: _tempDir);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Url"] = "https://example.com/page" },
            CancellationToken.None);

        var files = Directory.GetFiles(_tempDir, "*.html");
        Assert.Single(files);

        var fileContent = await File.ReadAllTextAsync(files[0], TestContext.Current.CancellationToken);
        Assert.Contains("<a href=", fileContent);
        Assert.Contains("<img src=", fileContent);
        Assert.Contains("<nav>", fileContent);
        Assert.Contains("<footer>", fileContent);
    }

    [Fact]
    public async Task ExecuteAsync_saves_json_with_json_extension()
    {
        var json = """{"name": "test", "value": 42}""";

        var handler = new FakeHttpHandler(json, "application/json");
        var httpClient = new HttpClient(handler);
        var tool = new WebFetchTool(httpClient: httpClient, fetchDirectory: _tempDir);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Url"] = "https://api.example.com/data.json" },
            CancellationToken.None);

        Assert.Contains("Saved to:", result);

        var files = Directory.GetFiles(_tempDir, "*.json");
        Assert.Single(files);

        var fileContent = await File.ReadAllTextAsync(files[0], TestContext.Current.CancellationToken);
        Assert.Contains("\"name\": \"test\"", fileContent);
    }

    [Fact]
    public async Task ExecuteAsync_saves_json_without_url_extension_uses_content_type()
    {
        var json = """{"name": "test"}""";

        var handler = new FakeHttpHandler(json, "application/json");
        var httpClient = new HttpClient(handler);
        var tool = new WebFetchTool(httpClient: httpClient, fetchDirectory: _tempDir);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Url"] = "https://api.example.com/data" },
            CancellationToken.None);

        Assert.Contains("Saved to:", result);

        // Should use .json from Content-Type fallback, not .txt
        var files = Directory.GetFiles(_tempDir, "*.json");
        Assert.Single(files);
    }

    [Fact]
    public async Task ExecuteAsync_preserves_shell_script_extension()
    {
        var script = "#!/bin/bash\necho 'hello world'";

        var handler = new FakeHttpHandler(script, "text/plain");
        var httpClient = new HttpClient(handler);
        var tool = new WebFetchTool(httpClient: httpClient, fetchDirectory: _tempDir);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Url"] = "https://example.com/install.sh" },
            CancellationToken.None);

        Assert.Contains("Saved to:", result);
        Assert.Contains(".sh", result);

        var files = Directory.GetFiles(_tempDir, "*.sh");
        Assert.Single(files);

        var fileContent = await File.ReadAllTextAsync(files[0], TestContext.Current.CancellationToken);
        Assert.Contains("echo 'hello world'", fileContent);
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
    public async Task ExecuteAsync_rejects_http_when_https_required()
    {
        var tool = new WebFetchTool(fetchDirectory: _tempDir);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Url"] = "http://example.com/page" },
            CancellationToken.None);

        Assert.Contains("HTTPS is required", result);
        Assert.Contains("Tools.WebFetch.RequireHttps", result);
    }

    [Fact]
    public async Task ExecuteAsync_allows_http_when_not_required()
    {
        var config = new ToolConfig { WebFetch = new WebFetchConfig { RequireHttps = false } };
        var handler = new FakeHttpHandler("<html><body><p>OK</p></body></html>", "text/html");
        var httpClient = new HttpClient(handler);
        var tool = new WebFetchTool(config, httpClient, _tempDir);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Url"] = "http://example.com/page" },
            CancellationToken.None);

        Assert.Contains("Fetched:", result);
        Assert.DoesNotContain("Error", result);
    }

    [Fact]
    public async Task ExecuteAsync_allows_http_localhost_by_default()
    {
        var handler = new FakeHttpHandler("<html><body><p>Local</p></body></html>", "text/html");
        var httpClient = new HttpClient(handler);
        var tool = new WebFetchTool(httpClient: httpClient, fetchDirectory: _tempDir);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Url"] = "http://localhost:8080/api" },
            CancellationToken.None);

        Assert.Contains("Fetched:", result);
        Assert.DoesNotContain("Error", result);
    }

    [Fact]
    public async Task ExecuteAsync_allows_http_127_0_0_1_by_default()
    {
        var handler = new FakeHttpHandler("<html><body><p>Loopback</p></body></html>", "text/html");
        var httpClient = new HttpClient(handler);
        var tool = new WebFetchTool(httpClient: httpClient, fetchDirectory: _tempDir);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Url"] = "http://127.0.0.1:3000/" },
            CancellationToken.None);

        Assert.Contains("Fetched:", result);
        Assert.DoesNotContain("Error", result);
    }

    [Fact]
    public async Task ExecuteAsync_allows_http_ipv6_loopback_by_default()
    {
        var handler = new FakeHttpHandler("<html><body><p>IPv6</p></body></html>", "text/html");
        var httpClient = new HttpClient(handler);
        var tool = new WebFetchTool(httpClient: httpClient, fetchDirectory: _tempDir);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Url"] = "http://[::1]:5000/" },
            CancellationToken.None);

        Assert.Contains("Fetched:", result);
        Assert.DoesNotContain("Error", result);
    }

    [Fact]
    public async Task ExecuteAsync_rejects_http_localhost_when_not_in_allow_list()
    {
        var config = new ToolConfig { WebFetch = new WebFetchConfig { HttpAllowList = [] } };
        var tool = new WebFetchTool(config, fetchDirectory: _tempDir);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Url"] = "http://localhost:8080/" },
            CancellationToken.None);

        Assert.Contains("HTTPS is required", result);
    }

    [Fact]
    public async Task ExecuteAsync_allows_http_for_custom_allow_list_host()
    {
        var config = new ToolConfig { WebFetch = new WebFetchConfig { HttpAllowList = ["internal.corp"] } };
        var handler = new FakeHttpHandler("<html><body><p>Internal</p></body></html>", "text/html");
        var httpClient = new HttpClient(handler);
        var tool = new WebFetchTool(config, httpClient, _tempDir);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Url"] = "http://internal.corp/api" },
            CancellationToken.None);

        Assert.Contains("Fetched:", result);
        Assert.DoesNotContain("Error", result);
    }

    [Fact]
    public async Task ExecuteAsync_saves_binary_image_with_correct_extension_and_bytes()
    {
        var imageBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xFF, 0xFE, 0x00, 0x01 };

        var handler = new FakeHttpHandler(imageBytes, "image/png");
        var httpClient = new HttpClient(handler);
        var tool = new WebFetchTool(httpClient: httpClient, fetchDirectory: _tempDir);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Url"] = "https://example.com/photo.png" },
            CancellationToken.None);

        Assert.Contains("Fetched:", result);
        Assert.Contains(".png", result);
        Assert.Contains("Content-Type: image/png", result);
        Assert.Contains("binary file", result);
        Assert.Contains("attach_file", result);

        var files = Directory.GetFiles(_tempDir, "*.png");
        Assert.Single(files);

        // Verify byte-perfect round-trip
        var savedBytes = await File.ReadAllBytesAsync(files[0], TestContext.Current.CancellationToken);
        Assert.Equal(imageBytes, savedBytes);
    }

    [Fact]
    public async Task ExecuteAsync_saves_pdf_with_correct_extension()
    {
        // PDF magic bytes
        var pdfBytes = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34 };

        var handler = new FakeHttpHandler(pdfBytes, "application/pdf");
        var httpClient = new HttpClient(handler);
        var tool = new WebFetchTool(httpClient: httpClient, fetchDirectory: _tempDir);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Url"] = "https://arxiv.org/pdf/2603.25414" },
            CancellationToken.None);

        Assert.Contains("Content-Type: application/pdf", result);

        // URL has no extension, should fall back to .pdf from Content-Type
        var files = Directory.GetFiles(_tempDir, "*.pdf");
        Assert.Single(files);

        var savedBytes = await File.ReadAllBytesAsync(files[0], TestContext.Current.CancellationToken);
        Assert.Equal(pdfBytes, savedBytes);
    }

    [Fact]
    public async Task ExecuteAsync_saves_binary_with_url_extension_over_fallback()
    {
        var bytes = new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }; // GIF89a

        var handler = new FakeHttpHandler(bytes, "image/gif");
        var httpClient = new HttpClient(handler);
        var tool = new WebFetchTool(httpClient: httpClient, fetchDirectory: _tempDir);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Url"] = "https://example.com/animation.gif" },
            CancellationToken.None);

        var files = Directory.GetFiles(_tempDir, "*.gif");
        Assert.Single(files);
    }

    [Fact]
    public async Task ExecuteAsync_binary_unknown_type_no_url_extension_saves_as_bin()
    {
        var bytes = new byte[] { 0x00, 0x01, 0x02, 0x03 };

        var handler = new FakeHttpHandler(bytes, "application/octet-stream");
        var httpClient = new HttpClient(handler);
        var tool = new WebFetchTool(httpClient: httpClient, fetchDirectory: _tempDir);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Url"] = "https://example.com/api/download" },
            CancellationToken.None);

        var files = Directory.GetFiles(_tempDir, "*.bin");
        Assert.Single(files);
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

    [Theory]
    [InlineData("image/png", true)]
    [InlineData("image/jpeg", true)]
    [InlineData("image/webp", true)]
    [InlineData("audio/mpeg", true)]
    [InlineData("video/mp4", true)]
    [InlineData("application/pdf", true)]
    [InlineData("application/zip", true)]
    [InlineData("application/octet-stream", true)]
    [InlineData("text/html", false)]
    [InlineData("text/plain", false)]
    [InlineData("application/json", false)]
    [InlineData("", false)]
    public void IsBinaryContentType_classifies_correctly(string contentType, bool expected)
    {
        Assert.Equal(expected, WebFetchTool.IsBinaryContentType(contentType));
    }

    [Theory]
    [InlineData("https://example.com/photo.png", ".png")]
    [InlineData("https://example.com/install.sh", ".sh")]
    [InlineData("https://example.com/data.json", ".json")]
    [InlineData("https://example.com/api/data", null)]
    [InlineData("https://example.com/", null)]
    [InlineData("https://arxiv.org/pdf/2603.25414", null)] // numeric-only = not a real extension
    public void GetExtensionFromUrl_extracts_extension(string url, string? expected)
    {
        var uri = new Uri(url);
        Assert.Equal(expected, WebFetchTool.GetExtensionFromUrl(uri));
    }

    [Theory]
    [InlineData("application/pdf", true, ".pdf")]
    [InlineData("application/json", false, ".json")]
    [InlineData("text/csv", false, ".csv")]
    [InlineData("image/png", true, ".bin")]
    [InlineData("text/plain", false, ".txt")]
    public void GetFallbackExtension_returns_correct_extension(string contentType, bool isBinary, string expected)
    {
        Assert.Equal(expected, WebFetchTool.GetFallbackExtension(contentType, isBinary));
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
    /// Fake HTTP handler that returns a canned response (text or binary).
    /// </summary>
    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly byte[] _bytes;
        private readonly string _contentType;

        public FakeHttpHandler(string content, string contentType)
        {
            _bytes = Encoding.UTF8.GetBytes(content);
            _contentType = contentType;
        }

        public FakeHttpHandler(byte[] bytes, string contentType)
        {
            _bytes = bytes;
            _contentType = contentType;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var content = new ByteArrayContent(_bytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(_contentType);
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = content
            };
            return Task.FromResult(response);
        }
    }
}

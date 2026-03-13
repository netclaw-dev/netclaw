using System.Net;
using System.Text;
using System.Text.Json;
using Netclaw.Configuration.Providers;
using Xunit;

namespace Netclaw.Configuration.Tests.Providers;

public class ProbeHelpersTests
{
    // ── FailForStatus ──

    [Fact]
    public void FailForStatus_Forbidden_WithDetail_IncludesDetail()
    {
        var result = ProbeHelpers.FailForStatus(
            HttpStatusCode.Forbidden, "openai", "Insufficient permissions for model listing.");

        Assert.False(result.Success);
        Assert.Contains("Access denied by openai", result.ErrorMessage);
        Assert.Contains("Insufficient permissions for model listing.", result.ErrorMessage);
    }

    [Fact]
    public void FailForStatus_Forbidden_WithoutDetail_ShowsGenericMessage()
    {
        var result = ProbeHelpers.FailForStatus(HttpStatusCode.Forbidden, "openai");

        Assert.False(result.Success);
        Assert.Contains("credentials may lack model-listing permissions", result.ErrorMessage);
    }

    [Fact]
    public void FailForStatus_Unauthorized_WithDetail_IncludesDetail()
    {
        var result = ProbeHelpers.FailForStatus(
            HttpStatusCode.Unauthorized, "openai", "Invalid API key provided.");

        Assert.False(result.Success);
        Assert.Contains("Invalid credentials for openai", result.ErrorMessage);
        Assert.Contains("Invalid API key provided.", result.ErrorMessage);
    }

    [Fact]
    public void FailForStatus_Unauthorized_SaysCredentials_NotApiKey()
    {
        var result = ProbeHelpers.FailForStatus(HttpStatusCode.Unauthorized, "openai");

        Assert.False(result.Success);
        Assert.Contains("credentials", result.ErrorMessage);
        Assert.DoesNotContain("API key", result.ErrorMessage);
    }

    [Fact]
    public void FailForStatus_Forbidden_SaysCredentials_NotApiKey()
    {
        var result = ProbeHelpers.FailForStatus(HttpStatusCode.Forbidden, "openai");

        Assert.False(result.Success);
        Assert.Contains("credentials", result.ErrorMessage);
        Assert.DoesNotContain("API key", result.ErrorMessage);
    }

    // ── ExtractApiErrorDetailAsync ──

    [Fact]
    public async Task ExtractApiErrorDetail_NestedErrorObject_ExtractsMessage()
    {
        var response = JsonErrorResponse(new
        {
            error = new { message = "You have insufficient permissions.", type = "permission_error" }
        });

        var detail = await ProbeHelpers.ExtractApiErrorDetailAsync(response, CancellationToken.None);

        Assert.Equal("You have insufficient permissions.", detail);
    }

    [Fact]
    public async Task ExtractApiErrorDetail_StringError_ExtractsString()
    {
        var response = JsonErrorResponse(new { error = "invalid_token" });

        var detail = await ProbeHelpers.ExtractApiErrorDetailAsync(response, CancellationToken.None);

        Assert.Equal("invalid_token", detail);
    }

    [Fact]
    public async Task ExtractApiErrorDetail_EmptyBody_ReturnsNull()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("", Encoding.UTF8, "application/json")
        };

        var detail = await ProbeHelpers.ExtractApiErrorDetailAsync(response, CancellationToken.None);

        Assert.Null(detail);
    }

    [Fact]
    public async Task ExtractApiErrorDetail_InvalidJson_ReturnsNull()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("not json at all", Encoding.UTF8, "text/plain")
        };

        var detail = await ProbeHelpers.ExtractApiErrorDetailAsync(response, CancellationToken.None);

        Assert.Null(detail);
    }

    [Fact]
    public async Task ExtractApiErrorDetail_NoErrorProperty_ReturnsNull()
    {
        var response = JsonErrorResponse(new { status = "error", reason = "unknown" });

        var detail = await ProbeHelpers.ExtractApiErrorDetailAsync(response, CancellationToken.None);

        Assert.Null(detail);
    }

    // ── ExecuteProbeAsync integration with error detail ──

    [Fact]
    public async Task ExecuteProbeAsync_403WithJsonError_IncludesErrorDetailInResult()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonErrorResponse(
            new { error = new { message = "Token lacks model-listing scope." } },
            HttpStatusCode.Forbidden));

        var httpClient = new HttpClient(handler);
        var result = await ProbeHelpers.ExecuteProbeAsync(
            httpClient, "openai", "https://api.example.com", "/v1/models",
            null, _ => { }, _ => throw new InvalidOperationException("Should not parse on error"),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Access denied by openai", result.ErrorMessage);
        Assert.Contains("Token lacks model-listing scope.", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteProbeAsync_401WithStringError_IncludesErrorDetailInResult()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonErrorResponse(
            new { error = "invalid_api_key" },
            HttpStatusCode.Unauthorized));

        var httpClient = new HttpClient(handler);
        var result = await ProbeHelpers.ExecuteProbeAsync(
            httpClient, "openai", "https://api.example.com", "/v1/models",
            null, _ => { }, _ => throw new InvalidOperationException("Should not parse on error"),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Invalid credentials for openai", result.ErrorMessage);
        Assert.Contains("invalid_api_key", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteProbeAsync_403WithInvalidJsonBody_FallsBackToGenericMessage()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("<html>Forbidden</html>", Encoding.UTF8, "text/html")
        });

        var httpClient = new HttpClient(handler);
        var result = await ProbeHelpers.ExecuteProbeAsync(
            httpClient, "openai", "https://api.example.com", "/v1/models",
            null, _ => { }, _ => throw new InvalidOperationException("Should not parse on error"),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("credentials may lack model-listing permissions", result.ErrorMessage);
    }

    // ── Helpers ──

    private static HttpResponseMessage JsonErrorResponse(
        object body, HttpStatusCode status = HttpStatusCode.Forbidden)
    {
        var json = JsonSerializer.Serialize(body);
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_handler(request));
        }
    }
}

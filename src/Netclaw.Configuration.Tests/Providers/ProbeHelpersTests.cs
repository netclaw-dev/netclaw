// -----------------------------------------------------------------------
// <copyright file="ProbeHelpersTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text;
using System.Text.Json;
using Netclaw.Providers;
using Netclaw.Providers.SelfHosted;
using Netclaw.Tests.Utilities;
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

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void FailForStatus_SaysCredentials_NotApiKey(HttpStatusCode status)
    {
        var result = ProbeHelpers.FailForStatus(status, "openai");

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
        // Body has JSON but no "error" key
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

    // ── ParseOpenAiStyleModels: never invent modalities (#1290) ──

    [Fact]
    public void ParseOpenAiStyleModels_OmitsModalities_LeavesThemUnset()
    {
        // An OpenAI-style /v1/models listing carries no modality field. Discovery must
        // report "unknown" (null), NOT default to Text — a persisted Text override would
        // permanently demote a multimodal self-hosted model to text-only.
        var result = ProbeHelpers.ParseOpenAiStyleModels("""{"data":[{"id":"my-model"}]}""");

        Assert.True(result.Success);
        var model = Assert.Single(result.Models);
        Assert.Equal("my-model", model.ModelId.Value);
        Assert.Null(model.InputModalities);
        Assert.Null(model.OutputModalities);
    }

    [Fact]
    public void OpenAiCompatible_ParseModels_ReadsContextWindowButNotModalities()
    {
        var result = OpenAiCompatibleDescriptor.ParseModels(
            """{"data":[{"id":"qwen-vl","max_model_len":32768}]}""");

        var model = Assert.Single(result.Models);
        Assert.Equal(32768, model.ContextWindowTokens);
        Assert.Null(model.InputModalities);
        Assert.Null(model.OutputModalities);
    }

    // ── ExecuteProbeAsync: one caller-supplied timeout, honest message (#1292) ──

    [Fact]
    public async Task ExecuteProbeAsync_WhenServerDoesNotRespond_TimesOutNamingTheEndpoint()
    {
        // The server accepts the connection but never answers. The probe must honor the
        // supplied timeout (not a buried 10s constant) and report the actual endpoint so
        // a wrong/blank target is visible rather than failing anonymously.
        var httpClient = new HttpClient(new HangingHandler());

        var result = await ProbeHelpers.ExecuteProbeAsync(
            httpClient, "openai-compatible", "http://localhost:11434", "/v1/models",
            "http://my-vllm:8000", _ => { }, ProbeHelpers.ParseOpenAiStyleModels,
            CancellationToken.None, TimeSpan.FromMilliseconds(150));

        Assert.False(result.Success);
        Assert.Contains("No response from http://my-vllm:8000", result.ErrorMessage);
        Assert.Contains("try again", result.ErrorMessage);
        Assert.DoesNotContain("10 seconds", result.ErrorMessage!);
    }

    // ── Helpers ──

    /// <summary>
    /// Accepts the request but never responds until the probe's own timeout cancels the
    /// token. The cancellation IS the synchronization signal — there is no arbitrary
    /// delay — so the test is deterministic, not timing-dependent.
    /// </summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<HttpResponseMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return tcs.Task;
        }
    }

    private static HttpResponseMessage JsonErrorResponse(
        object body, HttpStatusCode status = HttpStatusCode.Forbidden)
    {
        var json = JsonSerializer.Serialize(body);
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

}

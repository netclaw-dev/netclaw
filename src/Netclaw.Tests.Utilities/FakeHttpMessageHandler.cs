// -----------------------------------------------------------------------
// <copyright file="FakeHttpMessageHandler.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text;
using System.Text.Json;

namespace Netclaw.Tests.Utilities;

internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private const string JsonMediaType = "application/json";

    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = [];
    private readonly Func<HttpRequestMessage, HttpResponseMessage>? _catchAll;

    public FakeHttpMessageHandler() { }

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        => _catchAll = handler;

    public void AddJsonResponse<T>(string url, T body)
        => AddResponse(url, HttpStatusCode.OK, JsonSerializer.Serialize(body), JsonMediaType);

    public void AddStringResponse(string url, string content, string contentType = "text/plain")
        => _routes[url] = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, contentType)
        };

    public void AddByteResponse(string url, byte[] content, string contentType = "application/octet-stream")
        => _routes[url] = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content)
            {
                Headers = { ContentType = new(contentType) }
            }
        };

    public void AddResponse(string url, HttpStatusCode status, string content, string contentType)
        => _routes[url] = _ => new HttpResponseMessage(status)
        {
            Content = new StringContent(content, Encoding.UTF8, contentType)
        };

    public void AddErrorResponse(string url, HttpStatusCode status)
        => _routes[url] = _ => new HttpResponseMessage(status);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var url = request.RequestUri!.ToString();
        if (_routes.TryGetValue(url, out var handler))
            return Task.FromResult(handler(request));
        if (_catchAll != null)
            return Task.FromResult(_catchAll(request));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    public static HttpResponseMessage JsonResponse<T>(T body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, JsonMediaType) };
}

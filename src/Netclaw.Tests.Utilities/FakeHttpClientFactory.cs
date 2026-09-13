// -----------------------------------------------------------------------
// <copyright file="FakeHttpClientFactory.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Tests.Utilities;

internal sealed class FakeHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> handler) : IHttpClientFactory
{
    public string? LastClientName { get; private set; }

    public HttpClient CreateClient(string name)
    {
        LastClientName = name;
        return new HttpClient(new FakeHttpMessageHandler(handler));
    }
}

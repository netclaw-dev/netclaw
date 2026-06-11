// -----------------------------------------------------------------------
// <copyright file="FakeHttpClientFactory.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Tests.Channels.TestHelpers;

/// <summary>
/// Hands out plain <see cref="HttpClient"/> instances for channels that
/// require an <see cref="IHttpClientFactory"/> but never make HTTP calls
/// in the test under exercise.
/// </summary>
internal sealed class FakeHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}

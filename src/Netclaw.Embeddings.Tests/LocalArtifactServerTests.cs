// -----------------------------------------------------------------------
// <copyright file="LocalArtifactServerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Netclaw.Embeddings.Tests;

public sealed class LocalArtifactServerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_completes_the_accept_loop_and_releases_the_port(bool requestBeforeClose)
    {
        await using var server = new LocalArtifactServer();
        var port = server.Port;

        if (requestBeforeClose)
        {
            byte[] expected = [1, 2, 3, 4];
            var uri = server.AddRoute("/artifact", expected);
            using var client = new HttpClient();
            var actual = await client.GetByteArrayAsync(uri, TestContext.Current.CancellationToken);
            Assert.Equal(expected, actual);
        }

        var firstDisposal = server.DisposeAsync().AsTask();
        var secondDisposal = server.DisposeAsync().AsTask();
        await Task.WhenAll(firstDisposal, secondDisposal).WaitAsync(TestContext.Current.CancellationToken);

        using var replacement = new TcpListener(IPAddress.Loopback, port);
        replacement.Start();
        Assert.Equal(port, ((IPEndPoint)replacement.LocalEndpoint).Port);
    }
}

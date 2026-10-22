// -----------------------------------------------------------------------
// <copyright file="LocalControlRequestsMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration.Secrets;
using Netclaw.Daemon.Security;
using Xunit;

namespace Netclaw.Actors.MutationTests;

public sealed class LocalControlRequestsMutationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly LocalControlPairingProofProtector _protector = new(new EphemeralDataProtectionProvider());

    [Fact]
    public async Task Invalid_proof_is_refused()
    {
        var (request, refusal) = await AuthorizeAsync(CreateRequests(), "not-a-proof");

        Assert.Null(request);
        Assert.NotNull(refusal);
    }

    [Fact]
    public async Task Valid_proof_is_accepted_after_a_flood_of_refused_requests()
    {
        // A tunnel agent forwards remote junk from loopback, the address of the host CLI.
        var requests = CreateRequests();
        for (var index = 0; index < LocalControlRequests.RefusalsPerWindow * 3; index++)
            await AuthorizeAsync(requests, "not-a-proof");

        var (request, refusal) = await AuthorizeAsync(
            requests,
            _protector.CreateProof(LocalControlOperation.GeneratePairingCode, Now));

        Assert.NotNull(request);
        Assert.Null(refusal);
    }

    private LocalControlRequests CreateRequests()
    {
        var time = new FixedTimeProvider(Now);
        return new LocalControlRequests(
            new LocalControlPairingProofValidator(_protector, time, NullLogger<LocalControlPairingProofValidator>.Instance),
            time);
    }

    private static Task<(LocalControlPairingCodeRequest? Request, IResult? Refusal)> AuthorizeAsync(
        LocalControlRequests requests,
        string proof)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = IPAddress.Loopback;
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes($"{{\"proof\":\"{proof}\"}}"));
        return requests.AuthorizeAsync(
            httpContext,
            PairingEndpointJsonContext.Default.LocalControlPairingCodeRequest,
            CancellationToken.None);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

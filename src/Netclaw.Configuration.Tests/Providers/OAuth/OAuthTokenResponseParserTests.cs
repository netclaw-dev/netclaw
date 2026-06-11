// -----------------------------------------------------------------------
// <copyright file="OAuthTokenResponseParserTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Providers.OAuth;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Configuration.Tests.Providers.OAuth;

public sealed class OAuthTokenResponseParserTests
{
    private static readonly FakeTimeProvider Clock =
        new(DateTimeOffset.Parse("2026-06-01T00:00:00+00:00"));

    private static OAuthDeviceFlowResult Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return OAuthTokenResponseParser.Parse(doc.RootElement, Clock);
    }

    [Fact]
    public void Parse_ExpiresInNumber_SetsExpiry()
    {
        var result = Parse("""{ "access_token": "at", "expires_in": 3600 }""");

        Assert.Equal(Clock.GetUtcNow().AddSeconds(3600), result.ExpiresAt);
    }

    [Fact]
    public void Parse_ExpiresInString_SetsExpiry()
    {
        var result = Parse("""{ "access_token": "at", "expires_in": "3600" }""");

        Assert.Equal(Clock.GetUtcNow().AddSeconds(3600), result.ExpiresAt);
    }

    [Fact]
    public void Parse_ExpiresInNull_TreatedAsNoExpiry()
    {
        var result = Parse("""{ "access_token": "at", "expires_in": null }""");

        Assert.Null(result.ExpiresAt);
    }

    [Fact]
    public void Parse_ExpiresInMissing_TreatedAsNoExpiry()
    {
        var result = Parse("""{ "access_token": "at" }""");

        Assert.Null(result.ExpiresAt);
    }

    [Fact]
    public void Parse_ExpiresInAbsurdlyLarge_ClampsInsteadOfThrowing()
    {
        // A misbehaving/compromised endpoint can send a value that would overflow
        // DateTimeOffset.AddSeconds — must clamp, not throw.
        var result = Parse("""{ "access_token": "at", "expires_in": 1e300 }""");

        Assert.Equal(DateTimeOffset.MaxValue, result.ExpiresAt);
    }

    [Fact]
    public void Parse_MissingAccessToken_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => Parse("""{ "expires_in": 3600 }"""));
    }

    [Fact]
    public void Parse_ExtractsAccountIdFromNestedOpenAiClaimInIdToken()
    {
        var idToken = JwtTestToken.MakeFromPayloadJson("""{ "https://api.openai.com/auth": { "chatgpt_account_id": "acct-nested" } }""");
        var result = Parse($$"""{ "access_token": "at", "id_token": "{{idToken}}" }""");

        Assert.Equal("acct-nested", result.AccountId!.Value);
    }

    [Fact]
    public void Parse_ExtractsTopLevelAccountId()
    {
        var result = Parse("""{ "access_token": "at", "account_id": "acct-top" }""");

        Assert.Equal("acct-top", result.AccountId!.Value);
    }

    [Fact]
    public void Parse_ExtractsNumericAccountIdAsInvariantString()
    {
        var result = Parse("""{ "access_token": "at", "account_id": 12345 }""");

        Assert.Equal("12345", result.AccountId!.Value);
    }
}

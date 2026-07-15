// -----------------------------------------------------------------------
// <copyright file="WebhookRouteStoreTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class WebhookRouteStoreTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public WebhookRouteStoreTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        _dir.Dispose();
    }

    [Theory]
    [InlineData("github-issues")]
    [InlineData("x")]
    [InlineData("route-2")]
    public void TryNormalizeRouteName_AcceptsValidKebabCase(string value)
    {
        var ok = WebhookRouteStore.TryNormalizeRouteName(value, out var normalized, out var error);

        Assert.True(ok);
        Assert.Equal(value, normalized);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("../secrets")]
    [InlineData("foo/bar")]
    [InlineData("foo\\bar")]
    [InlineData("/tmp/evil")]
    [InlineData("foo bar")]
    [InlineData("foo_bar")]
    [InlineData("foo..bar")]
    [InlineData("-foo")]
    [InlineData("foo-")]
    [InlineData("foo--bar")]
    public void TryNormalizeRouteName_RejectsInvalidNames(string value)
    {
        var ok = WebhookRouteStore.TryNormalizeRouteName(value, out _, out var error);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Save_RejectsTraversalRouteName()
    {
        var store = new WebhookRouteStore(_paths);
        var route = CreateValidRoute();

        Assert.Throws<ArgumentException>(() => store.Save("../secrets", route));
    }

    [Fact]
    public void Delete_RejectsTraversalRouteName()
    {
        var store = new WebhookRouteStore(_paths);

        Assert.Throws<ArgumentException>(() => store.Delete("../secrets"));
    }

    [Fact]
    public void TryGet_RejectsTraversalRouteName()
    {
        var store = new WebhookRouteStore(_paths);

        Assert.Throws<ArgumentException>(() => store.TryGet("../secrets", out _));
    }

    [Fact]
    public void Save_NormalizesTrimAndCase()
    {
        var store = new WebhookRouteStore(_paths);
        var route = CreateValidRoute();

        store.Save("  github-issues  ", route);

        Assert.True(File.Exists(Path.Combine(_paths.WebhooksDirectory, "github-issues.json")));
    }

    [Theory]
    [InlineData("Hmac")]
    [InlineData("HeaderSecret")]
    public void Legacy_route_loads_and_round_trips_without_timestamped_properties(string verifierKind)
    {
        var path = Path.Combine(_paths.WebhooksDirectory, "legacy.json");
        File.WriteAllText(path, $$"""
{
  "Prompt": "process legacy delivery",
  "Verification": {
    "Kind": "{{verifierKind}}",
    "Secret": "legacy-secret",
    "SignatureHeaderName": "X-Legacy-Signature",
    "SecretHeaderName": "X-Legacy-Secret"
  }
}
""");
        var store = new WebhookRouteStore(_paths);

        Assert.True(store.TryGet("legacy", out var loaded));
        var route = Assert.IsType<WebhookRouteConfig>(loaded.Definition);
        Assert.Equal(verifierKind, route.Verification.Kind.ToString());
        Assert.Null(route.Verification.ToleranceSeconds);
        Assert.Null(route.Verification.TimestampField);

        route.RateLimitPerMinute = 12;
        store.Save("legacy", route);
        var saved = File.ReadAllText(path);

        Assert.DoesNotContain("ToleranceSeconds", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("TimestampField", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("SignatureField", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("SignedPayloadSeparator", saved, StringComparison.Ordinal);
    }

    [Fact]
    public void Timestamped_route_without_advanced_fields_uses_effective_defaults()
    {
        var path = Path.Combine(_paths.WebhooksDirectory, "stripe.json");
        File.WriteAllText(path, """
{
  "Prompt": "process Stripe event",
  "Verification": {
    "Kind": "HmacTimestamped",
    "Secret": "whsec_test",
    "SignatureHeaderName": "Stripe-Signature"
  }
}
""");
        var store = new WebhookRouteStore(_paths);

        Assert.True(store.TryGet("stripe", out var loaded));
        var route = Assert.IsType<WebhookRouteConfig>(loaded.Definition);
        Assert.Empty(WebhookRouteValidator.Validate("stripe", route));
        Assert.Null(route.Verification.ToleranceSeconds);
        Assert.Null(route.Verification.TimestampField);
        Assert.Null(route.Verification.SignatureField);
        Assert.Null(route.Verification.SignedPayloadSeparator);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3601)]
    public void Timestamped_route_rejects_unsafe_tolerance(int toleranceSeconds)
    {
        var route = CreateValidRoute();
        route.Verification.Kind = WebhookVerifierKind.HmacTimestamped;
        route.Verification.ToleranceSeconds = toleranceSeconds;

        var errors = WebhookRouteValidator.Validate("stripe", route);

        Assert.Contains(errors, error => error.Contains("ToleranceSeconds", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("hmac", WebhookVerifierKind.Hmac)]
    [InlineData("header-secret", WebhookVerifierKind.HeaderSecret)]
    [InlineData("HeaderSecret", WebhookVerifierKind.HeaderSecret)]
    [InlineData("hmac-timestamped", WebhookVerifierKind.HmacTimestamped)]
    [InlineData("HmacTimestamped", WebhookVerifierKind.HmacTimestamped)]
    public void TryParseVerifierKind_accepts_documented_and_config_spellings(
        string value,
        WebhookVerifierKind expected)
    {
        Assert.True(WebhookRouteValidator.TryParseVerifierKind(value, out var actual));
        Assert.Equal(expected, actual);
    }

    private static WebhookRouteConfig CreateValidRoute()
        => new()
        {
            Prompt = "triage",
            Verification = new WebhookVerificationConfig
            {
                Kind = WebhookVerifierKind.Hmac,
                Secret = new SensitiveString("secret")
            }
        };
}

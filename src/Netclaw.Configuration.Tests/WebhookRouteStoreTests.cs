// -----------------------------------------------------------------------
// <copyright file="WebhookRouteStoreTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
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

        Assert.Throws<ArgumentException>(() => store.Delete("../secrets", CancellationToken.None));
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
    [InlineData("t", "t")]
    [InlineData(null, "t")]
    [InlineData("v1", null)]
    [InlineData(" timestamp", "v1")]
    [InlineData("timestamp ", "v1")]
    [InlineData("time,stamp", "v1")]
    [InlineData("time=stamp", "v1")]
    public void Timestamped_route_rejects_unusable_structured_header_fields(
        string? timestampField,
        string? signatureField)
    {
        var route = CreateValidRoute();
        route.Verification.Kind = WebhookVerifierKind.HmacTimestamped;
        route.Verification.TimestampField = timestampField;
        route.Verification.SignatureField = signatureField;

        var errors = WebhookRouteValidator.Validate("stripe", route);

        Assert.NotEmpty(errors);
    }

    [Theory]
    [InlineData("time stamp")]
    [InlineData("time\nstamp")]
    [InlineData("t\0stamp")]
    [InlineData("téstamp")]
    public void Timestamped_route_rejects_non_token_structured_header_fields(string timestampField)
    {
        var route = CreateValidRoute();
        route.Verification.Kind = WebhookVerifierKind.HmacTimestamped;
        route.Verification.TimestampField = timestampField;

        var errors = WebhookRouteValidator.Validate("stripe", route);

        Assert.Contains(errors, error => error.Contains("HTTP token", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Route_rejects_undefined_numeric_verification_enums(bool invalidKind)
    {
        var route = CreateValidRoute();
        if (invalidKind)
            route.Verification.Kind = (WebhookVerifierKind)99;
        else
            route.Verification.HmacAlgorithm = (WebhookHmacAlgorithm)99;

        var errors = WebhookRouteValidator.Validate("invalid-enum", route);

        Assert.Contains(errors, error => error.Contains("not supported", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_serializes_read_modify_write_operations_across_store_instances_and_path_aliases()
    {
        var firstStore = new WebhookRouteStore(_paths);
        string? aliasPath = null;
        string? parentAliasPath = null;
        NetclawPaths secondPaths = _paths;
        if (!OperatingSystem.IsWindows())
        {
            var parentPath = Path.GetDirectoryName(_dir.Path)
                ?? throw new InvalidOperationException("Test directory has no parent directory.");
            parentAliasPath = $"{_dir.Path}-parent-alias";
            Directory.CreateSymbolicLink(parentAliasPath, parentPath);

            var targetThroughParentAlias = Path.Combine(parentAliasPath, Path.GetFileName(_dir.Path));
            aliasPath = $"{_dir.Path}-alias";
            Directory.CreateSymbolicLink(aliasPath, targetThroughParentAlias);
            secondPaths = new NetclawPaths(aliasPath);
        }

        var secondStore = new WebhookRouteStore(secondPaths);
        firstStore.Save("concurrent-route", CreateValidRoute());
        using var firstEntered = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var cancellationToken = TestContext.Current.CancellationToken;

        try
        {
            var first = Task.Run(() => firstStore.Update("concurrent-route", cancellationToken, existing =>
            {
                firstEntered.Set();
                Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(10), cancellationToken));
                Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(10), cancellationToken));
                existing!.RateLimitPerMinute = 12;
                return (existing, true);
            }), cancellationToken);
            Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(10), cancellationToken));

            var second = Task.Run(() =>
            {
                secondStarted.Set();
                return secondStore.Update("concurrent-route", cancellationToken, existing =>
                {
                    existing!.MaxBodyBytes = 2048;
                    return (existing, true);
                });
            }, cancellationToken);

            Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(10), cancellationToken));
            releaseFirst.Set();
            await Task.WhenAll(first, second);

            Assert.True(firstStore.TryGet("concurrent-route", out var saved));
            Assert.Equal(12, saved.Definition!.RateLimitPerMinute);
            Assert.Equal(2048, saved.Definition.MaxBodyBytes);
        }
        finally
        {
            releaseFirst.Set();
            if (aliasPath is not null)
                Directory.Delete(aliasPath);
            if (parentAliasPath is not null)
                Directory.Delete(parentAliasPath);
        }
    }

    [Fact]
    public async Task Update_lock_wait_honors_cancellation()
    {
        var firstStore = new WebhookRouteStore(_paths);
        firstStore.Save("cancelled-update", CreateValidRoute());
        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var testCancellation = TestContext.Current.CancellationToken;

        var first = Task.Run(() => firstStore.Update(
            "cancelled-update",
            testCancellation,
            existing =>
            {
                firstEntered.Set();
                Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(10), testCancellation));
                return (existing, true);
            }), testCancellation);
        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(10), testCancellation));

        var second = Task.Run(() => firstStore.Update(
            "cancelled-update",
            cancellation.Token,
            existing => (existing, true)), testCancellation);
        cancellation.Cancel();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        }
        finally
        {
            releaseFirst.Set();
            await first;
        }
    }

    [Fact]
    public void Embedded_config_and_route_schemas_share_timestamped_verification_contract()
    {
        using var configSchema = LoadEmbeddedSchema("netclaw-config.v1.schema.json");
        using var routeSchema = LoadEmbeddedSchema("webhook-route.v1.schema.json");
        var configVerificationSchema = configSchema.RootElement
            .GetProperty("$defs")
            .GetProperty("WebhookVerification");
        var routeVerificationSchema = routeSchema.RootElement
            .GetProperty("properties")
            .GetProperty("verification");
        var configVerification = configVerificationSchema.GetProperty("properties");
        var routeVerification = routeVerificationSchema.GetProperty("properties");

        foreach (var (configName, routeName) in new[]
                 {
                     ("Kind", "kind"),
                     ("ToleranceSeconds", "toleranceSeconds"),
                     ("TimestampField", "timestampField"),
                     ("SignatureField", "signatureField"),
                     ("SignedPayloadSeparator", "signedPayloadSeparator")
                 })
        {
            var configProperty = configVerification.GetProperty(configName);
            var routeProperty = routeVerification.GetProperty(routeName);
            Assert.Equal(
                JsonSerializer.Serialize(routeProperty),
                JsonSerializer.Serialize(configProperty));
        }

        var configConditionalProperties = configVerificationSchema.GetProperty("allOf")[0]
            .GetProperty("then")
            .GetProperty("properties");
        var routeConditionalProperties = routeVerificationSchema.GetProperty("allOf")[0]
            .GetProperty("then")
            .GetProperty("properties");
        foreach (var (configName, routeName) in new[]
                 {
                     ("ToleranceSeconds", "toleranceSeconds"),
                     ("TimestampField", "timestampField"),
                     ("SignatureField", "signatureField")
                 })
        {
            Assert.Equal(
                JsonSerializer.Serialize(routeConditionalProperties.GetProperty(routeName)),
                JsonSerializer.Serialize(configConditionalProperties.GetProperty(configName)));
        }
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

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    public void TryParseVerifierKind_rejects_numeric_aliases(string value)
    {
        Assert.False(WebhookRouteValidator.TryParseVerifierKind(value, out _));
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

    private static JsonDocument LoadEmbeddedSchema(string fileName)
    {
        var assembly = typeof(EmbeddedSchemaLoader).Assembly;
        var resourceName = Assert.Single(
            assembly.GetManifestResourceNames(),
            name => name.EndsWith(fileName, StringComparison.Ordinal));
        using var stream = Assert.IsAssignableFrom<Stream>(assembly.GetManifestResourceStream(resourceName));
        return JsonDocument.Parse(stream);
    }
}

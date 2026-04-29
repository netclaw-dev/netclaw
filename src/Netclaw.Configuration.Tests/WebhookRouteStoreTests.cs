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

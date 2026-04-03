using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Daemon.Webhooks;
using Xunit;

namespace Netclaw.Daemon.Tests.Webhooks;

public sealed class WebhookRouteCatalogTests : IDisposable
{
    private int _writeVersion;
    private readonly string _tempDir;
    private readonly NetclawPaths _paths;
    private readonly FakeTimeProvider _timeProvider = new(DateTimeOffset.Parse("2026-04-02T18:30:00Z"));
    private readonly RecordingNotificationSink _sink = new();

    public WebhookRouteCatalogTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-webhooks-{Guid.NewGuid():N}");
        _paths = new NetclawPaths(_tempDir);
        _paths.EnsureDirectoriesExist();
    }

    [Fact]
    public void Missing_route_returns_false()
    {
        var sut = CreateCatalog();

        var resolved = sut.TryGetRoute("missing", out _);

        Assert.False(resolved);
    }

    [Fact]
    public void Valid_route_file_loads_by_filename()
    {
        WriteRouteFile("github-issues", CreateRoute());
        var sut = CreateCatalog();

        var resolved = sut.TryGetRoute("github-issues", out var route);

        Assert.True(resolved);
        Assert.Equal("github-issues", route.Name);
        Assert.Equal("issues", Assert.Single(route.Config.Events));
    }

    [Fact]
    public void Invalid_route_file_emits_alert_and_route_is_unavailable()
    {
        WriteRouteText("github-issues", "{ bad json");
        var sut = CreateCatalog();

        var resolved = sut.TryGetRoute("github-issues", out _);

        Assert.False(resolved);
        Assert.Equal(AlertType.WebhookRouteInvalid, Assert.Single(_sink.Alerts).Category);
    }

    [Fact]
    public void Invalid_edit_removes_previously_loaded_route()
    {
        WriteRouteFile("github-issues", CreateRoute());
        var sut = CreateCatalog();

        Assert.True(sut.TryGetRoute("github-issues", out _));

        WriteRouteText("github-issues", "{ still bad json");

        var resolved = sut.TryGetRoute("github-issues", out _);

        Assert.False(resolved);
        Assert.Equal(AlertType.WebhookRouteInvalid, Assert.Single(_sink.Alerts).Category);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private WebhookRouteCatalog CreateCatalog()
        => new(
            _paths,
            new WebhooksConfig { Enabled = true },
            _sink,
            _timeProvider,
            NullLogger<WebhookRouteCatalog>.Instance);

    private void WriteRouteFile(string routeName, WebhookRouteConfig route)
    {
        var store = new Netclaw.Configuration.WebhookRouteStore(_paths);
        store.Save(routeName, route);
        BumpWriteTime(routeName);
    }

    private void WriteRouteText(string routeName, string text)
    {
        File.WriteAllText(Path.Combine(_paths.WebhooksDirectory, $"{routeName}.json"), text);
        BumpWriteTime(routeName);
    }

    private void BumpWriteTime(string routeName)
    {
        var filePath = Path.Combine(_paths.WebhooksDirectory, $"{routeName}.json");
        File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddSeconds(++_writeVersion));
    }

    private static WebhookRouteConfig CreateRoute(int? maxBodyBytes = null) => new()
    {
        Prompt = "triage",
        Events = ["issues"],
        MaxBodyBytes = maxBodyBytes ?? 1024 * 1024,
        Verification = new WebhookVerificationConfig
        {
            Kind = WebhookVerifierKind.Hmac,
            Secret = new SensitiveString("secret"),
            SignatureHeaderName = "X-Hub-Signature-256",
            SignaturePrefix = "sha256=",
            EventHeaderName = "X-GitHub-Event",
            DeliveryIdHeaderName = "X-GitHub-Delivery"
        }
    };

    private sealed class RecordingNotificationSink : IOperationalNotificationSink
    {
        public List<OperationalAlert> Alerts { get; } = [];

        public void Emit(OperationalAlert alert) => Alerts.Add(alert);
    }
}

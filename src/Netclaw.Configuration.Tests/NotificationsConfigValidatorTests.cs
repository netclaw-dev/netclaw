// -----------------------------------------------------------------------
// <copyright file="NotificationsConfigValidatorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Configuration.Tests;

/// <summary>
/// Issue #2777. A webhook entry without a Url from any source is a config error that names the key.
/// </summary>
public sealed class NotificationsConfigValidatorTests : IDisposable
{
    private const string KeyedEntryWithoutUrl = """
        { "configVersion": 1, "Notifications": { "Webhooks": { "ops": { "Format": "Slack" } } } }
        """;

    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public NotificationsConfigValidatorTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        File.WriteAllText(_paths.NetclawConfigPath, KeyedEntryWithoutUrl);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void A_keyed_webhook_with_no_url_in_any_source_fails_and_names_the_key()
    {
        var result = new NotificationsConfigValidator().Validate(name: null, Bind());

        Assert.True(result.Failed);
        Assert.Contains("Notifications:Webhooks:ops:Url", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void A_keyed_webhook_whose_url_comes_from_secrets_json_passes()
    {
        File.WriteAllText(_paths.SecretsPath, """
            { "Notifications": { "Webhooks": { "ops": { "Url": "https://hooks.example.test/x" } } } }
            """);

        var result = new NotificationsConfigValidator().Validate(name: null, Bind());

        Assert.True(result.Succeeded);
    }

    private NotificationsConfig Bind()
    {
        var configuration = new ConfigurationBuilder().AddNetclawDaemonSources(_paths).Build();
        return configuration.GetSection("Notifications").Get<NotificationsConfig>()
               ?? throw new InvalidOperationException("The Notifications section did not bind.");
    }
}

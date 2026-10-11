// -----------------------------------------------------------------------
// <copyright file="SessionKeysSchemaTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

/// <summary>
/// Session and Providers keys that the options classes bind must pass the schema check, and
/// <c>doctor --fix</c> must leave them in the file.
/// </summary>
public sealed class SessionKeysSchemaTests : IDisposable
{
    private readonly DisposableTempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Every_corrected_session_key_passes_the_check_and_survives_fix()
    {
        const string config = """
            {
              "configVersion": 1,
              "Session": {
                "MemoryDistillationTurnInterval": 5,
                "MinimumRecallCompositeScore": 0.5,
                "MaxToolDescriptionChars": 2048,
                "MaxToolSchemaWarnChars": 8000,
                "Tuning": {
                  "MemoryDistillationTurnInterval": 5,
                  "MaxToolDescriptionChars": 2048,
                  "MaxToolSchemaWarnChars": 8000,
                  "StreamingRetryPolicy": { "MaxRetries": 3, "BaseDelay": "00:00:01", "MaxDelay": "00:00:30" }
                }
              },
              "Providers": {
                "main": {
                  "Type": "openai-compatible",
                  "Endpoint": null,
                  "AuthMethod": "ApiKey",
                  "OAuthTokenExpiry": "2026-01-01T00:00:00Z",
                  "VendorOptions": { "Anything": "goes" }
                }
              }
            }
            """;

        await AssertPassesAndUnchangedAsync(config);
    }

    [Fact]
    public async Task Provider_entry_with_lowercase_and_unknown_keys_is_left_alone()
    {
        // The binder ignores key case, and --fix deletes disallowed keys without a backup.
        const string config = """
            {
              "configVersion": 1,
              "Providers": {
                "local": { "type": "openai-compatible", "authmethod": "None", "Comment": "mine" }
              }
            }
            """;

        await AssertPassesAndUnchangedAsync(config);
    }

    [Theory]
    [InlineData("5", false)]
    [InlineData("00:00:05", true)]
    [InlineData("1.02:03:04.5", true)]
    public async Task Retry_delays_must_be_timespans_not_bare_integers(string delay, bool accepted)
    {
        // TimeSpan.Parse reads "5" as five days.
        var config = $$"""
            {
              "configVersion": 1,
              "Session": { "Tuning": { "StreamingRetryPolicy": { "BaseDelay": "{{delay}}", "MaxDelay": "{{delay}}" } } }
            }
            """;

        var result = await CheckAsync(config);

        Assert.Equal(accepted, result.Severity == DoctorSeverity.Pass);
    }

    private async Task AssertPassesAndUnchangedAsync(string config)
    {
        var result = await CheckAsync(config);
        Assert.True(result.Severity == DoctorSeverity.Pass, result.Message);

        var paths = new NetclawPaths(_temp.Path);
        var service = new DoctorFixService(paths, Path.Combine(_temp.Path, "unused.service"), systemdEnabled: false);
        var plan = await service.BuildPlanAsync(TestContext.Current.CancellationToken);

        await service.ApplyAsync(plan, TestContext.Current.CancellationToken);

        Assert.Equal(config, await File.ReadAllTextAsync(paths.NetclawConfigPath, TestContext.Current.CancellationToken));
    }

    private async Task<DoctorCheckResult> CheckAsync(string config)
    {
        var paths = new NetclawPaths(_temp.Path);
        paths.EnsureDirectoriesExist();
        await File.WriteAllTextAsync(paths.NetclawConfigPath, config, TestContext.Current.CancellationToken);

        return await new ConfigSchemaDoctorCheck(paths).RunAsync(TestContext.Current.CancellationToken);
    }
}

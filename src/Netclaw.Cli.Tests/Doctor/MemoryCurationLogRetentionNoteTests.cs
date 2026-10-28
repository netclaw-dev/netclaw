// -----------------------------------------------------------------------
// <copyright file="MemoryCurationLogRetentionNoteTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Time.Testing;
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class MemoryCurationLogRetentionNoteTests : IDisposable
{
    private readonly DisposableTempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData(3, true)]
    [InlineData(13, true)]
    [InlineData(14, false)]
    [InlineData(0, false)]
    public async Task Notes_a_retention_shorter_than_the_14_day_window(int retentionDays, bool expectNote)
    {
        var paths = new NetclawPaths(Path.Combine(_temp.Path, Guid.NewGuid().ToString("N")));
        paths.EnsureDirectoriesExist();
        await File.WriteAllTextAsync(paths.NetclawConfigPath,
            $$"""{ "configVersion": 1, "Retention": { "Logs": { "Days": {{retentionDays}} } } }""", TestContext.Current.CancellationToken);

        var result = await new MemoryCurationLlmDoctorCheck(paths, new FakeTimeProvider(DateTimeOffset.Parse("2026-05-20T12:00:00Z")))
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expectNote, result.Message.Contains($"Log retention is set to {retentionDays} days", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("daemon-2026-05-19.log", 0, true)]
    [InlineData("daemon-2026-05-19.1.log", 0, true)]
    [InlineData("daemon-2026-05-19.10.log", 0, true)]
    [InlineData("daemon-2026-05-19.2147483647.log", 0, true)]
    [InlineData("daemon-2026-05-19.2.log", 2, true)]
    [InlineData("daemon-2026-05-06.log", 0, true)]
    [InlineData("daemon-2026-05-06.1.log", 0, true)]
    [InlineData("daemon-2026-05-05.log", 0, false)]
    [InlineData("daemon-2026-05-05.1.log", 0, false)]
    [InlineData("daemon-2026-05-19.0.log", 2, false)]
    [InlineData("daemon-2026-05-19.01.log", 2, false)]
    [InlineData("daemon-2026-05-19.-1.log", 2, false)]
    [InlineData("daemon-2026-05-19.+1.log", 2, false)]
    [InlineData("daemon-2026-05-19.2147483648.log", 2, false)]
    [InlineData("daemon-2026-05-19.99999999999.log", 2, false)]
    [InlineData("daemon-2026-05-19.1.2.log", 2, false)]
    [InlineData("daemon-2026-05-19..log", 2, false)]
    [InlineData("daemon-2026-05-19. 1.log", 2, false)]
    [InlineData("daemon-2026-05-19.١.log", 2, false)]
    [InlineData("daemon-2026-05-19.extra.log", 2, false)]
    [InlineData("daemon-2026-05-19.1.log.backup", 2, false)]
    [InlineData("daemon-2026-5-19.1.log", 2, false)]
    [InlineData("daemon-2026-05-32.1.log", 2, false)]
    [InlineData("other-daemon-2026-05-19.1.log", 2, false)]
    public async Task Reports_curation_health_from_valid_segments_in_the_date_window(
        string fileName, int baseSuccesses, bool includeFailures)
    {
        var paths = new NetclawPaths(_temp.Path);
        paths.EnsureDirectoriesExist();
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllLinesAsync(Path.Combine(paths.LogsDirectory, "daemon-2026-05-20.log"),
            Enumerable.Repeat("curation_llm_decision", baseSuccesses), cancellationToken);
        await File.WriteAllLinesAsync(Path.Combine(paths.LogsDirectory, fileName),
            ["curation_llm_no_decision", "curation_llm_timeout", "curation_llm_error"], cancellationToken);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 20, 0, 0, 0, TimeSpan.Zero));

        var result = await new MemoryCurationLlmDoctorCheck(paths, time).RunAsync(cancellationToken);

        Assert.Equal("Memory Curation LLM", result.Name);
        Assert.Equal(includeFailures ? DoctorSeverity.Warning : DoctorSeverity.Pass, result.Severity);
        if (includeFailures)
        {
            Assert.Contains(baseSuccesses == 0 ? "0 successful decisions vs 3 failures" : "(3/5)", result.Message);
            Assert.NotNull(result.Remediation);
            Assert.Contains("curation_llm_timeout", result.Remediation);
        }
        else
        {
            Assert.Contains(baseSuccesses == 0 ? "No curation LLM activity" : "2 decisions, 0 failures", result.Message);
            Assert.Null(result.Remediation);
        }
    }
}

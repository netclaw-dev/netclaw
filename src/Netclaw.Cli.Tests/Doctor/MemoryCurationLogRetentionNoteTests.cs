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
}

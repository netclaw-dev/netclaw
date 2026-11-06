// -----------------------------------------------------------------------
// <copyright file="MemoryCurationLlmDoctorCheckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Time.Testing;
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class MemoryCurationLlmDoctorCheckTests : IDisposable
{
    private readonly DisposableTempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Reads_and_classifies_log_while_daemon_keeps_it_open_for_append()
    {
        var paths = new NetclawPaths(_temp.Path);
        paths.EnsureDirectoriesExist();

        var logPath = Path.Combine(paths.LogsDirectory, "daemon-2026-05-20.log");
        using var writerStream = new FileStream(
            logPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        using var writer = new StreamWriter(writerStream);
        writer.WriteLine("curation_llm_decision");
        writer.WriteLine("curation_llm_timeout");
        writer.Flush();

        var time = new FakeTimeProvider(
            new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero));
        var result = await new MemoryCurationLlmDoctorCheck(paths, time)
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Memory Curation LLM", result.Name);
        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        // The percent text follows the current culture ("50%" or "50 %"), so
        // check the counts, which do not change with the culture.
        Assert.Contains("(1/2)", result.Message);
    }
}

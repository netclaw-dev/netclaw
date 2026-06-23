// -----------------------------------------------------------------------
// <copyright file="JobOutputLogTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Jobs;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Actors.Tests.Jobs;

public class JobOutputLogTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string LogPath => Path.Combine(_dir.Path, "job", "output.log");

    [Fact]
    public async Task WriteLine_IsObservableOnDiskBeforeDispose()
    {
        await using var log = new JobOutputLog(LogPath);

        await log.WriteLineAsync("server listening on :4000", isStderr: false);

        // The whole point of streaming capture: the line is readable while the
        // writer (and the process behind it) is still alive.
        var (tail, truncated) = JobOutputLog.ReadTail(LogPath, 2000);
        Assert.Contains("server listening on :4000", tail);
        Assert.False(truncated);
    }

    [Fact]
    public async Task Constructor_EagerlyCreatesLogFile()
    {
        // The submit ACK hands the agent this path immediately — it must be
        // readable from job start, not after first output.
        await using var log = new JobOutputLog(LogPath);
        Assert.True(File.Exists(LogPath));
    }

    [Fact]
    public async Task WriteLine_RedactsSecretsPerLine()
    {
        await using var log = new JobOutputLog(LogPath);

        await log.WriteLineAsync("API_KEY=super-secret-value-123", isStderr: false);

        var content = await ReadAllSharedAsync(LogPath);
        Assert.DoesNotContain("super-secret-value-123", content);
        Assert.Contains("API_KEY=", content);
    }

    [Fact]
    public async Task WriteLine_PrefixesStderrLines()
    {
        await using var log = new JobOutputLog(LogPath);

        await log.WriteLineAsync("an error happened", isStderr: true);
        await log.WriteLineAsync("normal output", isStderr: false);

        var content = await ReadAllSharedAsync(LogPath);
        Assert.Contains("[stderr] an error happened", content);
        Assert.Contains("normal output", content);
        Assert.DoesNotContain("[stderr] normal output", content);
    }

    [Fact]
    public async Task Rotation_BoundsDiskAndKeepsStreaming()
    {
        await using var log = new JobOutputLog(LogPath, rotationThresholdBytes: 256);

        for (var i = 0; i < 20; i++)
            await log.WriteLineAsync($"line-{i:D3} padding-padding-padding-padding", isStderr: false);

        Assert.True(log.Rotated);
        Assert.True(File.Exists(log.RotatedPath));
        // Streaming continued into a fresh current log after rotation.
        var current = await ReadAllSharedAsync(LogPath);
        Assert.Contains("line-019", current);
        // Bounded: the current log holds less than the full 20 lines.
        Assert.DoesNotContain("line-000", current);
    }

    [Fact]
    public async Task ReadTail_IsBoundedAndMarksTruncation()
    {
        await using var log = new JobOutputLog(LogPath);
        for (var i = 0; i < 200; i++)
            await log.WriteLineAsync($"line-{i:D4}", isStderr: false);

        var (tail, truncated) = JobOutputLog.ReadTail(LogPath, 100);

        Assert.True(tail.Length <= 100);
        Assert.Contains("line-0199", tail);
        Assert.True(truncated);
    }

    [Fact]
    public async Task RotationFailure_IsNonFatal_AndCaptureContinues()
    {
        await using var log = new JobOutputLog(LogPath, rotationThresholdBytes: 64);
        // A directory squatting on the rotation target makes the rotation's
        // File.Move throw deterministically, regardless of platform or user.
        Directory.CreateDirectory(log.RotatedPath);

        for (var i = 0; i < 10; i++)
            await log.WriteLineAsync($"line-{i} padding-padding-padding-padding-padding", isStderr: false);

        // A transient rotation (File.Move) failure must NOT be treated as a
        // capture failure: the pipe is healthy, so capture continues writing to
        // the current log rather than going permanently silent for the job.
        Assert.Null(log.WriteFailure);
        await log.WriteLineAsync("still-capturing-after-failed-rotate", isStderr: false);

        var content = await ReadAllSharedAsync(LogPath);
        Assert.Contains("still-capturing-after-failed-rotate", content);
        // Move never succeeded, so nothing rotated out.
        Assert.False(log.Rotated);
    }

    [Fact]
    public async Task ReadTail_FallsBackToRotatedFile_WhenCurrentLogMissing()
    {
        // Simulate the rotation window: the current log has been File.Move'd to
        // the .1 slot and the fresh current file is not open yet. ReadTail must
        // read the rotated predecessor rather than throw / report an empty tail.
        await using (var log = new JobOutputLog(LogPath))
        {
            await log.WriteLineAsync("server listening on :4000", isStderr: false);
        }

        var rotated = JobOutputLog.RotatedPathFor(LogPath);
        File.Move(LogPath, rotated);
        Assert.False(File.Exists(LogPath));

        var (tail, _) = JobOutputLog.ReadTail(LogPath, 2000);
        Assert.Contains("server listening on :4000", tail);
    }

    [Fact]
    public void ReadTail_RethrowsWhenNeitherCurrentNorRotatedExists()
    {
        var missing = Path.Combine(_dir.Path, "never", "created.log");
        Assert.ThrowsAny<IOException>(() => JobOutputLog.ReadTail(missing, 2000));
    }

    private static async Task<string> ReadAllSharedAsync(string path)
    {
        using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }
}

// -----------------------------------------------------------------------
// <copyright file="InboxWriterTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using Netclaw.Actors.Protocol;
using Xunit;

namespace Netclaw.Actors.Tests.Protocol;

public sealed class InboxWriterTests : IDisposable
{
    private readonly string _inboxDir;

    public InboxWriterTests()
    {
        _inboxDir = Path.Combine(
            Path.GetTempPath(),
            $"netclaw-inbox-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_inboxDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_inboxDir))
                Directory.Delete(_inboxDir, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public void ReserveUniquePath_returns_plain_name_when_no_collision()
    {
        var path = InboxWriter.ReserveUniquePath(_inboxDir, "report.pdf");

        Assert.Equal(Path.Combine(_inboxDir, "report.pdf"), path);
    }

    [Fact]
    public void ReserveUniquePath_suffixes_when_file_exists()
    {
        File.WriteAllText(Path.Combine(_inboxDir, "report.pdf"), "existing");

        var path = InboxWriter.ReserveUniquePath(_inboxDir, "report.pdf");

        Assert.Equal(Path.Combine(_inboxDir, "report_1.pdf"), path);
    }

    [Fact]
    public void ReserveUniquePath_chains_suffixes_across_multiple_collisions()
    {
        File.WriteAllText(Path.Combine(_inboxDir, "report.pdf"), "first");
        File.WriteAllText(Path.Combine(_inboxDir, "report_1.pdf"), "second");
        File.WriteAllText(Path.Combine(_inboxDir, "report_2.pdf"), "third");

        var path = InboxWriter.ReserveUniquePath(_inboxDir, "report.pdf");

        Assert.Equal(Path.Combine(_inboxDir, "report_3.pdf"), path);
    }

    [Fact]
    public void ReserveUniquePath_handles_extensionless_names()
    {
        File.WriteAllText(Path.Combine(_inboxDir, "README"), "first");

        var path = InboxWriter.ReserveUniquePath(_inboxDir, "README");

        Assert.Equal(Path.Combine(_inboxDir, "README_1"), path);
    }

    [Fact]
    public void ReserveUniquePath_throws_on_exhaustion()
    {
        File.WriteAllText(Path.Combine(_inboxDir, "report.pdf"), "base");
        for (var i = 1; i <= InboxWriter.MaxCollisionSuffix; i++)
            File.WriteAllText(Path.Combine(_inboxDir, $"report_{i}.pdf"), "collide");

        Assert.Throws<InboxWriter.CollisionExhaustedException>(
            () => InboxWriter.ReserveUniquePath(_inboxDir, "report.pdf"));
    }

    [Fact]
    public async Task WriteAtomicAsync_writes_expected_bytes()
    {
        var target = Path.Combine(_inboxDir, "hello.txt");
        var payload = "hello world"u8.ToArray();

        await InboxWriter.WriteAtomicAsync(target, payload, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(target));
        Assert.Equal(payload, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WriteAtomicAsync_leaves_no_temp_file_on_success()
    {
        var target = Path.Combine(_inboxDir, "hello.txt");
        await InboxWriter.WriteAtomicAsync(target, "hi"u8.ToArray(), TestContext.Current.CancellationToken);

        var stragglers = Directory.GetFiles(_inboxDir).Where(f => Path.GetFileName(f).Contains(".tmp"));
        Assert.Empty(stragglers);
    }

    [Fact]
    public async Task SanitizeReserveAndWrite_strips_path_components_and_traversal()
    {
        var written = await InboxWriter.SanitizeReserveAndWriteAsync(
            _inboxDir,
            "../../../etc/passwd",
            "malicious"u8.ToArray(),
            TestContext.Current.CancellationToken);

        var directory = Path.GetDirectoryName(written)!;
        Assert.Equal(_inboxDir, directory);
        Assert.DoesNotContain("..", Path.GetFileName(written), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine("/", "etc", "passwd_suffix_that_should_not_exist")));
    }

    [Fact]
    public async Task SanitizeReserveAndWrite_round_trips_bytes()
    {
        var payload = Encoding.UTF8.GetBytes("roundtrip content");

        var written = await InboxWriter.SanitizeReserveAndWriteAsync(
            _inboxDir,
            "note.txt",
            payload,
            TestContext.Current.CancellationToken);

        Assert.Equal(payload, await File.ReadAllBytesAsync(written, TestContext.Current.CancellationToken));
    }
}

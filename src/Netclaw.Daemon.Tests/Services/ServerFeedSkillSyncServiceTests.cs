// -----------------------------------------------------------------------
// <copyright file="ServerFeedSkillSyncServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Netclaw.Configuration.Feeds;
using Netclaw.Daemon.Services;
using Netclaw.Security.Skills;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class ServerFeedSkillSyncServiceTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly SkillRegistry _skillRegistry = new();
    private readonly SkillIndexContextLayer _indexLayer = new();

    public ServerFeedSkillSyncServiceTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    [Fact]
    public async Task ExtractArchiveAsync_AllowsArbitraryResourcesAndPreservesExecutableMode()
    {
        var skillContent = Encoding.UTF8.GetBytes("""
            ---
            name: packaged
            description: Packaged skill.
            ---

            # Packaged
            """);
        var scriptContent = Encoding.UTF8.GetBytes("#!/bin/bash\necho ok\n");
        var binaryContent = new byte[] { 0x00, 0x01, 0xFF, 0x02 };
        var archive = BuildArchive(
            ("SKILL.md", skillContent, 0x1A4),
            ("tools/check", scriptContent, 0x1ED),
            ("assets/icon.bin", binaryContent, 0x1A4));

        var files = await CreateService().ExtractArchiveAsync(
            "packaged", "private", archive, TestContext.Current.CancellationToken);

        Assert.NotNull(files);
        Assert.Contains(files!, file => file.RelativePath == "tools/check" && file.UnixMode == 0x1ED);
        Assert.Contains(files, file => file.RelativePath == "assets/icon.bin");

        var feedDir = _paths.ServerFeedDirectory("private");
        await SkillSyncHelpers.ReplaceSkillDirectoryAsync(
            feedDir, "packaged", files!, TestContext.Current.CancellationToken);

        var skillDir = Path.Combine(feedDir, "packaged");
        Assert.Equal(scriptContent, await File.ReadAllBytesAsync(Path.Combine(skillDir, "tools", "check"), TestContext.Current.CancellationToken));
        Assert.Equal(binaryContent, await File.ReadAllBytesAsync(Path.Combine(skillDir, "assets", "icon.bin"), TestContext.Current.CancellationToken));

        if (!OperatingSystem.IsWindows())
        {
            var mode = (int)File.GetUnixFileMode(Path.Combine(skillDir, "tools", "check")) & 0x1FF;
            Assert.Equal(0x1ED, mode);
        }
    }

    [Fact]
    public async Task ExtractArchiveAsync_RejectsTraversalEntries()
    {
        var skillContent = Encoding.UTF8.GetBytes("""
            ---
            name: packaged
            description: Packaged skill.
            ---

            # Packaged
            """);
        var archive = BuildArchive(
            ("SKILL.md", skillContent, 0x1A4),
            ("../escape.sh", Encoding.UTF8.GetBytes("echo no"), 0x1ED));

        var files = await CreateService().ExtractArchiveAsync(
            "packaged", "private", archive, TestContext.Current.CancellationToken);

        Assert.Null(files);
    }

    public void Dispose() => _dir.Dispose();

    private ServerFeedSkillSyncService CreateService(ISkillContentScanner? scanner = null)
        => new(
            new SkillFeedsConfig(),
            _paths,
            _skillRegistry,
            _indexLayer,
            TimeProvider.System,
            scanner ?? new NoOpSkillContentScanner(),
            NullLogger<ServerFeedSkillSyncService>.Instance,
            []);

    private static byte[] BuildArchive(params (string Path, byte[] Content, int UnixMode)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content, unixMode) in entries)
            {
                var entry = archive.CreateEntry(path);
                entry.ExternalAttributes = unixMode << 16;
                using var entryStream = entry.Open();
                entryStream.Write(content);
            }
        }

        return stream.ToArray();
    }
}

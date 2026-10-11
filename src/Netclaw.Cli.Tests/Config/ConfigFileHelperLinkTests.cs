// -----------------------------------------------------------------------
// <copyright file="ConfigFileHelperLinkTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Config;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Config;

/// <summary>
/// <see cref="ConfigFileHelper.WriteConfigFile(string, Dictionary{string, object})"/> follows a
/// symbolic link. These tests make the write to the link target fail with a chosen errno.
/// </summary>
public sealed class ConfigFileHelperLinkTests : IDisposable
{
    private const string Original = """{ "configVersion": 1 }""";

    private readonly DisposableTempDir _dir = new();
    private readonly string _link;
    private readonly string _target;

    public ConfigFileHelperLinkTests()
    {
        _target = Path.Combine(_dir.Path, "dotfiles-netclaw.json");
        _link = Path.Combine(_dir.Path, "netclaw.json");
        File.WriteAllText(_target, Original);
        if (!OperatingSystem.IsWindows())
            File.CreateSymbolicLink(_link, _target);
    }

    public void Dispose() => _dir.Dispose();

    private Action<string, string, Action<string>?> FailOnTarget(int errno)
        => (path, contents, harden) =>
        {
            if (path == _target)
                throw new IOException("write failed", errno);

            AtomicFile.WriteAllText(path, contents, harden);
        };

    [Fact]
    public void A_full_disk_on_the_target_fails_the_save_and_keeps_the_link()
    {
        if (OperatingSystem.IsWindows())
            return; // errno values are POSIX

        var ex = Assert.Throws<IOException>(() =>
            ConfigFileHelper.WriteConfigFile(_link, new Dictionary<string, object> { ["configVersion"] = 2 }, FailOnTarget(errno: 28)));

        Assert.Equal(28, ex.HResult);
        Assert.Equal(_target, new FileInfo(_link).LinkTarget);
        Assert.Equal(Original, File.ReadAllText(_target));
    }

    [Theory]
    [InlineData(16)] // EBUSY: a single file mounted into a container
    [InlineData(30)] // EROFS
    public void A_busy_or_read_only_target_is_replaced_by_a_plain_file(int errno)
    {
        if (OperatingSystem.IsWindows())
            return; // errno values are POSIX

        ConfigFileHelper.WriteConfigFile(_link, new Dictionary<string, object> { ["configVersion"] = 2 }, FailOnTarget(errno));

        Assert.Null(new FileInfo(_link).LinkTarget);
        Assert.Contains("2", File.ReadAllText(_link), StringComparison.Ordinal);
        Assert.Equal(Original, File.ReadAllText(_target));
    }
}

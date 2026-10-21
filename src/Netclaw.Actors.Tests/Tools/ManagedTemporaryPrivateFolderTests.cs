// -----------------------------------------------------------------------
// <copyright file="ManagedTemporaryPrivateFolderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Netclaw.Actors.Tools;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Issue #2554, Fix B, on the real host filesystem: <see cref="ManagedTemporaryEnvironment.Prepare(ProcessStartInfo, ManagedTemporaryLocation)"/>
/// creates the private base and the key folder with mode 0700, refuses a
/// linked or open folder, and never falls back to the deep path.
/// </summary>
/// <remarks>
/// Each test uses a private base below its own temp folder, never the
/// <c>/tmp/netclaw-&lt;uid&gt;</c> folder of a live daemon. Only the socket test
/// needs a short base directly below <c>/tmp</c>. It uses a unique name and
/// deletes the folder.
/// </remarks>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class ManagedTemporaryPrivateFolderTests : IDisposable
{
    private const UnixFileMode PrivateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OpenMode = PrivateMode
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private readonly DisposableTempDir _dir = new();
    private readonly string _envelope;
    private readonly string _base;
    private string? _shortBase;

    public ManagedTemporaryPrivateFolderTests()
    {
        _envelope = Path.Combine(_dir.Path, "sessions", "C0EXAMPLE01_1791949577_000000-0123456789abcdef");
        _base = Path.Combine(_dir.Path, "netclaw-base");
        Directory.CreateDirectory(_envelope);
    }

    public static bool IsPosix => !OperatingSystem.IsWindows();

    public void Dispose()
    {
        if (_shortBase is not null)
            DisposableTempDir.Delete(_shortBase);
        _dir.Dispose();
    }

    [SlopwatchSuppress("SW001", "The private base exists only on POSIX hosts.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The private base exists only on POSIX hosts.")]
    public void Prepare_creates_the_base_and_the_key_folder_with_mode_0700()
    {
        var storage = Storage(_base);
        var startInfo = CreateStartInfoWithoutTemporaryVariables();

        Assert.Null(ManagedTemporaryEnvironment.Prepare(startInfo, storage.ManagedTemporary));

        var directory = storage.ManagedTemporary.Directory.Value;
        Assert.Equal(Path.Combine(_base, Key(_envelope), "p"), directory);
        Assert.True(Directory.Exists(directory));
        Assert.Equal(PrivateMode, File.GetUnixFileMode(_base));
        Assert.Equal(PrivateMode, File.GetUnixFileMode(Path.Combine(_base, Key(_envelope))));
        Assert.Equal(directory, startInfo.Environment["TMPDIR"]);
        Assert.Equal(directory, startInfo.Environment["TMP"]);
        Assert.Equal(directory, startInfo.Environment["TEMP"]);
        Assert.False(Directory.Exists(Path.Combine(_envelope, "tmp")));
    }

    [SlopwatchSuppress("SW001", "The private base exists only on POSIX hosts.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The private base exists only on POSIX hosts.")]
    public void A_child_run_gets_its_own_folder_in_the_key_folder()
    {
        var child = Storage(_base).ForChild(new SubAgentRunId("run-7"), new SubAgentScopeId("s/subagent/a/run-7"));
        var startInfo = CreateStartInfoWithoutTemporaryVariables();

        Assert.Null(ManagedTemporaryEnvironment.Prepare(startInfo, child.ManagedTemporary));

        var expected = Path.Combine(_base, Key(_envelope), "c" + Hex("run-7", 8));
        Assert.True(Directory.Exists(expected));
        Assert.Equal(expected, startInfo.Environment["TMPDIR"]);
    }

    [SlopwatchSuppress("SW001", "This test needs POSIX symbolic links.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "This test needs POSIX symbolic links.")]
    public void A_linked_base_refuses_the_launch_without_a_fallback()
    {
        var target = Path.Combine(_dir.Path, "attacker-folder");
        Directory.CreateDirectory(target);
        File.SetUnixFileMode(target, PrivateMode);
        Directory.CreateSymbolicLink(_base, target);
        var storage = Storage(_base);
        var startInfo = CreateStartInfoWithoutTemporaryVariables();

        var error = ManagedTemporaryEnvironment.Prepare(startInfo, storage.ManagedTemporary);

        AssertRefused(error, _base, "symbolic link", startInfo);
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    [SlopwatchSuppress("SW001", "The private base exists only on POSIX hosts.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The private base exists only on POSIX hosts.")]
    public void An_open_base_refuses_the_launch_and_keeps_its_mode()
    {
        Directory.CreateDirectory(_base);
        File.SetUnixFileMode(_base, OpenMode);
        var startInfo = CreateStartInfoWithoutTemporaryVariables();

        var error = ManagedTemporaryEnvironment.Prepare(startInfo, Storage(_base).ManagedTemporary);

        AssertRefused(error, _base, "group or other", startInfo);
        Assert.Equal(OpenMode, File.GetUnixFileMode(_base));
    }

    [SlopwatchSuppress("SW001", "This test uses /bin/sh and POSIX temporary variables.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "This test uses /bin/sh and POSIX temporary variables.")]
    public async Task The_launched_process_and_the_parser_facts_see_the_same_short_folder()
    {
        var storage = Storage(_base);
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("printf '%s' \"$TMPDIR|$TMP|$TEMP\"");

        Assert.Null(ManagedTemporaryEnvironment.Prepare(startInfo, storage.ManagedTemporary));
        using var process = Process.Start(startInfo)!;
        var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        var facts = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux)
            .GetLaunchVariables(storage.ManagedTemporary)
            .Where(static variable => variable.Key is "TMPDIR" or "TMP" or "TEMP")
            .Select(static variable => variable.Value)
            .Distinct()
            .ToArray();
        var expected = Path.Combine(_base, Key(_envelope), "p");
        Assert.Equal($"{expected}|{expected}|{expected}", output);
        Assert.Equal([expected], facts);
    }

    /// <summary>
    /// The #2554 Chrome failure: a Unix socket below the deep folder is too long.
    /// The same socket below the short folder binds.
    /// </summary>
    [SlopwatchSuppress("SW001", "Unix socket path limits apply to POSIX hosts.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "Unix socket path limits apply to POSIX hosts.")]
    public void A_chrome_singleton_socket_binds_below_the_short_folder_and_not_below_the_deep_folder()
    {
        // A short private base like /tmp/netclaw-<uid>, with a unique test name.
        _shortBase = Path.Combine(
            OperatingSystem.IsMacOS() ? "/private/tmp" : "/tmp",
            $"netclaw-t{Guid.NewGuid():N}"[..21]);
        var storage = Storage(_shortBase);
        Assert.Null(ManagedTemporaryEnvironment.Prepare(new ProcessStartInfo(), storage.ManagedTemporary));

        Assert.True(BindChromeSingletonSocket(storage.ManagedTemporary.Directory.Value));

        // The deep layout of the same session fails, as Chrome did on 14 Oct 2026.
        var deep = SessionStoragePaths.CreateVersion2(
            new SessionStorageEnvelopeRoot(_envelope),
            ManagedTemporaryLayout.InsideSessionStorage.Instance);
        Assert.Null(ManagedTemporaryEnvironment.Prepare(new ProcessStartInfo(), deep.ManagedTemporary));
        Assert.ThrowsAny<Exception>(() => BindChromeSingletonSocket(deep.ManagedTemporary.Directory.Value));
    }

    // Returns true when the socket file exists while the socket is bound.
    // .NET removes the file when it closes the socket.
    private static bool BindChromeSingletonSocket(string temporaryDirectory)
    {
        var folder = Path.Combine(temporaryDirectory, "com.google.Chrome.KZWqUD");
        Directory.CreateDirectory(folder);
        var socketPath = Path.Combine(folder, "SingletonSocket");
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(socketPath));
        return Path.Exists(socketPath);
    }

    private void AssertRefused(string? error, string folder, string cause, ProcessStartInfo startInfo)
    {
        Assert.NotNull(error);
        Assert.StartsWith("Error:", error, StringComparison.Ordinal);
        Assert.Contains(folder, error, StringComparison.Ordinal);
        Assert.Contains(cause, error, StringComparison.OrdinalIgnoreCase);
        Assert.False(startInfo.Environment.ContainsKey("TMPDIR"));
        Assert.False(startInfo.Environment.ContainsKey("TMP"));
        Assert.False(startInfo.Environment.ContainsKey("TEMP"));
        // No silent fallback to the deep folder in the session envelope.
        Assert.False(Directory.Exists(Path.Combine(_envelope, "tmp")));
    }

    private SessionStoragePaths Storage(string baseFolder)
        => SessionStoragePaths.CreateVersion2(
            new SessionStorageEnvelopeRoot(_envelope),
            new ManagedTemporaryLayout.PrivateBase(new ManagedTemporaryBase(baseFolder)));

    private static string Key(string envelope) => Hex(envelope, 16);

    private static string Hex(string text, int length)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..length];

    private static ProcessStartInfo CreateStartInfoWithoutTemporaryVariables()
    {
        var startInfo = new ProcessStartInfo();
        startInfo.Environment.Remove("TMPDIR");
        startInfo.Environment.Remove("TMP");
        startInfo.Environment.Remove("TEMP");
        return startInfo;
    }
}

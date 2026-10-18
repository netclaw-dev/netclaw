// -----------------------------------------------------------------------
// <copyright file="ManagedTemporaryLayoutTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Issue #2554, Fix B: on POSIX the managed temporary folder of a run is
/// <c>&lt;base&gt;/&lt;key&gt;/p</c> (parent) or <c>&lt;base&gt;/&lt;key&gt;/c&lt;8 hex&gt;</c>
/// (child). The key folder is the storage root. These tests check paths only;
/// they create no folder.
/// </summary>
public sealed class ManagedTemporaryLayoutTests
{
    // The live envelope of the #2554 Chrome failure (14 Oct 2026).
    private const string LiveEnvelope =
        "/home/petabridge/.netclaw/sessions/D0AC6CKBK5K_1791949577_967149-798b80ef96b11016";

    // Chrome makes "<TMPDIR>/com.google.Chrome.XXXXXX/SingletonSocket".
    private const string ChromeSocketSuffix = "/com.google.Chrome.KZWqUD/SingletonSocket";

    // Linux sun_path holds 108 bytes, with the terminating NUL.
    private const int MaxSocketPathLength = 107;

    public static bool IsPosix => !OperatingSystem.IsWindows();

    [SlopwatchSuppress("SW001", "The short layout exists only on POSIX hosts.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The short layout exists only on POSIX hosts.")]
    public void Version_2_parent_uses_the_key_folder_below_the_private_base()
    {
        var temporaryBase = new ManagedTemporaryBase("/tmp/netclaw-1000");
        var storage = SessionStoragePaths.CreateVersion2(
            new SessionStorageEnvelopeRoot(LiveEnvelope),
            new ManagedTemporaryLayout.PrivateBase(temporaryBase));

        var key = Hex(LiveEnvelope, 16);
        Assert.Equal($"/tmp/netclaw-1000/{key}/p", storage.ManagedTemporary.Directory.Value);
        Assert.Equal($"/tmp/netclaw-1000/{key}", storage.ManagedTemporary.StorageRoot.Value);
        Assert.Equal(temporaryBase, storage.ManagedTemporary.PrivateBase);

        // The other session paths stay in the envelope.
        Assert.Equal(Path.Combine(LiveEnvelope, "workspace"), storage.SessionDirectory.Value);
        Assert.Equal(Path.Combine(LiveEnvelope, "artifacts"), storage.ArtifactDirectory.Value);
    }

    [SlopwatchSuppress("SW001", "The short layout exists only on POSIX hosts.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The short layout exists only on POSIX hosts.")]
    public void Child_runs_use_their_own_folder_in_the_same_key_folder()
    {
        var layout = new ManagedTemporaryLayout.PrivateBase(new ManagedTemporaryBase("/tmp/netclaw-1000"));
        var parent = SessionStoragePaths.CreateVersion2(new SessionStorageEnvelopeRoot(LiveEnvelope), layout);

        var first = parent.ForChild(new SubAgentRunId("run-7"), new SubAgentScopeId("s/subagent/a/run-7"));
        var second = parent.ForChild(new SubAgentRunId("run-8"), new SubAgentScopeId("s/subagent/a/run-8"));

        var key = Hex(LiveEnvelope, 16);
        Assert.Equal($"/tmp/netclaw-1000/{key}/c{Hex("run-7", 8)}", first.ManagedTemporary.Directory.Value);
        Assert.Equal($"/tmp/netclaw-1000/{key}/c{Hex("run-8", 8)}", second.ManagedTemporary.Directory.Value);
        Assert.Equal(parent.ManagedTemporary.StorageRoot, first.ManagedTemporary.StorageRoot);
        Assert.Equal(parent.ManagedTemporary.StorageRoot, second.ManagedTemporary.StorageRoot);
        Assert.NotEqual(first.ManagedTemporary.Directory, second.ManagedTemporary.Directory);
        Assert.NotEqual(parent.ManagedTemporary.Directory, first.ManagedTemporary.Directory);
        Assert.Equal(layout.Base, first.ManagedTemporary.PrivateBase);
    }

    [SlopwatchSuppress("SW001", "The short layout exists only on POSIX hosts.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The short layout exists only on POSIX hosts.")]
    public void Legacy_session_uses_the_short_layout_keyed_by_its_session_directory()
    {
        const string sessionDirectory = "/home/user/.netclaw/sessions/signalr_example";
        var storage = SessionStoragePaths.CreateLegacy(
            sessionDirectory,
            "/home/user/.netclaw/logs/sessions",
            "signalr_example",
            new ManagedTemporaryLayout.PrivateBase(new ManagedTemporaryBase("/tmp/netclaw-1000")));

        var key = Hex(sessionDirectory, 16);
        Assert.Equal($"/tmp/netclaw-1000/{key}/p", storage.ManagedTemporary.Directory.Value);
        Assert.Equal($"/tmp/netclaw-1000/{key}", storage.ManagedTemporary.StorageRoot.Value);
    }

    [SlopwatchSuppress("SW001", "The short layout exists only on POSIX hosts.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The short layout exists only on POSIX hosts.")]
    [InlineData("/tmp/netclaw-1000")]
    [InlineData("/tmp/netclaw-4294967294")]
    [InlineData("/private/tmp/netclaw-501")]
    public void A_chrome_socket_below_the_managed_folder_fits_the_socket_limit(string baseFolder)
    {
        var parent = SessionStoragePaths.CreateVersion2(
            new SessionStorageEnvelopeRoot(LiveEnvelope),
            new ManagedTemporaryLayout.PrivateBase(new ManagedTemporaryBase(baseFolder)));
        var child = parent.ForChild(
            new SubAgentRunId("0f8fad5b-d9cb-469f-a165-70867728950e"),
            new SubAgentScopeId("s/subagent/a/run"));

        Assert.InRange((parent.ManagedTemporary.Directory.Value + ChromeSocketSuffix).Length, 1, MaxSocketPathLength);
        Assert.InRange((child.ManagedTemporary.Directory.Value + ChromeSocketSuffix).Length, 1, MaxSocketPathLength);
    }

    [SlopwatchSuppress("SW001", "The deep layout length matters only for POSIX Unix sockets.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The deep layout length matters only for POSIX Unix sockets.")]
    public void The_storage_layout_keeps_the_deep_path_that_breaks_chrome()
    {
        // Guard and evidence: the Windows layout is unchanged, and on the live
        // host its Chrome socket path has 133 characters.
        var storage = SessionStoragePaths.CreateVersion2(
            new SessionStorageEnvelopeRoot(LiveEnvelope),
            ManagedTemporaryLayout.InsideSessionStorage.Instance);

        Assert.Equal(Path.Combine(LiveEnvelope, "tmp", "parent"), storage.ManagedTemporary.Directory.Value);
        Assert.Equal(LiveEnvelope, storage.ManagedTemporary.StorageRoot.Value);
        Assert.Null(storage.ManagedTemporary.PrivateBase);
        Assert.Equal(133, (storage.ManagedTemporary.Directory.Value + ChromeSocketSuffix).Length);
    }

    [Fact]
    public void A_persisted_job_record_keeps_its_stored_deep_path()
    {
        var envelope = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "netclaw-storage", "session-42"));
        var deep = Path.Combine(envelope, "tmp", "parent");

        var location = ManagedTemporaryLocation.FromPersistedPaths(deep, envelope);

        Assert.Equal(deep, location.Directory.Value);
        Assert.Equal(envelope, location.StorageRoot.Value);
        Assert.Null(location.PrivateBase);
    }

    [SlopwatchSuppress("SW001", "The private base exists only on POSIX hosts.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "The private base exists only on POSIX hosts.")]
    public void Base_of_the_current_user_uses_the_effective_user_id()
    {
        var expectedRoot = OperatingSystem.IsMacOS() ? "/private/tmp" : "/tmp";

        var temporaryBase = ManagedTemporaryBase.ForCurrentUser();

        Assert.Equal($"{expectedRoot}/netclaw-{ReadEffectiveUserId()}", temporaryBase.Value);
        Assert.Equal(
            temporaryBase,
            Assert.IsType<ManagedTemporaryLayout.PrivateBase>(ManagedTemporaryLayout.ForHost()).Base);
    }

    [Theory]
    [InlineData("relative/netclaw-1000")]
    [InlineData("/tmp/one/../netclaw-1000")]
    public void Base_rejects_a_relative_or_noncanonical_path(string value)
    {
        Assert.Throws<ArgumentException>(() => new ManagedTemporaryBase(value));
    }

    private static string Hex(string text, int length)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..length];

    private static string ReadEffectiveUserId()
    {
        using var process = Process.Start(new ProcessStartInfo("id", "-u")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false
        })!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return output;
    }
}

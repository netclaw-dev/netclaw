// -----------------------------------------------------------------------
// <copyright file="ManagedTemporaryFolderCheckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Netclaw.Actors.Tools;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Issue #2554, Fix B: each private-folder check of
/// <see cref="ManagedTemporaryEnvironment"/> refuses the launch when it fails.
/// A fake inspector forces each failure, which a test cannot make on a real
/// filesystem without root (another owner). The file is linked into
/// <c>Netclaw.Actors.MutationTests</c> for the <c>path-access</c> Stryker gate.
/// </summary>
public sealed class ManagedTemporaryFolderCheckTests : IDisposable
{
    private const uint DaemonUser = 1000;
    private const uint OtherUser = 1001;
    private const UnixFileMode PrivateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly DisposableTempDir _dir = new();
    private readonly string _envelope;
    private readonly string _base;

    public ManagedTemporaryFolderCheckTests()
    {
        _envelope = Path.Combine(_dir.Path, "sessions", "session-a");
        _base = Path.Combine(_dir.Path, "netclaw-base");
        Directory.CreateDirectory(_envelope);
    }

    // Created per test, after the Windows guard: the private base is a POSIX layout.
    private SessionStoragePaths Storage()
        => SessionStoragePaths.CreateVersion2(
            new SessionStorageEnvelopeRoot(_envelope),
            new ManagedTemporaryLayout.PrivateBase(new ManagedTemporaryBase(_base)));

    public void Dispose() => _dir.Dispose();

    public enum Folder
    {
        Base,
        Key
    }

    public enum Fault
    {
        Link,
        Owner,
        GroupRead,
        GroupWrite,
        OtherRead,
        OtherExecute,
        Missing
    }

    public static TheoryData<Folder, Fault, string> FaultCases => new()
    {
        { Folder.Base, Fault.Link, "symbolic link" },
        { Folder.Base, Fault.Owner, "owned by" },
        { Folder.Base, Fault.GroupRead, "group or other" },
        { Folder.Base, Fault.GroupWrite, "group or other" },
        { Folder.Base, Fault.OtherRead, "group or other" },
        { Folder.Base, Fault.OtherExecute, "group or other" },
        { Folder.Base, Fault.Missing, "Error" },
        { Folder.Key, Fault.Link, "symbolic link" },
        { Folder.Key, Fault.Owner, "owned by" },
        { Folder.Key, Fault.GroupRead, "group or other" },
        { Folder.Key, Fault.GroupWrite, "group or other" },
        { Folder.Key, Fault.OtherRead, "group or other" },
        { Folder.Key, Fault.OtherExecute, "group or other" },
        { Folder.Key, Fault.Missing, "Error" },
    };

    [Theory]
    [MemberData(nameof(FaultCases))]
    public void A_failed_folder_check_refuses_the_launch(Folder folder, Fault fault, string cause)
    {
        // The private base exists only on POSIX hosts. The mutation project has no skip attribute.
        if (OperatingSystem.IsWindows())
            return;

        var storage = Storage();
        var path = folder == Folder.Base ? _base : storage.ManagedTemporary.StorageRoot.Value;
        var inspector = new FakeInspector(DaemonUser);
        inspector.Override(path, fault);
        var startInfo = CreateStartInfoWithoutTemporaryVariables();

        var error = ManagedTemporaryEnvironment.Prepare(startInfo, storage.ManagedTemporary, inspector);

        Assert.NotNull(error);
        Assert.StartsWith("Error:", error, StringComparison.Ordinal);
        Assert.Contains(path, error, StringComparison.Ordinal);
        Assert.Contains(cause, error, StringComparison.OrdinalIgnoreCase);
        Assert.False(startInfo.Environment.ContainsKey("TMPDIR"));
        Assert.False(startInfo.Environment.ContainsKey("TMP"));
        Assert.False(startInfo.Environment.ContainsKey("TEMP"));
        Assert.False(Directory.Exists(Path.Combine(_envelope, "tmp")));
    }

    [Fact]
    public void Private_folders_owned_by_the_daemon_user_allow_the_launch()
    {
        if (OperatingSystem.IsWindows())
            return;

        var storage = Storage();
        var inspector = new FakeInspector(DaemonUser);
        var startInfo = CreateStartInfoWithoutTemporaryVariables();

        var error = ManagedTemporaryEnvironment.Prepare(startInfo, storage.ManagedTemporary, inspector);

        Assert.Null(error);
        Assert.Equal(storage.ManagedTemporary.Directory.Value, startInfo.Environment["TMPDIR"]);
        Assert.Contains(_base, inspector.Inspected);
        Assert.Contains(storage.ManagedTemporary.StorageRoot.Value, inspector.Inspected);
    }

    private static ProcessStartInfo CreateStartInfoWithoutTemporaryVariables()
    {
        var startInfo = new ProcessStartInfo();
        startInfo.Environment.Remove("TMPDIR");
        startInfo.Environment.Remove("TMP");
        startInfo.Environment.Remove("TEMP");
        return startInfo;
    }

    /// <summary>
    /// Reports a private folder owned by the daemon user for each path, except
    /// for one forced fault.
    /// </summary>
    private sealed class FakeInspector(uint effectiveUserId) : IManagedTemporaryFolderInspector
    {
        private readonly Dictionary<string, Fault> _faults = new(StringComparer.Ordinal);

        public List<string> Inspected { get; } = [];

        public uint EffectiveUserId { get; } = effectiveUserId;

        public void Override(string path, Fault fault) => _faults[path] = fault;

        public bool TryInspect(string path, out ManagedTemporaryFolderFacts facts)
        {
            Inspected.Add(path);
            const UnixFileMode mode = PrivateMode;
            if (!_faults.TryGetValue(path, out var fault))
            {
                facts = new ManagedTemporaryFolderFacts(IsSymbolicLink: false, EffectiveUserId, mode);
                return true;
            }

            facts = fault switch
            {
                Fault.Link => new ManagedTemporaryFolderFacts(true, EffectiveUserId, mode),
                Fault.Owner => new ManagedTemporaryFolderFacts(false, OtherUser, mode),
                Fault.GroupRead => new ManagedTemporaryFolderFacts(false, EffectiveUserId, mode | UnixFileMode.GroupRead),
                Fault.GroupWrite => new ManagedTemporaryFolderFacts(false, EffectiveUserId, mode | UnixFileMode.GroupWrite),
                Fault.OtherRead => new ManagedTemporaryFolderFacts(false, EffectiveUserId, mode | UnixFileMode.OtherRead),
                Fault.OtherExecute => new ManagedTemporaryFolderFacts(false, EffectiveUserId, mode | UnixFileMode.OtherExecute),
                _ => default
            };
            return fault != Fault.Missing;
        }
    }
}

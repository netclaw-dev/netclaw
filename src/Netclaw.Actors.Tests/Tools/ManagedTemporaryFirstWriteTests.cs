// -----------------------------------------------------------------------
// <copyright file="ManagedTemporaryFirstWriteTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// PR #2559 review item B1. A file tool can be the first writer below the private
/// managed temporary base. A Team session has no shell, and the skill tells the agent to
/// use <c>file_write</c> in <c>temp_dir</c>. That write must leave the base, the key folder,
/// and the run folder without a group or other permission bit. After the write, the shell
/// launch check of this session and of another session must still pass.
/// </summary>
/// <remarks>
/// The test calls the tool directly with a fresh base. A resolve-time step is not
/// enough: a host cleaner can remove the folders of an idle session after the resolve,
/// and the next file tool write then makes them again. The base is below the private
/// temp folder of the test, never the <c>/tmp/netclaw-&lt;uid&gt;</c> folder of a live
/// daemon. The test proves the defect on a host with the usual umask (0022).
/// The negative rows make the folders before the write, with an open mode or a link.
/// </remarks>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class ManagedTemporaryFirstWriteTests : IDisposable
{
    private const UnixFileMode GroupAndOtherBits =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly string _base;
    private readonly SessionStoragePaths _storage;
    private readonly SessionStoragePaths _otherStorage;

    public ManagedTemporaryFirstWriteTests()
    {
        _paths = new NetclawPaths(Path.Combine(_dir.Path, "home"));
        _base = Path.Combine(_dir.Path, "netclaw-base");
        var layout = new ManagedTemporaryLayout.PrivateBase(new ManagedTemporaryBase(_base));
        _storage = SessionStoragePaths.CreateVersion2(
            new SessionStorageEnvelopeRoot(Path.Combine(_paths.SessionsDirectory, "session-a")),
            layout);
        _otherStorage = SessionStoragePaths.CreateVersion2(
            new SessionStorageEnvelopeRoot(Path.Combine(_paths.SessionsDirectory, "session-b")),
            layout);
    }

    public static bool IsPosix => !OperatingSystem.IsWindows();

    public void Dispose() => _dir.Dispose();

    [SlopwatchSuppress("SW001", "The private base exists only on POSIX hosts.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The private base exists only on POSIX hosts.")]
    [InlineData(TrustAudience.Personal, FileWriteTool.ToolName, false)]
    [InlineData(TrustAudience.Team, FileWriteTool.ToolName, false)]
    [InlineData(TrustAudience.Team, FileEditTool.ToolName, false)]
    [InlineData(TrustAudience.Team, FileWriteTool.ToolName, true)]
    public async Task A_first_file_tool_write_to_temp_dir_keeps_the_folders_private_and_the_shell_working(
        TrustAudience audience,
        string toolName,
        bool childRun)
    {
        var run = childRun
            ? _storage.ForChild(new SubAgentRunId("run-7"), new SubAgentScopeId("s/subagent/a/run-7"))
            : _storage;
        var runFolder = run.ManagedTemporary.Directory.Value;
        var keyFolder = run.ManagedTemporary.StorageRoot.Value;
        var target = Path.Combine(runFolder, "notes.txt");
        Assert.False(Directory.Exists(_base), "No shell call ran, so the base must not exist yet.");

        var context = Context(audience, run);
        var policy = new PathAccessPolicy(new ToolConfig(), _paths, new ToolPathPolicy([]));
        var input = ToolInput.Create("Path", target, "Content", "notes");
        var result = toolName == FileEditTool.ToolName
            ? await new FileEditTool(policy).ExecuteAsync(input, context, TestContext.Current.CancellationToken)
            : await new FileWriteTool(policy).ExecuteAsync(input, context, TestContext.Current.CancellationToken);

        Assert.True(
            context.Receipt?.Category == ToolInvocationOutcomeCategory.Success,
            $"The write to temp_dir must succeed: {result}");
        Assert.Equal("notes", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));

        foreach (var folder in new[] { _base, keyFolder, runFolder })
        {
            var mode = File.GetUnixFileMode(folder);
            Assert.True(
                (mode & GroupAndOtherBits) == 0,
                $"The file tool left '{folder}' with group or other access ({mode}).");
        }

        Assert.Null(ManagedTemporaryEnvironment.Prepare(new ProcessStartInfo("true"), run.ManagedTemporary));
        Assert.Null(ManagedTemporaryEnvironment.Prepare(new ProcessStartInfo("true"), _otherStorage.ManagedTemporary));
    }

    /// <summary>The unsafe shape that another local user leaves before the first write.</summary>
    public enum ForeignFolders
    {
        /// <summary>The base, the key folder, and the run folder exist with mode 0777.</summary>
        OpenModes,

        /// <summary>The base is a link to a folder of mode 0700. The key and run folders are 0700.</summary>
        LinkedBase,

        /// <summary>The base is 0700. The key folder is a link to a folder of mode 0700.</summary>
        LinkedKeyFolder,
    }

    // Negative example (PR #2559 review item B1-R): another local user can make the full
    // path below the base before the daemon does. The folders then exist, but they are open
    // or linked. A file tool write into temp_dir must refuse, not write into those folders.
    // The owner check is not tested here: a test without root cannot make a folder that
    // another user owns. Mode 0777 stands in for it, because the same guard skips both checks.
    [SlopwatchSuppress("SW001", "The private base exists only on POSIX hosts.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The private base exists only on POSIX hosts.")]
    [InlineData(TrustAudience.Team, FileWriteTool.ToolName, ForeignFolders.OpenModes)]
    [InlineData(TrustAudience.Team, FileEditTool.ToolName, ForeignFolders.OpenModes)]
    [InlineData(TrustAudience.Personal, FileWriteTool.ToolName, ForeignFolders.OpenModes)]
    [InlineData(TrustAudience.Personal, FileEditTool.ToolName, ForeignFolders.OpenModes)]
    [InlineData(TrustAudience.Team, FileWriteTool.ToolName, ForeignFolders.LinkedBase)]
    [InlineData(TrustAudience.Team, FileEditTool.ToolName, ForeignFolders.LinkedBase)]
    [InlineData(TrustAudience.Personal, FileWriteTool.ToolName, ForeignFolders.LinkedBase)]
    [InlineData(TrustAudience.Personal, FileEditTool.ToolName, ForeignFolders.LinkedBase)]
    [InlineData(TrustAudience.Team, FileWriteTool.ToolName, ForeignFolders.LinkedKeyFolder)]
    [InlineData(TrustAudience.Personal, FileWriteTool.ToolName, ForeignFolders.LinkedKeyFolder)]
    public async Task A_file_tool_write_to_temp_dir_refuses_folders_that_exist_but_are_not_private(
        TrustAudience audience,
        string toolName,
        ForeignFolders shape)
    {
        var runFolder = _storage.ManagedTemporary.Directory.Value;
        var keyFolder = _storage.ManagedTemporary.StorageRoot.Value;
        var target = Path.Combine(runFolder, "notes.txt");
        PlantForeignFolders(shape, keyFolder, runFolder);
        Assert.True(Directory.Exists(runFolder), "The test must make the full path before the write.");

        var context = Context(audience, _storage);
        var policy = new PathAccessPolicy(new ToolConfig(), _paths, new ToolPathPolicy([]));
        var input = ToolInput.Create("Path", target, "Content", "notes");
        var result = toolName == FileEditTool.ToolName
            ? await new FileEditTool(policy).ExecuteAsync(input, context, TestContext.Current.CancellationToken)
            : await new FileWriteTool(policy).ExecuteAsync(input, context, TestContext.Current.CancellationToken);

        Assert.True(
            context.Receipt?.Category == ToolInvocationOutcomeCategory.AccessDenied,
            $"A {audience} {toolName} into folders of shape {shape} must be refused, got {context.Receipt?.Category}: {result}");
        Assert.False(File.Exists(target), $"The refused write must not make '{target}'.");
        Assert.Empty(Directory.EnumerateFileSystemEntries(runFolder));
    }

    // Negative example: a targeted edit must refuse the same folders. The test seeds the file;
    // it stands in for a file that another local user planted in the open or linked folders.
    [SlopwatchSuppress("SW001", "The private base exists only on POSIX hosts.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "The private base exists only on POSIX hosts.")]
    [InlineData(TrustAudience.Team, ForeignFolders.OpenModes)]
    [InlineData(TrustAudience.Personal, ForeignFolders.OpenModes)]
    [InlineData(TrustAudience.Team, ForeignFolders.LinkedBase)]
    [InlineData(TrustAudience.Personal, ForeignFolders.LinkedBase)]
    public async Task A_targeted_file_edit_in_temp_dir_refuses_folders_that_exist_but_are_not_private(
        TrustAudience audience,
        ForeignFolders shape)
    {
        var ct = TestContext.Current.CancellationToken;
        var runFolder = _storage.ManagedTemporary.Directory.Value;
        var keyFolder = _storage.ManagedTemporary.StorageRoot.Value;
        var target = Path.Combine(runFolder, "notes.txt");
        PlantForeignFolders(shape, keyFolder, runFolder);
        await File.WriteAllTextAsync(target, "planted-marker", ct);

        var context = Context(audience, _storage);
        var policy = new PathAccessPolicy(new ToolConfig(), _paths, new ToolPathPolicy([]));
        var result = await new FileEditTool(policy).ExecuteAsync(
            ToolInput.Create("Path", target, "OldString", "planted-marker", "NewString", "edited"),
            context,
            ct);

        Assert.True(
            context.Receipt?.Category == ToolInvocationOutcomeCategory.AccessDenied,
            $"A {audience} targeted file_edit in folders of shape {shape} must be refused, got {context.Receipt?.Category}: {result}");
        Assert.Equal("planted-marker", await File.ReadAllTextAsync(target, ct));
    }

    private void PlantForeignFolders(ForeignFolders shape, string keyFolder, string runFolder)
    {
        const UnixFileMode open = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                  | GroupAndOtherBits;
        const UnixFileMode owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var keyName = Path.GetRelativePath(_base, keyFolder);
        var runName = Path.GetRelativePath(keyFolder, runFolder);

        switch (shape)
        {
            case ForeignFolders.OpenModes:
                Directory.CreateDirectory(runFolder);
                // Set the mode after creation: the umask of the host masks the create mode.
                foreach (var folder in new[] { _base, keyFolder, runFolder })
                    File.SetUnixFileMode(folder, open);
                break;

            case ForeignFolders.LinkedBase:
            {
                var real = Path.Combine(_dir.Path, "planted-base");
                var realRun = Path.Combine(real, keyName, runName);
                Directory.CreateDirectory(realRun);
                foreach (var folder in new[] { real, Path.Combine(real, keyName), realRun })
                    File.SetUnixFileMode(folder, owner);
                Directory.CreateSymbolicLink(_base, real);
                break;
            }

            case ForeignFolders.LinkedKeyFolder:
            {
                Directory.CreateDirectory(_base);
                File.SetUnixFileMode(_base, owner);
                var real = Path.Combine(_dir.Path, "planted-key");
                var realRun = Path.Combine(real, runName);
                Directory.CreateDirectory(realRun);
                foreach (var folder in new[] { real, realRun })
                    File.SetUnixFileMode(folder, owner);
                Directory.CreateSymbolicLink(keyFolder, real);
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }
    }

    private static ToolExecutionContext Context(TrustAudience audience, SessionStoragePaths storage)
        => TestToolExecutionContext.CreateBoundWithStorage(
            "test/session-a",
            storage,
            new TestToolExecutionContextOptions
            {
                Audience = audience,
                Boundary = SecurityPolicyDefaults.ResolveBoundaryFromAudience(audience),
                ChannelType = audience == TrustAudience.Personal ? "signalr" : "slack"
            });
}

// -----------------------------------------------------------------------
// <copyright file="ChildRunFilePathTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// The runtime writes <c>task.md</c> and <c>result.md</c> in the folder of a child run. They carry no work of the user,
/// so a read of them stays out of the recent files. The test covers both storage layouts. The positive cases are the two
/// runtime files. The negative cases are the files that look alike and must stay in the recent files.
/// </summary>
public sealed class ChildRunFilePathTests
{
    // The paths are strings only. The test creates no file. The root does not depend on the temp folder, because the
    // test run changes the temp folder after the class loads.
    private static readonly string Root = Path.Combine(Path.GetPathRoot(Path.GetFullPath(".")) ?? "/", "child-run-file-paths");
    private static readonly string Envelope = Path.Combine(Root, "sessions", "envelope-1");
    private static readonly string LegacySession = Path.Combine(Root, "sessions", "legacy-1");

    public static TheoryData<string, string> RunFiles() => new()
    {
        { "envelope", Path.Combine(Envelope, "subagents", "run-1", "task.md") },
        { "envelope", Path.Combine(Envelope, "subagents", "run-1", "result.md") },
        { "legacy", Path.Combine(LegacySession, "subagents", "run-1", "task.md") },
        { "legacy", Path.Combine(LegacySession, "subagents", "run-1", "result.md") }
    };

    public static TheoryData<string, string> OtherFiles() => new()
    {
        // A project file with the same name is user work.
        { "envelope", Path.Combine(Root, "project", "result.md") },
        { "legacy", Path.Combine(Root, "project", "task.md") },
        // Other files of the run are not the two runtime files.
        { "envelope", Path.Combine(Envelope, "subagents", "run-1", "artifacts", "result.md") },
        { "legacy", Path.Combine(LegacySession, "subagents", "run-1", "artifacts", "task.md") },
        { "envelope", Path.Combine(Envelope, "subagents", "run-1", "notes.md") },
        // A run folder of another session is not a run folder of this session.
        { "envelope", Path.Combine(Root, "sessions", "envelope-2", "subagents", "run-1", "result.md") },
        { "legacy", Path.Combine(Root, "sessions", "legacy-2", "subagents", "run-1", "result.md") },
        // A file beside the run folders is not in a run folder.
        { "envelope", Path.Combine(Envelope, "subagents", "result.md") },
        { "legacy", Path.Combine(LegacySession, "result.md") }
    };

    [Theory]
    [MemberData(nameof(RunFiles))]
    public void A_runtime_written_run_file_is_a_child_run_file(string layout, string path)
        => Assert.True(Storage(layout).IsChildRunFile(path));

    [Theory]
    [MemberData(nameof(OtherFiles))]
    public void A_project_file_or_another_file_of_the_run_is_not_a_child_run_file(string layout, string path)
        => Assert.False(Storage(layout).IsChildRunFile(path));

    // Both layouts answer the same question.
    private static SessionStoragePaths Storage(string layout) => layout == "envelope"
        ? SessionStoragePaths.CreateVersion2(new SessionStorageEnvelopeRoot(Envelope), ManagedTemporaryLayout.InsideSessionStorage.Instance)
        : SessionStoragePaths.CreateLegacy(LegacySession, Path.Combine(Root, "logs"), "legacy-1", ManagedTemporaryLayout.InsideSessionStorage.Instance);
}

// -----------------------------------------------------------------------
// <copyright file="ToolApprovalStoreConcurrencyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Configuration.Tests;

/// <summary>
/// Approval reads take no store lock. These tests prove that a read never
/// makes a write fail, that a read never returns a partial store, and that
/// a version conversion still takes the lock and runs once.
/// </summary>
/// <remarks>
/// Each store instance has a private cache and a private in-process lock, so
/// two instances on one file act like two processes. The cross-process file
/// lock is the only lock that they share. Linux renames over an open file, so
/// the Windows CI run is the real proof of the rename retry.
/// </remarks>
public sealed class ToolApprovalStoreConcurrencyTests : IDisposable
{
    private const string ShellTool = "shell_execute";
    private const int WriteCycles = 100;
    private const int ReaderCount = 2;

    private readonly DisposableTempDir _dir = new();
    private readonly string _file;

    public ToolApprovalStoreConcurrencyTests()
    {
        _file = Path.Combine(_dir.Path, "tool-approvals.json");
    }

    public void Dispose() => _dir.Dispose();

    private static ApprovalShell NativeShell => OperatingSystem.IsWindows()
        ? ApprovalShell.PowerShell
        : ApprovalShell.Bash;

    private static ApprovalEntry Grant(params string[] verbTokens) =>
        ApprovalEntry.CreateTokenPrefix(ApprovalShell.Bash, verbTokens);

    private ToolApprovalStore NewStore(TimeSpan lockTimeout) => new(
        _file,
        TimeProvider.System,
        new ApprovalStoreMigrationContext(NativeShell),
        lockTimeout);

    [Fact]
    public async Task Reads_without_the_lock_never_fail_a_write_or_see_a_partial_store()
    {
        var baseline = Grant("git", "status");
        var toggled = Grant("git", "push");
        var writer = NewStore(TimeSpan.FromSeconds(10));
        Assert.True(writer.AddApproval(TrustAudience.Personal, ShellTool, baseline));

        using var writerDone = new CancellationTokenSource();
        var readers = Enumerable.Range(0, ReaderCount)
            .Select(_ => Task.Run(
                () => ReadUntilWriterIsDone(NewStore(TimeSpan.Zero), writerDone.Token),
                TestContext.Current.CancellationToken))
            .ToArray();

        var writeFailures = new List<string>();
        var writeTask = Task.Run(() =>
        {
            try
            {
                for (var i = 0; i < WriteCycles; i++)
                {
                    var added = writer.TryAddApproval(TrustAudience.Personal, ShellTool, toggled);
                    if (added is not ApprovalStoreChangeResult.Completed { ChangeCount: 1 })
                        writeFailures.Add($"add {i}: {added}");

                    var removed = writer.TryRemoveApproval(TrustAudience.Personal, ShellTool, toggled);
                    if (removed is not ApprovalStoreChangeResult.Completed { ChangeCount: 1 })
                        writeFailures.Add($"remove {i}: {removed}");
                }
            }
            finally
            {
                writerDone.Cancel();
            }
        }, TestContext.Current.CancellationToken);

        await writeTask;
        var readResults = await Task.WhenAll(readers);

        Assert.Empty(writeFailures);
        foreach (var result in readResults)
        {
            Assert.Empty(result.Failures);
            Assert.True(result.ReadCount > 0);
        }

        var final = Assert.Single(writer.GetApprovedEntries(TrustAudience.Personal, ShellTool));
        Assert.True(ToolApprovalEntryComparer.Equals(baseline, final));
    }

    // Every complete store holds the baseline grant, with or without the
    // toggled grant. A partial, empty, or failed read breaks that rule.
    private static ReadResult ReadUntilWriterIsDone(ToolApprovalStore reader, CancellationToken writerDone)
    {
        var failures = new List<string>();
        var reads = 0;
        do
        {
            reads++;
            var result = reader.TryLoad();
            if (result is not ApprovalStoreLoadResult.Ready ready)
            {
                failures.Add($"read {reads}: {result}");
                continue;
            }

            var count = ready.Data.Audiences.TryGetValue("personal", out var tools) &&
                        tools.TryGetValue(ShellTool, out var entries)
                ? entries.Count
                : 0;
            if (count is not (1 or 2))
                failures.Add($"read {reads}: {count} shell grants");
        }
        while (!writerDone.IsCancellationRequested);

        return new ReadResult(reads, failures);
    }

    private sealed record ReadResult(int ReadCount, IReadOnlyList<string> Failures);

    [Fact]
    public void Current_store_read_succeeds_while_another_process_holds_the_lock()
    {
        var writer = NewStore(TimeSpan.Zero);
        Assert.True(writer.AddApproval(TrustAudience.Personal, ShellTool, Grant("git", "status")));
        using var competingLease = HoldLock(writer.LockPath);

        var result = NewStore(TimeSpan.Zero).TryLoad();

        var ready = Assert.IsType<ApprovalStoreLoadResult.Ready>(result);
        Assert.Single(ready.Data.Audiences["personal"][ShellTool]);
    }

    [Fact]
    public void Version_two_store_waits_for_the_lock_before_conversion()
    {
        const string Source = "{\"version\":2,\"audiences\":{}}";
        File.WriteAllText(_file, Source);
        var store = NewStore(TimeSpan.Zero);
        using var competingLease = HoldLock(store.LockPath);

        var result = store.TryLoad();

        var unavailable = Assert.IsType<ApprovalStoreLoadResult.Unavailable>(result);
        Assert.Equal(ApprovalStoreFailure.LockUnavailable, unavailable.Failure);
        Assert.Equal(Source, File.ReadAllText(_file));
        Assert.False(File.Exists(store.V2BackupPath));
    }

    [Fact]
    public void Version_two_store_converts_once_when_two_processes_load_it()
    {
        const string Source = "{\"version\":2,\"audiences\":{\"personal\":{\"shell_execute\":[{\"verb\":\"git push\"}]}}}";
        File.WriteAllText(_file, Source);
        var fileAccess = new CountingFileAccess();
        var first = NewStore(fileAccess);
        var second = NewStore(fileAccess);

        var firstResult = first.TryLoad();
        var secondResult = second.TryLoad();
        var firstAgain = first.TryLoad();

        Assert.IsType<ApprovalStoreLoadResult.Ready>(firstResult);
        var ready = Assert.IsType<ApprovalStoreLoadResult.Ready>(secondResult);
        Assert.IsType<ApprovalStoreLoadResult.Ready>(firstAgain);
        Assert.Single(ready.Data.Audiences["personal"][ShellTool]);
        Assert.Equal(1, fileAccess.Version2Replacements);
        Assert.Equal(1, fileAccess.LockAcquisitions);
        Assert.Equal(Source, File.ReadAllText(Path.Combine(_dir.Path, "tool-approvals.json.v2.bak")));
    }

    private ToolApprovalStore NewStore(CountingFileAccess fileAccess) => new(
        _file,
        TimeProvider.System,
        new ApprovalStoreMigrationContext(NativeShell),
        TimeSpan.Zero,
        fileAccess);

    private static FileStream HoldLock(string lockPath) => new(
        lockPath,
        FileMode.OpenOrCreate,
        FileAccess.ReadWrite,
        FileShare.None);

    private sealed class CountingFileAccess : IApprovalStoreFileAccess
    {
        private readonly IApprovalStoreFileAccess _inner = ApprovalStoreFileAccess.Instance;

        internal int LockAcquisitions { get; private set; }

        internal int Version2Replacements { get; private set; }

        public FileStream AcquireLock(string lockPath, TimeSpan timeout)
        {
            LockAcquisitions++;
            return _inner.AcquireLock(lockPath, timeout);
        }

        public byte[] ReadAllBytes(string path) => _inner.ReadAllBytes(path);

        public void WriteAtomic(string path, string contents, byte[]? expectedSourceBytes) =>
            _inner.WriteAtomic(path, contents, expectedSourceBytes);

        public void ReplaceVersion2(string path, string backupPath, byte[] sourceBytes, string version3Contents)
        {
            Version2Replacements++;
            _inner.ReplaceVersion2(path, backupPath, sourceBytes, version3Contents);
        }

        public void EnsureNotLink(string path) => _inner.EnsureNotLink(path);
    }
}

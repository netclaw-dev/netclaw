// -----------------------------------------------------------------------
// <copyright file="ToolApprovalStoreTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Time.Testing;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class ToolApprovalStoreTests : IDisposable
{
    private readonly string _file;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly ToolApprovalStore _store;

    public ToolApprovalStoreTests()
    {
        _file = Path.Combine(Path.GetTempPath(), $"netclaw-approvals-{Guid.NewGuid():N}.json");
        _store = new ToolApprovalStore(
            _file,
            _time,
            new ApprovalStoreMigrationContext(NativeShell),
            TimeSpan.Zero);
    }

    private static ApprovalShell NativeShell => OperatingSystem.IsWindows()
        ? ApprovalShell.PowerShell
        : ApprovalShell.Bash;

    public void Dispose()
    {
        if (File.Exists(_file)) File.Delete(_file);
        if (File.Exists(_store.MalformedQuarantinePath)) File.Delete(_store.MalformedQuarantinePath);
        if (File.Exists(_store.V1QuarantinePath)) File.Delete(_store.V1QuarantinePath);
        if (File.Exists(_store.V2BackupPath)) File.Delete(_store.V2BackupPath);
        if (File.Exists(_store.LockPath)) File.Delete(_store.LockPath);
    }

    private static ApprovalEntry Verb(string verb) => ApprovalEntry.CreateTokenPrefix(
        ApprovalShell.Bash,
        verb.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static ApprovalEntry InDir(string verb, string dir) => ApprovalEntry.CreateTokenPrefix(
        ApprovalShell.Bash,
        verb.Split(' ', StringSplitOptions.RemoveEmptyEntries),
        dir);

    private static ApprovalEntry NonShellInDir(string verb, string dir) => new(verb) { Directory = dir };

    [Fact]
    public void RemoveApproval_returns_false_when_file_is_empty()
    {
        Assert.False(_store.RemoveApproval(TrustAudience.Personal, "shell_execute", Verb("git push")));
    }

    [Fact]
    public void RemoveApproval_removes_exact_match_and_returns_true()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("git push"));
        _store.AddApproval(TrustAudience.Personal, "shell_execute", InDir("grep", "/home/user/logs/"));

        Assert.True(_store.RemoveApproval(TrustAudience.Personal, "shell_execute", Verb("git push")));

        var remaining = _store.GetApprovedEntries(TrustAudience.Personal, "shell_execute");
        Assert.Single(remaining);
        Assert.Equal("grep", remaining[0].Verb);
        Assert.Equal("/home/user/logs", remaining[0].Directory);
    }

    [Fact]
    public void RemoveApproval_returns_false_for_unknown_entry()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("git push"));
        Assert.False(_store.RemoveApproval(TrustAudience.Personal, "shell_execute", Verb("git pull")));
        Assert.Single(_store.GetApprovedEntries(TrustAudience.Personal, "shell_execute"));
    }

    [Fact]
    public void RemoveApproval_uses_bash_case_sensitivity_on_all_hosts()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("git push"));

        var caseDifferent = _store.RemoveApproval(TrustAudience.Personal, "shell_execute", Verb("GIT PUSH"));

        Assert.False(caseDifferent);
        Assert.Single(_store.GetApprovedEntries(TrustAudience.Personal, "shell_execute"));
    }

    [Fact]
    public void RemoveApproval_prunes_empty_tool_and_audience_sections()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("git push"));
        Assert.True(_store.RemoveApproval(TrustAudience.Personal, "shell_execute", Verb("git push")));

        var snapshot = _store.Snapshot();
        Assert.Empty(snapshot);
    }

    [Fact]
    public void RemoveApproval_does_not_disturb_other_audiences_or_tools()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("git push"));
        _store.AddApproval(TrustAudience.Public, "shell_execute", Verb("git push"));
        var fileScope = Path.Combine(Path.GetTempPath(), "scratch");
        _store.AddApproval(TrustAudience.Personal, "file_write", NonShellInDir("file_write", fileScope));

        Assert.True(_store.RemoveApproval(TrustAudience.Personal, "shell_execute", Verb("git push")));

        Assert.Empty(_store.GetApprovedEntries(TrustAudience.Personal, "shell_execute"));

        var publicShell = _store.GetApprovedEntries(TrustAudience.Public, "shell_execute");
        Assert.Single(publicShell);
        Assert.Equal("git push", publicShell[0].Verb);

        var personalFileWrite = _store.GetApprovedEntries(TrustAudience.Personal, "file_write");
        Assert.Single(personalFileWrite);
        Assert.Equal(Path.GetFullPath(fileScope).TrimEnd(Path.DirectorySeparatorChar), personalFileWrite[0].Directory);
    }

    [Fact]
    public void RemoveAllForTool_clears_every_entry_and_returns_count()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("git push"));
        _store.AddApproval(TrustAudience.Personal, "shell_execute", InDir("grep", "/home/user/logs/"));
        _store.AddApproval(
            TrustAudience.Personal,
            "file_write",
            NonShellInDir("file_write", Path.Combine(Path.GetTempPath(), "scratch")));

        var removed = _store.RemoveAllForTool(TrustAudience.Personal, "shell_execute");

        Assert.Equal(2, removed);
        Assert.Empty(_store.GetApprovedEntries(TrustAudience.Personal, "shell_execute"));
        Assert.Single(_store.GetApprovedEntries(TrustAudience.Personal, "file_write"));
    }

    [Fact]
    public void RemoveAllForTool_returns_zero_when_tool_absent()
    {
        Assert.Equal(0, _store.RemoveAllForTool(TrustAudience.Personal, "shell_execute"));
    }

    [Fact]
    public void Snapshot_returns_deep_clone_independent_of_subsequent_writes()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("git push"));
        var snapshot = _store.Snapshot();

        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("git pull"));

        var personalShell = snapshot["personal"]["shell_execute"];
        Assert.Single(personalShell);
        Assert.Equal("git push", personalShell[0].Verb);
    }

    [Fact]
    public void AddApproval_is_idempotent_for_equal_entries()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("git push"));
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("git push"));

        var entries = _store.GetApprovedEntries(TrustAudience.Personal, "shell_execute");
        Assert.Single(entries);
    }

    [Fact]
    public void AddApproval_normalizes_trailing_slash_in_directory()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", InDir("grep", "/home/user/logs/"));
        _store.AddApproval(TrustAudience.Personal, "shell_execute", InDir("grep", "/home/user/logs"));

        var entries = _store.GetApprovedEntries(TrustAudience.Personal, "shell_execute");
        Assert.Single(entries);
        Assert.Equal("/home/user/logs", entries[0].Directory);
    }

    [Fact]
    public void RemoveApproval_normalizes_trailing_slash_in_pattern_input()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", InDir("grep", "/home/user/logs"));
        Assert.True(_store.RemoveApproval(TrustAudience.Personal, "shell_execute", InDir("grep", "/home/user/logs/")));
        Assert.Empty(_store.GetApprovedEntries(TrustAudience.Personal, "shell_execute"));
    }

    [Fact]
    public void Save_emits_version_three_and_closed_entries()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("freshdesk"));
        _store.AddApproval(TrustAudience.Personal, "shell_execute", InDir("grep", "/home/user/logs"));

        var json = File.ReadAllText(_file);
        Assert.Contains("\"version\": 3", json);
        Assert.Contains("\"verbTokens\": [", json);
        Assert.Contains("\"freshdesk\"", json);
        Assert.Contains("\"directory\": \"/home/user/logs\"", json);
        Assert.Contains("\"directory\": null", json);
    }

    [Fact]
    public void Load_quarantines_v1_file_and_returns_empty()
    {
        const string V1Json = """
            {
              "audiences": {
                "personal": {
                  "shell_execute": [ "git push", "/home/user/logs/" ]
                }
              }
            }
            """;
        File.WriteAllText(_file, V1Json);

        var data = _store.Load();

        Assert.Empty(data.Audiences);
        Assert.Contains("\"version\": 3", File.ReadAllText(_file));
        Assert.True(File.Exists(_store.V1QuarantinePath));
        Assert.Equal(V1Json, File.ReadAllText(_store.V1QuarantinePath));
    }

    [Fact]
    public void Load_quarantines_file_with_wrong_version_number()
    {
        File.WriteAllText(_file, """{"version":1,"audiences":{}}""");

        var data = _store.Load();

        Assert.Empty(data.Audiences);
        Assert.Contains("\"version\": 3", File.ReadAllText(_file));
        Assert.True(File.Exists(_store.V1QuarantinePath));
    }

    [Fact]
    public void TryLoad_keeps_malformed_file_and_returns_unavailable()
    {
        const string Malformed = "not valid json {{{";
        File.WriteAllText(_file, Malformed);

        var result = _store.TryLoad();

        var unavailable = Assert.IsType<ApprovalStoreLoadResult.Unavailable>(result);
        Assert.Equal(ApprovalStoreFailure.InvalidData, unavailable.Failure);
        Assert.Equal(Malformed, File.ReadAllText(_file));
        Assert.False(File.Exists(_store.MalformedQuarantinePath));
        Assert.False(File.Exists(_store.V1QuarantinePath));
    }

    [Fact]
    public void Load_after_v1_quarantine_writes_fresh_v3_file()
    {
        File.WriteAllText(_file, """{"audiences":{"personal":{"shell_execute":["git push"]}}}""");

        // First read quarantines and returns empty.
        Assert.Empty(_store.Load().Audiences);

        // The conversion creates the v3 file. A later write keeps that format.
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("freshdesk"));
        Assert.True(File.Exists(_file));
        Assert.Contains("\"version\": 3", File.ReadAllText(_file));
        Assert.True(File.Exists(_store.V1QuarantinePath));
    }

    [Fact]
    public void V3_file_round_trips_global_wildcard_and_folder_scoped_entries()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("freshdesk"));
        _store.AddApproval(TrustAudience.Personal, "shell_execute", InDir("grep", "/home/user/logs"));

        var reloaded = new ToolApprovalStore(
            _file,
            timeProvider: null,
            migrationContext: new ApprovalStoreMigrationContext(NativeShell),
            lockTimeout: TimeSpan.Zero).GetApprovedEntries(TrustAudience.Personal, "shell_execute");

        Assert.Equal(2, reloaded.Count);
        Assert.Contains(reloaded, e => e.Verb == "freshdesk" && e.Directory is null);
        Assert.Contains(reloaded, e => e.Verb == "grep" && e.Directory == "/home/user/logs");
    }

    [Fact]
    public void AddApproval_stamps_createdAt_with_the_provider_clock()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("git push"));

        var entry = Assert.Single(_store.GetApprovedEntries(TrustAudience.Personal, "shell_execute"));
        Assert.Equal(_time.GetUtcNow(), entry.CreatedAt);
    }

    [Fact]
    public void AddApproval_persists_createdAt_to_disk()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("freshdesk"));

        Assert.Contains("\"createdAt\"", File.ReadAllText(_file));
    }

    [Fact]
    public void AddApproval_idempotent_regrant_preserves_the_original_createdAt()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("git push"));
        var firstStamp = _time.GetUtcNow();

        _time.Advance(TimeSpan.FromDays(7));
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("git push"));

        var entry = Assert.Single(_store.GetApprovedEntries(TrustAudience.Personal, "shell_execute"));
        Assert.Equal(firstStamp, entry.CreatedAt);
    }

    [Fact]
    public void Load_converts_v2_shell_entries_to_legacy_exact()
    {
        File.WriteAllText(_file, """
            {
              "version": 2,
              "audiences": { "personal": { "shell_execute": [ { "verb": "git push" } ] } }
            }
            """);

        var data = _store.Load();

        Assert.Equal(ToolApprovalStore.CurrentSchemaVersion, data.Version);
        var entry = Assert.Single(_store.GetApprovedEntries(TrustAudience.Personal, "shell_execute"));
        Assert.Null(entry.CreatedAt);
        Assert.Equal(NativeShell, entry.Shell);
        Assert.Equal(ApprovalMatchKind.LegacyExact, entry.Match);
        Assert.Equal("git push", entry.Verb);
        Assert.True(File.Exists(_file));
        Assert.True(File.Exists(_store.V2BackupPath));
        Assert.False(File.Exists(_store.V1QuarantinePath));
    }

    [Fact]
    public void Version_two_conversion_keeps_a_byte_identical_backup()
    {
        var source = Encoding.UTF8.GetBytes(
            "{\r\n  \"version\": 2,\r\n  \"audiences\": {}\r\n}\r\n");
        File.WriteAllBytes(_file, source);

        var result = _store.TryLoad();

        Assert.IsType<ApprovalStoreLoadResult.Ready>(result);
        Assert.Equal(source, File.ReadAllBytes(_store.V2BackupPath));
        Assert.Contains("\"version\": 3", File.ReadAllText(_file));
    }

    [Fact]
    public void First_add_against_version_two_converts_and_applies_the_mutation()
    {
        File.WriteAllText(_file, "{\"version\":2,\"audiences\":{}}");

        var result = _store.TryAddApproval(
            TrustAudience.Personal,
            "shell_execute",
            Verb("git push"));

        Assert.Equal(1, Assert.IsType<ApprovalStoreChangeResult.Completed>(result).ChangeCount);
        var entry = Assert.Single(
            _store.GetApprovedEntries(TrustAudience.Personal, "shell_execute"));
        Assert.Equal(["git", "push"], entry.VerbTokens);
        Assert.Contains("\"version\": 3", File.ReadAllText(_file));
    }

    [Fact]
    public void First_revoke_against_version_two_converts_and_applies_the_mutation()
    {
        File.WriteAllText(
            _file,
            "{\"version\":2,\"audiences\":{\"personal\":{\"shell_execute\":[{\"verb\":\"git push\"}]}}}");

        var result = _store.TryRemoveApproval(
            TrustAudience.Personal,
            "shell_execute",
            ApprovalEntry.CreateLegacyExact(NativeShell, "git push"));

        Assert.Equal(1, Assert.IsType<ApprovalStoreChangeResult.Completed>(result).ChangeCount);
        Assert.Empty(_store.GetApprovedEntries(TrustAudience.Personal, "shell_execute"));
        Assert.Contains("\"version\": 3", File.ReadAllText(_file));
    }

    [Fact]
    public void First_add_after_version_one_quarantine_applies_the_mutation()
    {
        File.WriteAllText(_file, "{\"audiences\":{}}");

        var result = _store.TryAddApproval(
            TrustAudience.Personal,
            "file_write",
            ApprovalEntry.CreateNonShell("write"));

        Assert.Equal(1, Assert.IsType<ApprovalStoreChangeResult.Completed>(result).ChangeCount);
        Assert.Single(_store.GetApprovedEntries(TrustAudience.Personal, "file_write"));
        Assert.True(File.Exists(_store.V1QuarantinePath));
    }

    [Fact]
    public void Version_two_conversion_omits_padded_verbs_without_widening_authority()
    {
        File.WriteAllText(_file, """
            {
              "version": 2,
              "audiences": {
                "personal": {
                  "shell_execute": [ { "verb": " git" }, { "verb": "git " } ]
                }
              }
            }
            """);

        var result = _store.TryLoad();

        Assert.IsType<ApprovalStoreLoadResult.Ready>(result);
        Assert.Empty(_store.GetApprovedEntries(TrustAudience.Personal, "shell_execute"));
        Assert.Equal(2, _store.LastMigrationOmittedEntryCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/path")]
    public void Version_two_conversion_omits_unrepresentable_directory_without_global_grant(
        string directory)
    {
        var encodedDirectory = JsonSerializer.Serialize(directory);
        File.WriteAllText(_file, $$"""
            {
              "version": 2,
              "audiences": {
                "personal": {
                  "shell_execute": [ { "verb": "git", "directory": {{encodedDirectory}} } ]
                }
              }
            }
            """);

        var result = _store.TryLoad();

        Assert.IsType<ApprovalStoreLoadResult.Ready>(result);
        Assert.Empty(_store.GetApprovedEntries(TrustAudience.Personal, "shell_execute"));
        Assert.Equal(1, _store.LastMigrationOmittedEntryCount);
    }

    [Fact]
    public void Version_two_conversion_preserves_significant_directory_whitespace()
    {
        var directory = OperatingSystem.IsWindows()
            ? Path.Combine(Path.GetTempPath(), "approval scope", "child")
            : "/work ";
        var encodedDirectory = JsonSerializer.Serialize(directory);
        File.WriteAllText(_file, $$"""
            {
              "version": 2,
              "audiences": {
                "personal": {
                  "shell_execute": [ { "verb": "git", "directory": {{encodedDirectory}} } ]
                }
              }
            }
            """);

        var result = _store.TryLoad();

        Assert.IsType<ApprovalStoreLoadResult.Ready>(result);
        var entry = Assert.Single(_store.GetApprovedEntries(TrustAudience.Personal, "shell_execute"));
        Assert.Equal(Path.GetFullPath(directory), entry.Directory);
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("approval scope", entry.Directory, StringComparison.Ordinal);
        }
        else
        {
            Assert.EndsWith(" ", entry.Directory);
        }
    }

    [Fact]
    public void Version_two_conversion_preserves_file_system_root()
    {
        var root = Assert.IsType<string>(Path.GetPathRoot(Path.GetTempPath()));
        var encodedRoot = JsonSerializer.Serialize(root);
        File.WriteAllText(_file, $$"""
            {
              "version": 2,
              "audiences": {
                "personal": {
                  "shell_execute": [ { "verb": "git", "directory": {{encodedRoot}} } ]
                }
              }
            }
            """);

        var result = _store.TryLoad();

        Assert.IsType<ApprovalStoreLoadResult.Ready>(result);
        var entry = Assert.Single(_store.GetApprovedEntries(TrustAudience.Personal, "shell_execute"));
        Assert.Equal(Path.GetFullPath(root), entry.Directory);
    }

    [Fact]
    public void Version_two_conversion_stops_when_existing_backup_differs()
    {
        const string Source = "{\"version\":2,\"audiences\":{}}";
        File.WriteAllText(_file, Source);
        File.WriteAllText(_store.V2BackupPath, "different source");

        var result = _store.TryLoad();

        var unavailable = Assert.IsType<ApprovalStoreLoadResult.Unavailable>(result);
        Assert.Equal(ApprovalStoreFailure.MigrationFailed, unavailable.Failure);
        Assert.Equal(Source, File.ReadAllText(_file));
        Assert.Equal("different source", File.ReadAllText(_store.V2BackupPath));
    }

    [Fact]
    public void Version_two_conversion_can_retry_after_replace_fails_with_completed_backup()
    {
        var source = Encoding.UTF8.GetBytes(
            "{\r\n  \"version\": 2,\r\n  \"audiences\": {}\r\n}\r\n");
        File.WriteAllBytes(_file, source);
        var fileAccess = new InterceptingFileAccess
        {
            FailNextVersion2ReplaceAfterBackup = true,
        };
        var store = CreateStore(fileAccess);

        var failed = store.TryLoad();

        Assert.IsType<ApprovalStoreLoadResult.Unavailable>(failed);
        Assert.Equal(source, File.ReadAllBytes(_file));
        Assert.Equal(source, File.ReadAllBytes(store.V2BackupPath));

        var retried = store.TryLoad();

        Assert.IsType<ApprovalStoreLoadResult.Ready>(retried);
        Assert.Equal(source, File.ReadAllBytes(store.V2BackupPath));
        Assert.Contains("\"version\": 3", File.ReadAllText(_file));
    }

    [Theory]
    [InlineData("{\"version\":4,\"audiences\":{}}", ApprovalStoreFailure.UnsupportedVersion)]
    [InlineData("{\"version\":3}", ApprovalStoreFailure.InvalidData)]
    [InlineData("{\"version\":3,\"version\":3,\"audiences\":{}}", ApprovalStoreFailure.InvalidData)]
    [InlineData("{\"version\":3,\"audiences\":{\"unknown\":{}}}", ApprovalStoreFailure.InvalidData)]
    [InlineData("{\"version\":3,\"audiences\":{\"personal\":{\"shell_execute\":[{\"shell\":\"Bash\",\"match\":\"TokenPrefix\",\"verbTokens\":[],\"directory\":null,\"createdAt\":null}]}}}", ApprovalStoreFailure.InvalidData)]
    public void TryLoad_keeps_invalid_version_three_file_unchanged(
        string source,
        ApprovalStoreFailure expectedFailure)
    {
        File.WriteAllText(_file, source);

        var result = _store.TryLoad();

        var unavailable = Assert.IsType<ApprovalStoreLoadResult.Unavailable>(result);
        Assert.Equal(expectedFailure, unavailable.Failure);
        Assert.Equal(source, File.ReadAllText(_file));
        Assert.False(File.Exists(_store.V2BackupPath));
        Assert.False(File.Exists(_store.V1QuarantinePath));
    }

    [Fact]
    public void TryLoad_reports_lock_contention_without_reading_authority()
    {
        File.WriteAllText(_file, "{\"version\":3,\"audiences\":{}}");
        using var competingLease = new FileStream(
            _store.LockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        var result = _store.TryLoad();

        var unavailable = Assert.IsType<ApprovalStoreLoadResult.Unavailable>(result);
        Assert.Equal(ApprovalStoreFailure.LockUnavailable, unavailable.Failure);
    }

    [Theory]
    [InlineData("{\"version\":3,\"audiences\":{},\"extra\":true}")]
    [InlineData("{\"version\":3,\"audiences\":null}")]
    [InlineData("{\"version\":3,\"audiences\":{\"personal\":null}}")]
    [InlineData("{\"version\":3,\"audiences\":{\"personal\":{},\"personal\":{}}}")]
    [InlineData("{\"version\":3,\"audiences\":{\"personal\":{\"tool\":[],\"tool\":[]}}}")]
    [InlineData("{\"version\":3,\"audiences\":{\"personal\":{\"\":[]}}}")]
    [InlineData("{\"version\":3,\"audiences\":{\"personal\":{\"tool\":null}}}")]
    [InlineData("{\"version\":3,\"audiences\":{\"personal\":{\"tool\":[null]}}}")]
    [InlineData("{\"version\":3,\"audiences\":{\"personal\":{\"shell_execute\":[{\"verb\":\"git\",\"directory\":null,\"createdAt\":null}]}}}")]
    [InlineData("{\"version\":3,\"audiences\":{\"personal\":{\"file_write\":[{\"shell\":\"Bash\",\"match\":\"TokenPrefix\",\"verbTokens\":[\"git\"],\"directory\":null,\"createdAt\":null}]}}}")]
    public void TryLoad_rejects_invalid_whole_file_shape(string source)
    {
        File.WriteAllText(_file, source);

        var result = _store.TryLoad();

        var unavailable = Assert.IsType<ApprovalStoreLoadResult.Unavailable>(result);
        Assert.Equal(ApprovalStoreFailure.InvalidData, unavailable.Failure);
        Assert.Equal(source, File.ReadAllText(_file));
    }

    [Fact]
    public void Cache_detects_same_size_external_replacement_with_same_timestamp()
    {
        const string Initial =
            "{\"version\":3,\"audiences\":{\"personal\":{\"file_write\":[{\"verb\":\"alpha\",\"directory\":null,\"createdAt\":null}]}}}";
        const string Replacement =
            "{\"version\":3,\"audiences\":{\"personal\":{\"file_write\":[{\"verb\":\"bravo\",\"directory\":null,\"createdAt\":null}]}}}";
        Assert.Equal(Initial.Length, Replacement.Length);
        File.WriteAllText(_file, Initial);
        var timestamp = File.GetLastWriteTimeUtc(_file);

        var initialEntry = Assert.Single(
            _store.GetApprovedEntries(TrustAudience.Personal, "file_write"));
        Assert.Equal("alpha", initialEntry.Verb);

        File.WriteAllText(_file, Replacement);
        File.SetLastWriteTimeUtc(_file, timestamp);

        var replacementEntry = Assert.Single(
            _store.GetApprovedEntries(TrustAudience.Personal, "file_write"));
        Assert.Equal("bravo", replacementEntry.Verb);
    }

    [Fact]
    public void Custom_shell_tool_round_trips_with_its_configured_key()
    {
        const string ShellToolName = "native_shell";
        var store = new ToolApprovalStore(
            _file,
            _time,
            new ApprovalStoreMigrationContext(ApprovalShell.Bash, ShellToolName),
            TimeSpan.Zero);
        store.AddApproval(
            TrustAudience.Personal,
            ShellToolName,
            ApprovalEntry.CreateTokenPrefix(ApprovalShell.Bash, ["git", "push"]));

        var reloaded = new ToolApprovalStore(
            _file,
            _time,
            new ApprovalStoreMigrationContext(ApprovalShell.Bash, ShellToolName),
            TimeSpan.Zero);

        var entry = Assert.Single(
            reloaded.GetApprovedEntries(TrustAudience.Personal, ShellToolName));
        Assert.Equal(["git", "push"], entry.VerbTokens);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" bad")]
    [InlineData("bad ")]
    [InlineData("bad\nkey")]
    [InlineData("bad\u202ekey")]
    public void Write_rejects_noncanonical_tool_key(string toolName)
    {
        var result = _store.TryAddApproval(
            TrustAudience.Personal,
            toolName,
            new ApprovalEntry("read"));

        var unavailable = Assert.IsType<ApprovalStoreChangeResult.Unavailable>(result);
        Assert.Equal(ApprovalStoreFailure.InvalidData, unavailable.Failure);
        Assert.False(File.Exists(_file));
    }

    [Fact]
    public void New_shell_write_cannot_create_legacy_exact_authority()
    {
        var result = _store.TryAddApproval(
            TrustAudience.Personal,
            "shell_execute",
            new ApprovalEntry("git push"));

        var unavailable = Assert.IsType<ApprovalStoreChangeResult.Unavailable>(result);
        Assert.Equal(ApprovalStoreFailure.InvalidData, unavailable.Failure);
        Assert.False(File.Exists(_file));
    }

    [Fact]
    public void TryLoad_returns_a_snapshot_detached_from_later_writes()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("git push"));
        var ready = Assert.IsType<ApprovalStoreLoadResult.Ready>(_store.TryLoad());

        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("git pull"));

        var entries = ready.Data.Audiences["personal"]["shell_execute"];
        Assert.Single(entries);
        Assert.Equal("git push", entries[0].Verb);
    }

    [Fact]
    public void Failed_write_does_not_publish_phantom_cache_authority_and_can_retry()
    {
        var fileAccess = new InterceptingFileAccess { FailNextWrite = true };
        var store = CreateStore(fileAccess);

        var failed = store.TryAddApproval(
            TrustAudience.Personal,
            "file_write",
            new ApprovalEntry("alpha"));

        Assert.IsType<ApprovalStoreChangeResult.Unavailable>(failed);
        Assert.Empty(store.GetApprovedEntries(TrustAudience.Personal, "file_write"));

        var retry = store.TryAddApproval(
            TrustAudience.Personal,
            "file_write",
            new ApprovalEntry("alpha"));

        Assert.Equal(1, Assert.IsType<ApprovalStoreChangeResult.Completed>(retry).ChangeCount);
        Assert.Equal(
            "alpha",
            Assert.Single(store.GetApprovedEntries(TrustAudience.Personal, "file_write")).Verb);
    }

    [Fact]
    public void Source_change_before_replace_fails_without_stale_or_new_authority()
    {
        const string Initial =
            "{\"version\":3,\"audiences\":{\"personal\":{\"file_write\":[{\"verb\":\"alpha\",\"directory\":null,\"createdAt\":null}]}}}";
        const string Replacement =
            "{\"version\":3,\"audiences\":{\"personal\":{\"file_write\":[{\"verb\":\"bravo\",\"directory\":null,\"createdAt\":null}]}}}";
        File.WriteAllText(_file, Initial);
        var fileAccess = new InterceptingFileAccess
        {
            BeforeNextWrite = path => File.WriteAllText(path, Replacement),
        };
        var store = CreateStore(fileAccess);
        Assert.Equal(
            "alpha",
            Assert.Single(store.GetApprovedEntries(TrustAudience.Personal, "file_write")).Verb);

        var result = store.TryAddApproval(
            TrustAudience.Personal,
            "file_write",
            new ApprovalEntry("charlie"));

        Assert.IsType<ApprovalStoreChangeResult.Unavailable>(result);
        var entry = Assert.Single(store.GetApprovedEntries(TrustAudience.Personal, "file_write"));
        Assert.Equal("bravo", entry.Verb);
        Assert.DoesNotContain("charlie", File.ReadAllText(_file), StringComparison.Ordinal);
    }

    [Fact]
    public void ToolApprovalEntryComparer_treats_entries_differing_only_by_createdAt_as_equal()
    {
        var early = new ApprovalEntry("git push") { Directory = "/repo", CreatedAt = _time.GetUtcNow() };
        var late = early with { CreatedAt = _time.GetUtcNow().AddYears(1) };
        var none = early with { CreatedAt = null };

        Assert.True(ToolApprovalEntryComparer.Equals(early, late));
        Assert.True(ToolApprovalEntryComparer.Equals(early, none));
    }

    [Fact]
    public void ToolApprovalEntryComparer_preserves_significant_path_whitespace()
    {
        const string directory = "/approval-scope";
        var withSpace = directory + " ";

        Assert.False(ToolApprovalEntryComparer.Equals(
            InDir("git", directory),
            InDir("git", withSpace)));
        Assert.NotNull(ToolApprovalEntryComparer.NormalizeDirectory(withSpace, ApprovalShell.Bash));
        Assert.EndsWith(" ", ToolApprovalEntryComparer.NormalizeDirectory(withSpace, ApprovalShell.Bash));
    }

    private ToolApprovalStore CreateStore(IApprovalStoreFileAccess fileAccess) => new(
        _file,
        _time,
        new ApprovalStoreMigrationContext(NativeShell),
        TimeSpan.Zero,
        fileAccess);

    private sealed class InterceptingFileAccess : IApprovalStoreFileAccess
    {
        private readonly IApprovalStoreFileAccess _inner = ApprovalStoreFileAccess.Instance;

        internal bool FailNextWrite { get; set; }

        internal bool FailNextVersion2ReplaceAfterBackup { get; set; }

        internal Action<string>? BeforeNextWrite { get; set; }

        public FileStream AcquireLock(string lockPath, TimeSpan timeout) =>
            _inner.AcquireLock(lockPath, timeout);

        public byte[] ReadAllBytes(string path) => _inner.ReadAllBytes(path);

        public void WriteAtomic(string path, string contents, byte[]? expectedSourceBytes)
        {
            var beforeWrite = BeforeNextWrite;
            BeforeNextWrite = null;
            beforeWrite?.Invoke(path);
            if (FailNextWrite)
            {
                FailNextWrite = false;
                throw new IOException("Synthetic write failure.");
            }

            _inner.WriteAtomic(path, contents, expectedSourceBytes);
        }

        public void ReplaceVersion2(
            string path,
            string backupPath,
            byte[] sourceBytes,
            string version3Contents)
        {
            if (FailNextVersion2ReplaceAfterBackup)
            {
                FailNextVersion2ReplaceAfterBackup = false;
                File.WriteAllBytes(backupPath, sourceBytes);
                throw new IOException("Synthetic version-2 replace failure.");
            }

            _inner.ReplaceVersion2(path, backupPath, sourceBytes, version3Contents);
        }

        public void EnsureNotLink(string path) => _inner.EnsureNotLink(path);
    }
}

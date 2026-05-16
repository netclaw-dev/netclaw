// -----------------------------------------------------------------------
// <copyright file="ToolApprovalStoreTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Time.Testing;
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
        _store = new ToolApprovalStore(_file, _time);
    }

    public void Dispose()
    {
        if (File.Exists(_file)) File.Delete(_file);
        if (File.Exists(_store.MalformedQuarantinePath)) File.Delete(_store.MalformedQuarantinePath);
        if (File.Exists(_store.V1QuarantinePath)) File.Delete(_store.V1QuarantinePath);
    }

    private static ApprovalEntry Verb(string verb) => new(verb) { Directory = null };
    private static ApprovalEntry InDir(string verb, string dir) => new(verb) { Directory = dir };

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
    public void RemoveApproval_uses_platform_case_sensitivity()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("git push"));

        var caseDifferent = _store.RemoveApproval(TrustAudience.Personal, "shell_execute", Verb("GIT PUSH"));

        if (OperatingSystem.IsWindows())
        {
            Assert.True(caseDifferent);
            Assert.Empty(_store.GetApprovedEntries(TrustAudience.Personal, "shell_execute"));
        }
        else
        {
            Assert.False(caseDifferent);
            Assert.Single(_store.GetApprovedEntries(TrustAudience.Personal, "shell_execute"));
        }
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
        _store.AddApproval(TrustAudience.Personal, "file_write", InDir("file_write", "/tmp/scratch/"));

        Assert.True(_store.RemoveApproval(TrustAudience.Personal, "shell_execute", Verb("git push")));

        Assert.Empty(_store.GetApprovedEntries(TrustAudience.Personal, "shell_execute"));

        var publicShell = _store.GetApprovedEntries(TrustAudience.Public, "shell_execute");
        Assert.Single(publicShell);
        Assert.Equal("git push", publicShell[0].Verb);

        var personalFileWrite = _store.GetApprovedEntries(TrustAudience.Personal, "file_write");
        Assert.Single(personalFileWrite);
        Assert.Equal("/tmp/scratch", personalFileWrite[0].Directory);
    }

    [Fact]
    public void RemoveAllForTool_clears_every_entry_and_returns_count()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("git push"));
        _store.AddApproval(TrustAudience.Personal, "shell_execute", InDir("grep", "/home/user/logs/"));
        _store.AddApproval(TrustAudience.Personal, "file_write", InDir("file_write", "/tmp/scratch/"));

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
    public void Save_emits_version_two_and_typed_entries()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("freshdesk"));
        _store.AddApproval(TrustAudience.Personal, "shell_execute", InDir("grep", "/home/user/logs"));

        var json = File.ReadAllText(_file);
        Assert.Contains("\"version\": 2", json);
        Assert.Contains("\"verb\": \"freshdesk\"", json);
        Assert.Contains("\"directory\": \"/home/user/logs\"", json);
        // Global wildcard omits the directory field via WhenWritingNull.
        Assert.DoesNotContain("\"directory\": null", json);
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
        Assert.False(File.Exists(_file));
        Assert.True(File.Exists(_store.V1QuarantinePath));
        Assert.Equal(V1Json, File.ReadAllText(_store.V1QuarantinePath));
    }

    [Fact]
    public void Load_quarantines_file_with_wrong_version_number()
    {
        File.WriteAllText(_file, """{"version":1,"audiences":{}}""");

        var data = _store.Load();

        Assert.Empty(data.Audiences);
        Assert.False(File.Exists(_file));
        Assert.True(File.Exists(_store.V1QuarantinePath));
    }

    [Fact]
    public void Load_quarantines_malformed_file_to_invalid_path()
    {
        File.WriteAllText(_file, "not valid json {{{");

        var data = _store.Load();

        Assert.Empty(data.Audiences);
        Assert.False(File.Exists(_file));
        Assert.True(File.Exists(_store.MalformedQuarantinePath));
        Assert.False(File.Exists(_store.V1QuarantinePath));
    }

    [Fact]
    public void Load_after_quarantine_writes_fresh_v2_file_on_next_persist()
    {
        File.WriteAllText(_file, """{"audiences":{"personal":{"shell_execute":["git push"]}}}""");

        // First read quarantines and returns empty.
        Assert.Empty(_store.Load().Audiences);

        // Next add writes a fresh v2 file at the original path.
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("freshdesk"));
        Assert.True(File.Exists(_file));
        Assert.Contains("\"version\": 2", File.ReadAllText(_file));
        Assert.True(File.Exists(_store.V1QuarantinePath));
    }

    [Fact]
    public void V2_file_round_trips_global_wildcard_and_folder_scoped_entries()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("freshdesk"));
        _store.AddApproval(TrustAudience.Personal, "shell_execute", InDir("grep", "/home/user/logs"));

        var reloaded = new ToolApprovalStore(_file).GetApprovedEntries(TrustAudience.Personal, "shell_execute");

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
    public void Load_reads_v2_entries_without_createdAt_as_null_without_quarantine()
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
        Assert.True(File.Exists(_file));
        Assert.False(File.Exists(_store.V1QuarantinePath));
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
}

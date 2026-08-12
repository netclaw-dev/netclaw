// -----------------------------------------------------------------------
// <copyright file="ApprovalsCommandTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Cli.Approvals;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Approvals;

public sealed class ApprovalsCommandTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly StringWriter _output = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly ToolApprovalStore _store;

    public ApprovalsCommandTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        _store = new ToolApprovalStore(
            _paths.ToolApprovalsPath,
            _time,
            new ApprovalStoreMigrationContext(ApprovalShell.Bash),
            TimeSpan.Zero);
    }

    public void Dispose()
    {
        _output.Dispose();
        _dir.Dispose();
    }

    private static ApprovalEntry Verb(string verb) => ApprovalEntry.CreateTokenPrefix(
        ApprovalShell.Bash,
        verb.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    private static ApprovalEntry InDir(string verb, string dir) => ApprovalEntry.CreateTokenPrefix(
        ApprovalShell.Bash,
        verb.Split(' ', StringSplitOptions.RemoveEmptyEntries),
        dir);

    private void SeedDefault()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("git push"));
        _store.AddApproval(TrustAudience.Personal, "shell_execute", InDir("grep", "/home/user/logs"));
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("npm install"));
        _store.AddApproval(
            TrustAudience.Personal,
            "file_write",
            new ApprovalEntry("file_write") { Directory = Path.Combine(_dir.Path, "scratch") });
        _store.AddApproval(TrustAudience.Public, "shell_execute", Verb("ls"));
    }

    [Fact]
    public async Task List_empty_file_prints_message_and_exits_zero()
    {
        var exit = await ApprovalsCommand.RunAsync(["approvals", "list"], _paths, _output);

        Assert.Equal(0, exit);
        Assert.Contains("No persistent approvals.", _output.ToString());
    }

    [Fact]
    public async Task List_with_entries_groups_by_audience_and_tool()
    {
        SeedDefault();

        var exit = await ApprovalsCommand.RunAsync(["approvals", "list"], _paths, _output);

        Assert.Equal(0, exit);
        var text = _output.ToString();
        Assert.Contains("personal / shell_execute", text);
        Assert.Contains("personal / file_write", text);
        Assert.Contains("public / shell_execute", text);
        Assert.Contains("Bash token-prefix \"git push\" anywhere", text);
        Assert.Contains("Bash token-prefix \"grep\" in /home/user/logs", text);
    }

    [Fact]
    public async Task List_json_emits_typed_entry_shape()
    {
        SeedDefault();

        await ApprovalsCommand.RunAsync(["approvals", "list", "--json"], _paths, _output);

        using var doc = JsonDocument.Parse(_output.ToString());
        var audiences = doc.RootElement.GetProperty("audiences");
        var personalShell = audiences.GetProperty("personal").GetProperty("shell_execute");
        var phrases = personalShell.EnumerateArray()
            .Select(e => string.Join(" ", e.GetProperty("verbTokens").EnumerateArray().Select(t => t.GetString())))
            .ToList();
        Assert.Contains("git push", phrases);
        Assert.Contains("grep", phrases);
        Assert.Contains("npm install", phrases);

        // The grep entry should carry its directory; "git push" should not.
        var grep = personalShell.EnumerateArray().Single(e =>
            e.GetProperty("verbTokens")[0].GetString() == "grep");
        Assert.Equal("/home/user/logs", grep.GetProperty("directory").GetString());

        var gitPush = personalShell.EnumerateArray().Single(e =>
            e.GetProperty("verbTokens")[0].GetString() == "git");
        Assert.Equal(JsonValueKind.Null, gitPush.GetProperty("directory").ValueKind);
    }

    [Fact]
    public async Task List_shows_relative_creation_time()
    {
        // SeedDefault stamps entries at the fake clock; advancing it makes
        // the rendered relative age deterministic.
        SeedDefault();
        _time.Advance(TimeSpan.FromDays(3));

        var exit = await ApprovalsCommand.RunAsync(["approvals", "list"], _paths, _output, _time);

        Assert.Equal(0, exit);
        Assert.Contains("added 3 days ago", _output.ToString());
    }

    [Fact]
    public async Task List_shows_placeholder_for_entry_without_a_timestamp()
    {
        // A v2 file written before timestamp tracking: the entry has no
        // createdAt property.
        File.WriteAllText(_paths.ToolApprovalsPath, """
            {
              "version": 2,
              "audiences": { "personal": { "shell_execute": [ { "verb": "git push" } ] } }
            }
            """);

        var exit = await ApprovalsCommand.RunAsync(["approvals", "list"], _paths, _output, _time);

        Assert.Equal(0, exit);
        Assert.Contains("added —", _output.ToString());
    }

    [Fact]
    public async Task List_json_round_trips_createdAt()
    {
        File.WriteAllText(_paths.ToolApprovalsPath, """
            {
              "version": 2,
              "audiences": {
                "personal": {
                  "shell_execute": [
                    { "verb": "git push", "directory": null, "createdAt": "2026-05-01T12:00:00+00:00" },
                    { "verb": "npm install", "directory": null }
                  ]
                }
              }
            }
            """);

        await ApprovalsCommand.RunAsync(["approvals", "list", "--json"], _paths, _output, _time);

        using var doc = JsonDocument.Parse(_output.ToString());
        var entries = doc.RootElement
            .GetProperty("audiences").GetProperty("personal").GetProperty("shell_execute");

        var stamped = entries.EnumerateArray().Single(e => e.GetProperty("verb").GetString() == "git push");
        Assert.True(stamped.TryGetProperty("createdAt", out var createdAt));
        Assert.Equal(
            new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero),
            createdAt.GetDateTimeOffset());

        // An entry written before timestamp tracking has an explicit null.
        var legacy = entries.EnumerateArray().Single(e => e.GetProperty("verb").GetString() == "npm install");
        Assert.Equal(JsonValueKind.Null, legacy.GetProperty("createdAt").ValueKind);
    }

    [Fact]
    public async Task List_reports_one_bounded_version_two_omission_off_stdout()
    {
        File.WriteAllText(_paths.ToolApprovalsPath, """
            {
              "version": 2,
              "audiences": {
                "personal": {
                  "shell_execute": [
                    { "verb": " git push", "directory": null },
                    { "verb": "git status", "directory": null }
                  ]
                }
              }
            }
            """);
        using var diagnostics = new StringWriter();

        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "list", "--json"],
            _paths,
            _output,
            _time,
            diagnostics);

        Assert.Equal(0, exit);
        using var _ = JsonDocument.Parse(_output.ToString());
        Assert.Equal(
            "Approval store version-2 conversion omitted 1 unrepresentable entries.",
            diagnostics.ToString().Trim());
    }

    [Fact]
    public async Task List_filters_by_audience_and_tool()
    {
        SeedDefault();

        await ApprovalsCommand.RunAsync(
            ["approvals", "list", "--audience", "personal", "--tool", "shell_execute"],
            _paths, _output);

        var text = _output.ToString();
        Assert.Contains("personal / shell_execute", text);
        Assert.DoesNotContain("file_write", text);
        Assert.DoesNotContain("public / shell_execute", text);
    }

    [Fact]
    public async Task Revoke_global_wildcard_by_anywhere_form_removes_entry()
    {
        SeedDefault();

        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "revoke", "git push anywhere", "--audience", "personal", "--tool", "shell_execute"],
            _paths, _output);

        Assert.Equal(0, exit);
        var remaining = _store.GetApprovedEntries(TrustAudience.Personal, "shell_execute");
        Assert.DoesNotContain(remaining, e => e.Verb == "git push" && e.Directory is null);
        Assert.Contains("Removed 'Bash token-prefix \"git push\" anywhere'", _output.ToString());
    }

    [Fact]
    public async Task Revoke_no_match_exits_one_and_does_not_modify_file()
    {
        SeedDefault();
        var beforeCount = _store.GetApprovedEntries(TrustAudience.Personal, "shell_execute").Count;

        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "revoke", "git pull anywhere", "--audience", "personal", "--tool", "shell_execute"],
            _paths, _output);

        Assert.Equal(1, exit);
        Assert.Equal(beforeCount, _store.GetApprovedEntries(TrustAudience.Personal, "shell_execute").Count);
        Assert.Contains("No matching approval found.", _output.ToString());
    }

    [Fact]
    public async Task Revoke_tool_all_clears_every_entry_for_tool()
    {
        SeedDefault();

        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "revoke", "--tool", "shell_execute", "--all"],
            _paths, _output);

        Assert.Equal(0, exit);
        Assert.Empty(_store.GetApprovedEntries(TrustAudience.Personal, "shell_execute"));
        Assert.Empty(_store.GetApprovedEntries(TrustAudience.Public, "shell_execute"));
        Assert.Single(_store.GetApprovedEntries(TrustAudience.Personal, "file_write"));
    }

    [Fact]
    public async Task Revoke_tool_all_scoped_by_audience_leaves_others_alone()
    {
        SeedDefault();

        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "revoke", "--tool", "shell_execute", "--all", "--audience", "personal"],
            _paths, _output);

        Assert.Equal(0, exit);
        Assert.Empty(_store.GetApprovedEntries(TrustAudience.Personal, "shell_execute"));

        var publicShell = _store.GetApprovedEntries(TrustAudience.Public, "shell_execute");
        Assert.Single(publicShell);
        Assert.Equal("ls", publicShell[0].Verb);
    }

    [Fact]
    public async Task Revoke_all_without_tool_exits_one_and_does_not_modify_file()
    {
        SeedDefault();
        var beforeCount = _store.GetApprovedEntries(TrustAudience.Personal, "shell_execute").Count;

        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "revoke", "--all"],
            _paths, _output);

        Assert.Equal(1, exit);
        Assert.Equal(beforeCount, _store.GetApprovedEntries(TrustAudience.Personal, "shell_execute").Count);
        Assert.Contains("--all requires --tool", _output.ToString());
    }

    [Fact]
    public async Task Unknown_audience_flag_exits_one()
    {
        SeedDefault();

        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "list", "--audience", "foo"],
            _paths, _output);

        Assert.Equal(1, exit);
        Assert.Contains("Unknown audience 'foo'", _output.ToString());
    }

    [Fact]
    public async Task Audience_flag_without_value_exits_one_with_specific_message()
    {
        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "list", "--audience"],
            _paths, _output);

        Assert.Equal(1, exit);
        Assert.Contains("--audience requires a value", _output.ToString());
    }

    [Fact]
    public async Task Tool_flag_without_value_exits_one_with_specific_message()
    {
        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "revoke", "git push", "--tool"],
            _paths, _output);

        Assert.Equal(1, exit);
        Assert.Contains("--tool requires a value", _output.ToString());
    }

    [Fact]
    public async Task Help_subcommand_exits_zero_and_prints_usage()
    {
        var exit = await ApprovalsCommand.RunAsync(["approvals", "help"], _paths, _output);

        Assert.Equal(0, exit);
        Assert.Contains("Usage: netclaw approvals", _output.ToString());
    }

    [Fact]
    public async Task Revoke_old_unscoped_form_rejects_cross_audience_ambiguity()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("ls"));
        _store.AddApproval(TrustAudience.Public, "shell_execute", Verb("ls"));

        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "revoke", "ls anywhere"],
            _paths, _output);

        Assert.Equal(1, exit);
        Assert.Contains("matches more than one typed phrase", _output.ToString());
        Assert.Single(_store.GetApprovedEntries(TrustAudience.Personal, "shell_execute"));
        Assert.Single(_store.GetApprovedEntries(TrustAudience.Public, "shell_execute"));
    }

    // ── Folder-scoped revoke ──

    [Fact]
    public async Task Revoke_folder_scoped_form_removes_entry_with_matching_directory()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", InDir("git remote", "/home/user/repos/foo"));

        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "revoke", "git remote in /home/user/repos/foo"],
            _paths, _output);

        Assert.Equal(0, exit);
        Assert.Empty(_store.GetApprovedEntries(TrustAudience.Personal, "shell_execute"));
    }

    [Fact]
    public async Task Revoke_folder_scoped_form_does_not_match_global_wildcard()
    {
        // The store has a (verb, null) entry; a folder-scoped revoke should not remove it.
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("git remote"));

        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "revoke", "git remote in /home/user/repos/foo"],
            _paths, _output);

        Assert.Equal(1, exit);
        var remaining = _store.GetApprovedEntries(TrustAudience.Personal, "shell_execute");
        Assert.Single(remaining);
        Assert.Null(remaining[0].Directory);
    }

    [Fact]
    public async Task Revoke_unrecognized_pattern_exits_one_without_a_change()
    {
        // No "anywhere" suffix and no " in " separator — not a valid revoke pattern.
        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "revoke", "git remote"],
            _paths, _output);

        Assert.Equal(1, exit);
        Assert.Contains("No matching approval found", _output.ToString());
    }

    // ── trust-verb ──

    [Fact]
    public async Task TrustVerb_adds_global_wildcard_with_default_audience_and_tool()
    {
        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "trust-verb", "freshdesk"],
            _paths, _output);

        Assert.Equal(0, exit);
        var entries = _store.GetApprovedEntries(TrustAudience.Personal, "shell_execute");
        Assert.Single(entries);
        Assert.Equal("freshdesk", entries[0].Verb);
        Assert.Null(entries[0].Directory);
        var expectedShell = OperatingSystem.IsWindows()
            ? ApprovalShell.PowerShell
            : ApprovalShell.Bash;
        Assert.Equal(expectedShell, entries[0].Shell);
        Assert.Contains($"Trusted '{expectedShell} token-prefix \"freshdesk\" anywhere'", _output.ToString());
    }

    [Fact]
    public async Task TrustVerb_is_idempotent_on_repeated_invocation()
    {
        await ApprovalsCommand.RunAsync(["approvals", "trust-verb", "freshdesk"], _paths, _output);
        _output.GetStringBuilder().Clear();

        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "trust-verb", "freshdesk"],
            _paths, _output);

        Assert.Equal(0, exit);
        var entries = _store.GetApprovedEntries(TrustAudience.Personal, "shell_execute");
        Assert.Single(entries);
        Assert.Contains("No changes", _output.ToString());
    }

    [Fact]
    public async Task TrustVerb_honors_audience_and_tool_flags()
    {
        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "trust-verb", "freshdesk", "--audience", "team", "--tool", "shell_execute"],
            _paths, _output);

        Assert.Equal(0, exit);
        Assert.Single(_store.GetApprovedEntries(TrustAudience.Team, "shell_execute"));
        Assert.Empty(_store.GetApprovedEntries(TrustAudience.Personal, "shell_execute"));
    }

    [Fact]
    public async Task TrustVerb_shell_selector_creates_requested_phrase_type()
    {
        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "trust-verb", "Get-Content", "--shell", "powershell"],
            _paths,
            _output);

        Assert.Equal(0, exit);
        var entry = Assert.Single(
            _store.GetApprovedEntries(TrustAudience.Personal, "shell_execute"));
        Assert.Equal(ApprovalShell.PowerShell, entry.Shell);
        Assert.Equal(ApprovalMatchKind.TokenPrefix, entry.Match);
        Assert.Equal(["Get-Content"], entry.VerbTokens);
    }

    [Fact]
    public async Task TrustVerb_abstract_PowerShell_prefers_PowerShell7_canonical_tokens()
    {
        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "trust-verb", "curl", "--shell", "powershell"],
            _paths,
            _output);

        Assert.Equal(0, exit);
        var entry = Assert.Single(
            _store.GetApprovedEntries(TrustAudience.Personal, "shell_execute"));
        Assert.Equal(ApprovalShell.PowerShell, entry.Shell);
        Assert.Equal(["curl"], entry.VerbTokens);
    }

    [Fact]
    public async Task TrustVerb_abstract_PowerShell_uses_legacy_fallback_when_preferred_parse_fails()
    {
        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "trust-verb", "gerr", "--shell", "powershell"],
            _paths,
            _output);

        Assert.Equal(0, exit);
        var entry = Assert.Single(
            _store.GetApprovedEntries(TrustAudience.Personal, "shell_execute"));
        Assert.Equal(ApprovalShell.PowerShell, entry.Shell);
        Assert.Equal(["gerr"], entry.VerbTokens);
    }

    [Theory]
    [InlineData("tool in mode", "tool|in|mode")]
    [InlineData("status anywhere", "status|anywhere")]
    public async Task TrustVerb_allows_static_PowerShell_tokens_that_resemble_scope_labels(
        string phrase,
        string expectedTokens)
    {
        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "trust-verb", phrase, "--shell", "powershell"],
            _paths,
            _output);

        Assert.Equal(0, exit);
        var entry = Assert.Single(
            _store.GetApprovedEntries(TrustAudience.Personal, "shell_execute"));
        Assert.Equal(expectedTokens.Split('|'), entry.VerbTokens);
    }

    [Theory]
    [InlineData("git push --force")]
    [InlineData("MODE=safe git push")]
    [InlineData("git push >out")]
    [InlineData("git status; rm file")]
    [InlineData("git  push")]
    [InlineData(" git push")]
    [InlineData("git push ")]
    public async Task TrustVerb_rejects_shell_effects_without_file_change(string phrase)
    {
        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "trust-verb", phrase, "--shell", "bash"],
            _paths,
            _output);

        Assert.Equal(1, exit);
        Assert.False(File.Exists(_paths.ToolApprovalsPath));
    }

    [Fact]
    public async Task TrustVerb_keeps_non_shell_tool_exact()
    {
        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "trust-verb", "create-page", "--tool", "notion/create-page"],
            _paths,
            _output);

        Assert.Equal(0, exit);
        var entry = Assert.Single(
            _store.GetApprovedEntries(TrustAudience.Personal, "notion/create-page"));
        Assert.Null(entry.Shell);
        Assert.Null(entry.Match);
        Assert.Equal("create-page", entry.Verb);
    }

    [Theory]
    [InlineData("tool in mode")]
    [InlineData("status anywhere")]
    [InlineData("-private-operation")]
    public async Task TrustVerb_keeps_arbitrary_non_shell_phrase_exact(string phrase)
    {
        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "trust-verb", phrase, "--tool", "custom/tool"],
            _paths,
            _output);

        Assert.Equal(0, exit);
        var entry = Assert.Single(
            _store.GetApprovedEntries(TrustAudience.Personal, "custom/tool"));
        Assert.Equal(phrase, entry.Verb);
        Assert.Null(entry.Shell);
    }

    [Fact]
    public async Task TrustVerb_rejects_shell_selector_for_non_shell_tool()
    {
        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "trust-verb", "create-page", "--tool", "notion/create-page", "--shell", "bash"],
            _paths,
            _output);

        Assert.Equal(1, exit);
        Assert.False(File.Exists(_paths.ToolApprovalsPath));
    }

    [Fact]
    public async Task Revoke_old_scope_rejects_ambiguous_typed_phrases()
    {
        _store.AddApproval(
            TrustAudience.Personal,
            "shell_execute",
            ApprovalEntry.CreateTokenPrefix(ApprovalShell.Bash, ["git", "push"]));
        _store.AddApproval(
            TrustAudience.Personal,
            "shell_execute",
            ApprovalEntry.CreateLegacyExact(ApprovalShell.Bash, "git push"));

        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "revoke", "git push anywhere"],
            _paths,
            _output);

        Assert.Equal(1, exit);
        Assert.Contains("matches more than one typed phrase", _output.ToString());
        Assert.Equal(2, _store.GetApprovedEntries(TrustAudience.Personal, "shell_execute").Count);
    }

    [Fact]
    public async Task Revoke_typed_label_preserves_significant_directory_space()
    {
        var entry = ApprovalEntry.CreateTokenPrefix(
            ApprovalShell.Bash,
            ["git", "status"],
            "/work/repo ");
        _store.AddApproval(TrustAudience.Personal, "shell_execute", entry);

        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "revoke", entry.FormatScope()],
            _paths,
            _output);

        Assert.Equal(0, exit);
        Assert.Empty(_store.GetApprovedEntries(TrustAudience.Personal, "shell_execute"));
    }

    [Fact]
    public async Task Revoke_PowerShell_label_uses_PowerShell_case_rules()
    {
        var entry = ApprovalEntry.CreateTokenPrefix(
            ApprovalShell.PowerShell,
            ["Get-Content"],
            @"C:\Work\Repo");
        _store.AddApproval(TrustAudience.Personal, "shell_execute", entry);

        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "revoke", "powershell token-prefix \"get-content\" in c:\\work\\repo"],
            _paths,
            _output);

        Assert.Equal(0, exit);
        Assert.Empty(_store.GetApprovedEntries(TrustAudience.Personal, "shell_execute"));
    }

    [Fact]
    public async Task Revoke_old_label_matches_verb_that_contains_in()
    {
        var entry = ApprovalEntry.CreateLegacyExact(
            ApprovalShell.Bash,
            "tool in mode",
            "/work/repo");
        _store.AddApproval(TrustAudience.Personal, "shell_execute", entry);

        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "revoke", "tool in mode in /work/repo"],
            _paths,
            _output);

        Assert.Equal(0, exit);
        Assert.Empty(_store.GetApprovedEntries(TrustAudience.Personal, "shell_execute"));
    }

    [Theory]
    [InlineData("list")]
    [InlineData("trust-verb")]
    public async Task Approval_command_fails_closed_for_invalid_store(string operation)
    {
        const string Invalid = "{\"version\":3,\"audiences\":{\"personal\":null}}";
        File.WriteAllText(_paths.ToolApprovalsPath, Invalid);
        var args = operation == "list"
            ? new[] { "approvals", "list" }
            : ["approvals", "trust-verb", "git push", "--shell", "bash"];

        var exit = await ApprovalsCommand.RunAsync(args, _paths, _output);

        Assert.Equal(1, exit);
        Assert.Contains("approval store is unavailable", _output.ToString());
        Assert.Equal(Invalid, File.ReadAllText(_paths.ToolApprovalsPath));
    }

    [Fact]
    public async Task TrustVerb_without_verb_argument_exits_one_with_usage()
    {
        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "trust-verb"],
            _paths, _output);

        Assert.Equal(1, exit);
        Assert.Contains("Usage: netclaw approvals trust-verb", _output.ToString());
    }

    [Fact]
    public async Task TrustVerb_unknown_audience_exits_one()
    {
        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "trust-verb", "freshdesk", "--audience", "bogus"],
            _paths, _output);

        Assert.Equal(1, exit);
        Assert.Contains("Unknown audience 'bogus'", _output.ToString());
    }

    // ── MCP name-form acceptance ──

    [Fact]
    public async Task Revoke_all_resolves_LlmFacing_alias_to_canonical_stored_key()
    {
        // Operator pastes the LLM-facing alias they saw in a transcript
        // — the grant is stored under canonical `notion/create-pages`,
        // and the revoke must still find and remove it.
        _store.AddApproval(TrustAudience.Personal, "notion/create-pages",
            new ApprovalEntry("notion/create-pages") { Directory = null });

        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "revoke", "--tool", "notion__create-pages", "--all", "--audience", "personal"],
            _paths, _output);

        Assert.Equal(0, exit);
        Assert.Empty(_store.GetApprovedEntries(TrustAudience.Personal, "notion/create-pages"));
        Assert.Contains("Removed 1 approval(s)", _output.ToString());
    }

    [Fact]
    public async Task List_filter_by_LlmFacing_alias_matches_canonical_stored_key()
    {
        _store.AddApproval(TrustAudience.Personal, "notion/create-pages",
            new ApprovalEntry("notion/create-pages") { Directory = null });
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("git push"));

        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "list", "--tool", "notion__create-pages"],
            _paths, _output);

        Assert.Equal(0, exit);
        var text = _output.ToString();
        Assert.Contains("notion/create-pages", text);
        Assert.DoesNotContain("git push", text);
    }

    [Fact]
    public async Task TrustVerb_persists_under_canonical_name_when_passed_LlmFacing_alias()
    {
        // If the operator passes the LLM-facing alias to trust-verb, the
        // grant should land under the canonical key so the runtime
        // approval gate — which queries canonical — finds it.
        var exit = await ApprovalsCommand.RunAsync(
            ["approvals", "trust-verb", "freshdesk", "--tool", "notion__create-pages"],
            _paths, _output);

        Assert.Equal(0, exit);
        Assert.Empty(_store.GetApprovedEntries(TrustAudience.Personal, "notion__create-pages"));
        Assert.Single(_store.GetApprovedEntries(TrustAudience.Personal, "notion/create-pages"));
        Assert.Contains("notion/create-pages", _output.ToString());
    }

    // ── help mentions trust-verb ──

    [Fact]
    public async Task Help_lists_trust_verb_subcommand()
    {
        await ApprovalsCommand.RunAsync(["approvals", "help"], _paths, _output);

        var output = _output.ToString();
        Assert.Contains("trust-verb", output);
        Assert.Contains("typed token prefixes", output);
    }
}

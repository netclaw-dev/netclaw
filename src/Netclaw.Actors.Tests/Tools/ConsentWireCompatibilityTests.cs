// -----------------------------------------------------------------------
// <copyright file="ConsentWireCompatibilityTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Google.Protobuf;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Serialization;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Security.Authorization.Consent;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Proves that the Consent types keep the external shapes unchanged: the
/// journal text of an answer, the approval button keys, and the v3 approval
/// store bytes. Each test starts from the recorded earlier shape.
/// </summary>
public sealed class ConsentWireCompatibilityTests
{
    private static readonly DateTimeOffset GrantTime = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    // The earlier ApprovalDecision enum names. The session journal stored
    // them with ToString(), so recovery reads exactly these strings.
    [Theory]
    [InlineData("ApprovedOnce")]
    [InlineData("ApprovedSession")]
    [InlineData("ApprovedAlways")]
    [InlineData("ApprovedEverywhere")]
    [InlineData("Denied")]
    [InlineData("TimedOut")]
    [InlineData("ApprovedRepository")]
    [InlineData("PromptUnavailable")]
    public void Journal_text_round_trips_through_the_answer(string journalText)
    {
        var answer = ConsentAnswerCodec.FromJournalText(journalText);

        Assert.Equal(journalText, ConsentAnswerCodec.ToJournalText(answer));
    }

    [Fact]
    public void Journal_text_maps_to_the_same_answer_as_the_earlier_enum()
    {
        Assert.Equal(ConsentAnswer.Once.Instance, ConsentAnswerCodec.FromJournalText("ApprovedOnce"));
        Assert.Equal(new ConsentAnswer.Grant(GrantScopeKind.Session), ConsentAnswerCodec.FromJournalText("ApprovedSession"));
        Assert.Equal(new ConsentAnswer.Grant(GrantScopeKind.Folder), ConsentAnswerCodec.FromJournalText("ApprovedAlways"));
        Assert.Equal(new ConsentAnswer.Grant(GrantScopeKind.Repository), ConsentAnswerCodec.FromJournalText("ApprovedRepository"));
        Assert.Equal(new ConsentAnswer.Grant(GrantScopeKind.Everywhere), ConsentAnswerCodec.FromJournalText("ApprovedEverywhere"));
        Assert.Equal(ConsentAnswer.Denied, ConsentAnswerCodec.FromJournalText("Denied"));
        Assert.Equal(ConsentAnswer.TimedOut, ConsentAnswerCodec.FromJournalText("TimedOut"));

        // The earlier parse ignored case and fell closed to Denied.
        Assert.Equal(new ConsentAnswer.Grant(GrantScopeKind.Folder), ConsentAnswerCodec.FromJournalText("approvedalways"));
        Assert.Equal(ConsentAnswer.Denied, ConsentAnswerCodec.FromJournalText("ApprovedForever"));
        Assert.Equal(ConsentAnswer.Denied, ConsentAnswerCodec.FromJournalText(string.Empty));
        Assert.Equal(ConsentAnswer.Denied, ConsentAnswerCodec.FromJournalText(null));
    }

    [Fact]
    public void Journal_event_bytes_do_not_change()
    {
        var recorded = new ToolApprovalResolved
        {
            SessionId = new SessionId("C123/1700000000.000001"),
            CallId = "call-resolved-1",
            AuthorizationAttemptId = "auth-fedcba9876543210fedcba9876543210",
            Decision = "ApprovedAlways",
            ResolvedAtMs = 1700000001000
        };
        var written = recorded with
        {
            Decision = ConsentAnswerCodec.ToJournalText(new ConsentAnswer.Grant(GrantScopeKind.Folder))
        };

        Assert.Equal(
            NetclawProtoMapper.ToProto(recorded).ToByteArray(),
            NetclawProtoMapper.ToProto(written).ToByteArray());
    }

    // Every button key that a channel can send. The journal text is what the
    // earlier MapApprovalDecision(key).ToString() produced.
    [Theory]
    [InlineData(ApprovalOptionKeys.ApproveOnce, "ApprovedOnce")]
    [InlineData(ApprovalOptionKeys.ApproveSession, "ApprovedSession")]
    [InlineData(ApprovalOptionKeys.ApproveAlways, "ApprovedAlways")]
    [InlineData(ApprovalOptionKeys.ApproveRepository, "ApprovedRepository")]
    [InlineData(ApprovalOptionKeys.ApproveEverywhere, "ApprovedEverywhere")]
    [InlineData(ApprovalOptionKeys.ApproveAssignmentSessionV1, "ApprovedSession")]
    [InlineData(ApprovalOptionKeys.ApproveAssignmentAlwaysV1, "ApprovedAlways")]
    [InlineData(ApprovalOptionKeys.ApproveAssignmentRepositoryV1, "ApprovedRepository")]
    [InlineData(ApprovalOptionKeys.ApproveAssignmentEverywhereV1, "ApprovedEverywhere")]
    [InlineData(ApprovalOptionKeys.Deny, "Denied")]
    [InlineData("approve_forever", "Denied")]
    public void Button_key_selects_the_same_answer_as_before(string optionKey, string journalText)
        => Assert.Equal(journalText, ConsentAnswerCodec.ToJournalText(ConsentAnswerCodec.FromOptionKey(optionKey)));

    [Fact]
    public void Stored_entry_scope_reads_the_v3_fields()
    {
        Assert.Equal(
            new GrantScope.Folder("/work/repo"),
            GrantScope.OfStoredEntry(ApprovalEntry.CreateTokenPrefix(ApprovalShell.Bash, ["git"], "/work/repo")));
        Assert.Equal(
            GrantScope.Everywhere.Instance,
            GrantScope.OfStoredEntry(ApprovalEntry.CreateTokenPrefix(ApprovalShell.Bash, ["git"])));
        Assert.Equal(
            new GrantScope.Repository("/work/main/.git"),
            GrantScope.OfStoredEntry(ApprovalEntry.CreateRepositoryTokenPrefix(ApprovalShell.Bash, ["git"], "/work/main/.git")));
        Assert.Equal(
            new GrantScope.Folder("/work/repo"),
            GrantScope.OfStoredEntry(ApprovalEntry.CreateNonShell("file_write", "/work/repo")));
    }

    [Fact]
    public void V3_store_bytes_survive_a_round_trip_through_grant_scopes()
    {
        var root = Directory.CreateTempSubdirectory("netclaw-consent-wire-");
        try
        {
            var digest = new ApprovalAssignmentDigest($"sha256:{new string('c', 64)}");
            // The v3 store keeps the path style of each entry: Bash folders are
            // POSIX paths and PowerShell folders are Windows paths on every
            // host. A non-shell folder is an absolute path of the host.
            var hostDirectory = Path.Combine(root.FullName, "repo");
            ApprovalEntry[] shellEntries =
            [
                ApprovalEntry.CreateTokenPrefix(ApprovalShell.Bash, ["git", "status"], "/work/repo"),
                ApprovalEntry.CreateTokenPrefix(ApprovalShell.Bash, ["curl"]),
                ApprovalEntry.CreateTokenPrefix(ApprovalShell.Bash, ["make", "test"], "/work/repo", assignmentDigest: digest),
                ApprovalEntry.CreateTokenPrefix(ApprovalShell.PowerShell, ["Get-ChildItem"], @"C:\work\repo"),
                ApprovalEntry.CreateTokenPrefix(ApprovalShell.PowerShell, ["Invoke-Build"], assignmentDigest: digest),
            ];
            ApprovalEntry[] nonShellEntries =
            [
                ApprovalEntry.CreateNonShell("file_write", hostDirectory),
                ApprovalEntry.CreateNonShell("notion/notion-create-pages"),
            ];

            // The earlier grant path wrote these entries directly.
            var recordedPath = Path.Combine(root.FullName, "recorded.json");
            var recorded = CreateStore(recordedPath);
            recorded.AddApprovals(TrustAudience.Personal, ShellTool.ToolName, shellEntries);
            recorded.AddApprovals(TrustAudience.Personal, "file_write", [nonShellEntries[0]]);
            recorded.AddApprovals(TrustAudience.Personal, "notion/notion-create-pages", [nonShellEntries[1]]);

            // The Consent path reads each stored scope, rebuilds the grant, and
            // converts it back to an entry the way the approval actor does.
            var rewrittenPath = Path.Combine(root.FullName, "rewritten.json");
            var rewritten = CreateStore(rewrittenPath);
            rewritten.AddApprovals(TrustAudience.Personal, ShellTool.ToolName, Rewrite(ShellTool.ToolName, shellEntries));
            rewritten.AddApprovals(TrustAudience.Personal, "file_write", Rewrite("file_write", [nonShellEntries[0]]));
            rewritten.AddApprovals(
                TrustAudience.Personal,
                "notion/notion-create-pages",
                Rewrite("notion/notion-create-pages", [nonShellEntries[1]]));

            var recordedText = File.ReadAllText(recordedPath);
            Assert.Contains("\"directory\": \"/work/repo\"", recordedText, StringComparison.Ordinal);
            Assert.Contains("\"directory\": \"C:\\\\work\\\\repo\"", recordedText, StringComparison.Ordinal);
            Assert.Contains(System.Text.Json.JsonSerializer.Serialize(hostDirectory), recordedText, StringComparison.Ordinal);
            Assert.DoesNotContain("\"repository\"", recordedText, StringComparison.Ordinal);
            Assert.Equal(File.ReadAllBytes(recordedPath), File.ReadAllBytes(rewrittenPath));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static IReadOnlyList<ApprovalEntry> Rewrite(string toolName, IReadOnlyList<ApprovalEntry> entries)
    {
        var grants = entries
            .Select(entry => new ToolApprovalGrant(
                new ApprovalCandidate(entry.Verb, Directory: null)
                {
                    Shell = entry.Shell,
                    VerbTokens = entry.VerbTokens,
                    AssignmentDigest = entry.AssignmentDigest,
                },
                GrantScope.OfStoredEntry(entry)))
            .ToArray();
        Assert.True(ToolApprovalActor.TryCreateEntries(
            new ToolName(toolName),
            grants,
            out var persistent,
            out _));
        return persistent;
    }

    private static ToolApprovalStore CreateStore(string path)
        => new(
            path,
            new FakeTimeProvider(GrantTime),
            new ApprovalStoreMigrationContext(ApprovalShell.Bash),
            lockTimeout: TimeSpan.Zero);
}

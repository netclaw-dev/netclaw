// -----------------------------------------------------------------------
// <copyright file="ToolFrictionEvidenceContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Netclaw.Security.Tests;

public sealed partial class ToolFrictionEvidenceContractTests
{
    private const string FixtureFile = "tool-friction-fixtures.json";
    private const string FixtureSha256 =
        "40ae6dbe3b9c6e9c8d061face39b341ee0124b517d63414353430f5ed7f3442c";

    private static readonly string[] ProhibitedRawIdentifierClasses =
    [
        "session_id",
        "tool_call_id",
        "user_identity",
        "repository_identity",
        "host",
        "url",
        "exact_timestamp",
        "credential",
        "raw_prompt",
        "raw_response",
        "raw_log_line"
    ];

    private static readonly ToolFrictionExpectedCase[] ExpectedCases =
    [
        new("TF01", "RecursiveSearch", "ApprovalGatedShellSearch", ["file_search"],
            "success", false, true, "NoContextChangeRequired"),
        new("TF02", "ComposedRead", "ApprovalGatedShellBatch", ["file_read", "file_read"],
            "success", false, true, "RecordTwoCanonicalFiles"),
        new("TF04", "ImageMetadata", "ApprovalGatedInterpreterMetadata", ["file_read"],
            "success", false, true, "RecordOneCanonicalFile"),
        new("TF05", "SpillContinuation", "ApprovalGatedSpillParsing", ["tool_output_read"],
            "success", false, true, "NoContextChangeRequired"),
        new("TF06", "FailedFileActivity", "FailedCallPollutedRecentFiles", ["file_write"],
            "access_denied", false, false, "PreserveRecentFiles"),
        new("TF07", "SubagentCatalogExposure", "EagerSubagentSchemaCatalog",
            ["search_tools", "load_tool"], "success", false, false, "CoreOnlyChildCatalog")
    ];

    [Fact]
    public void Fixture_binds_the_complete_sanitized_contract()
    {
        var bytes = File.ReadAllBytes(FixturePath());
        var catalog = Deserialize(bytes);

        Assert.Equal(FixtureSha256, ComputeSha256(bytes));
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal(1, catalog.SchemaVersion);
        Assert.Equal(
            "Raw transcripts remain local. Cases are semantic reconstructions with synthetic identifiers.",
            catalog.Sanitization.SourceBoundary);
        Assert.Equal(
            ProhibitedRawIdentifierClasses,
            catalog.Sanitization.ProhibitedRawIdentifierClasses);
        Assert.Equal(ExpectedCases.Length, catalog.Cases.Count);
        for (var index = 0; index < ExpectedCases.Length; index++)
        {
            var expected = ExpectedCases[index];
            var actual = ToExpectedCase(catalog.Cases[index]);
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.Scenario, actual.Scenario);
            Assert.Equal(expected.ObservedFriction, actual.ObservedFriction);
            Assert.Equal(expected.ExpectedToolSequence, actual.ExpectedToolSequence);
            Assert.Equal(expected.ExpectedOutcome, actual.ExpectedOutcome);
            Assert.Equal(expected.ExpectedApprovalRequired, actual.ExpectedApprovalRequired);
            Assert.Equal(expected.FallbackApprovalRequired, actual.FallbackApprovalRequired);
            Assert.Equal(expected.ExpectedContextEffect, actual.ExpectedContextEffect);
        }
    }

    [Theory]
    [InlineData("root")]
    [InlineData("case")]
    public void Fixture_schema_rejects_unknown_members(string location)
    {
        var json = File.ReadAllText(FixturePath());
        var malformed = location == "root"
            ? json.Replace(
                "\"schemaVersion\": 1,",
                "\"schemaVersion\": 1, \"unexpected\": true,",
                StringComparison.Ordinal)
            : json.Replace(
                "\"id\": \"TF01\",",
                "\"id\": \"TF01\", \"unexpected\": true,",
                StringComparison.Ordinal);

        Assert.NotEqual(json, malformed);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            malformed,
            ToolFrictionEvidenceJsonContext.Default.ToolFrictionFixtureCatalog));
    }

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("sourceBoundary")]
    [InlineData("prohibitedRawIdentifierClasses")]
    [InlineData("id")]
    [InlineData("scenario")]
    [InlineData("observedFriction")]
    [InlineData("expectedToolSequence")]
    [InlineData("expectedOutcome")]
    [InlineData("expectedApprovalRequired")]
    [InlineData("fallbackApprovalRequired")]
    [InlineData("expectedContextEffect")]
    [InlineData("caseOrder")]
    public void Typed_digest_detects_each_policy_field_mutation(string field)
    {
        var catalog = Deserialize(File.ReadAllBytes(FixturePath()));
        var mutated = Mutate(catalog, field);

        Assert.NotEqual(ComputeContractDigest(catalog), ComputeContractDigest(mutated));
    }

    [Fact]
    public void Fixture_contains_no_raw_runtime_identity()
    {
        var text = File.ReadAllText(FixturePath());

        Assert.False(ContainsRawIdentity(text));
    }

    [Theory]
    [InlineData("D1234567890")]
    [InlineData("1234567890.123456")]
    [InlineData("operator@example.com")]
    [InlineData("/home/private-user/project")]
    [InlineData(@"C:\Users\private-user\project")]
    [InlineData("petabridge")]
    [InlineData("ghp_000000000000000000000000000000000000")]
    [InlineData("Authorization: Bearer example-credential-value")]
    [InlineData("api_key=examplecredentialvalue")]
    [InlineData("https://private.example.com/path")]
    [InlineData("--repo private-owner/private-project")]
    [InlineData("call_private")]
    [InlineData("2026-08-19T01:02:03Z")]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData("akka://netclaw/user/session")]
    [InlineData("[INF] raw daemon line")]
    [InlineData("\"rawPrompt\":\"private request\"")]
    [InlineData("\"rawResponse\":\"private result\"")]
    [InlineData("\"rawLogLine\":\"private log\"")]
    public void Pii_audit_detects_prohibited_identity_shapes(string value)
    {
        Assert.True(ContainsRawIdentity(value));
    }

    private static ToolFrictionFixtureCatalog Deserialize(byte[] bytes)
        => JsonSerializer.Deserialize(
               bytes,
               ToolFrictionEvidenceJsonContext.Default.ToolFrictionFixtureCatalog)
           ?? throw new InvalidDataException($"{FixtureFile} has no root object.");

    private static ToolFrictionExpectedCase ToExpectedCase(ToolFrictionCase item)
        => new(
            item.Id,
            item.Scenario,
            item.ObservedFriction,
            [.. item.ExpectedToolSequence],
            item.ExpectedOutcome,
            item.ExpectedApprovalRequired,
            item.FallbackApprovalRequired,
            item.ExpectedContextEffect);

    private static ToolFrictionFixtureCatalog Mutate(
        ToolFrictionFixtureCatalog catalog,
        string field)
    {
        if (field == "schemaVersion")
            return catalog with { SchemaVersion = catalog.SchemaVersion + 1 };
        if (field == "sourceBoundary")
            return catalog with
            {
                Sanitization = catalog.Sanitization with { SourceBoundary = "changed boundary" }
            };
        if (field == "prohibitedRawIdentifierClasses")
            return catalog with
            {
                Sanitization = catalog.Sanitization with
                {
                    ProhibitedRawIdentifierClasses = ["changed_identifier_class"]
                }
            };
        if (field == "caseOrder")
            return catalog with { Cases = [.. catalog.Cases.AsEnumerable().Reverse()] };

        var first = catalog.Cases[0];
        var changed = field switch
        {
            "id" => first with { Id = "changed" },
            "scenario" => first with { Scenario = "ChangedScenario" },
            "observedFriction" => first with { ObservedFriction = "ChangedFriction" },
            "expectedToolSequence" => first with { ExpectedToolSequence = ["changed_tool"] },
            "expectedOutcome" => first with { ExpectedOutcome = "changed_outcome" },
            "expectedApprovalRequired" => first with
            {
                ExpectedApprovalRequired = !first.ExpectedApprovalRequired
            },
            "fallbackApprovalRequired" => first with
            {
                FallbackApprovalRequired = !first.FallbackApprovalRequired
            },
            "expectedContextEffect" => first with
            {
                ExpectedContextEffect = "ChangedContextEffect"
            },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown field.")
        };

        return catalog with { Cases = [changed, .. catalog.Cases.Skip(1)] };
    }

    private static string ComputeContractDigest(ToolFrictionFixtureCatalog catalog)
    {
        var text = new StringBuilder();
        Append(text, catalog.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(text, catalog.Sanitization.SourceBoundary);
        foreach (var identifierClass in catalog.Sanitization.ProhibitedRawIdentifierClasses)
            Append(text, identifierClass);

        foreach (var item in catalog.Cases)
        {
            Append(text, item.Id);
            Append(text, item.Scenario);
            Append(text, item.ObservedFriction);
            foreach (var tool in item.ExpectedToolSequence)
                Append(text, tool);
            Append(text, item.ExpectedOutcome);
            Append(text, item.ExpectedApprovalRequired ? "true" : "false");
            Append(text, item.FallbackApprovalRequired ? "true" : "false");
            Append(text, item.ExpectedContextEffect);
        }

        return ComputeSha256(Encoding.UTF8.GetBytes(text.ToString()));
    }

    private static void Append(StringBuilder target, string value)
        => target.Append(value.Length).Append(':').Append(value).Append('\n');

    private static bool ContainsRawIdentity(string text)
        => SlackChannelPattern().IsMatch(text)
           || SlackThreadPattern().IsMatch(text)
           || EmailPattern().IsMatch(text)
           || PrivateHomePattern().IsMatch(text)
           || PrivateWindowsUserPattern().IsMatch(text)
           || KnownSourceIdentityPattern().IsMatch(text)
           || AccessTokenPattern().IsMatch(text)
           || BearerCredentialPattern().IsMatch(text)
           || CredentialAssignmentPattern().IsMatch(text)
           || UrlPattern().IsMatch(text)
           || RemoteRepositoryPattern().IsMatch(text)
           || CallIdPattern().IsMatch(text)
           || ExactTimestampPattern().IsMatch(text)
           || GuidPattern().IsMatch(text)
           || RawPayloadFieldPattern().IsMatch(text)
           || text.Contains("akka://", StringComparison.Ordinal)
           || text.Contains("[INF]", StringComparison.Ordinal);

    private static string ComputeSha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string FixturePath()
        => Path.Combine(AppContext.BaseDirectory, "ToolFrictionEvidence", FixtureFile);

    [GeneratedRegex(@"\bD[A-Z0-9]{10}\b", RegexOptions.CultureInvariant)]
    private static partial Regex SlackChannelPattern();

    [GeneratedRegex(@"\b\d{10}\.\d{6}\b", RegexOptions.CultureInvariant)]
    private static partial Regex SlackThreadPattern();

    [GeneratedRegex(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"/home/(?!user/|test/|foo/|dev/|runner/|gh-actions/|ci/)[a-zA-Z0-9_.-]+/", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateHomePattern();

    [GeneratedRegex(@"[A-Za-z]:[\\/]Users[\\/](?!user[\\/]|test[\\/]|foo[\\/]|dev[\\/]|runner[\\/]|ci[\\/])[A-Za-z0-9_.-]+[\\/]", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateWindowsUserPattern();

    [GeneratedRegex(@"petabridge|stannard|testlab|D0AC6", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KnownSourceIdentityPattern();

    [GeneratedRegex(
        @"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[baprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|eyJ[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{10,})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AccessTokenPattern();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9._~+/-]{8,}=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerCredentialPattern();

    [GeneratedRegex(
        @"[""']?(?:token|access[_-]?token|api[_-]?key|client[_-]?secret|password)[""']?\s*[:=]\s*[""']?(?!<)[A-Za-z0-9+/=_-]{8,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialAssignmentPattern();

    [GeneratedRegex(@"https?://[A-Za-z0-9.-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(
        @"(?:--repo\s+|gh\s+api\s+repos/|api\.github\.com/repos/)[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RemoteRepositoryPattern();

    [GeneratedRegex(@"\bcall_[A-Za-z0-9_-]+\b", RegexOptions.CultureInvariant)]
    private static partial Regex CallIdPattern();

    [GeneratedRegex(
        @"\b20\d{2}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z?\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex ExactTimestampPattern();

    [GeneratedRegex(
        @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex GuidPattern();

    [GeneratedRegex(
        @"[""'](?:raw)?(?:prompt|response|logLine)[""']\s*:",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RawPayloadFieldPattern();

    private sealed record ToolFrictionExpectedCase(
        string Id,
        string Scenario,
        string ObservedFriction,
        IReadOnlyList<string> ExpectedToolSequence,
        string ExpectedOutcome,
        bool ExpectedApprovalRequired,
        bool FallbackApprovalRequired,
        string ExpectedContextEffect);
}

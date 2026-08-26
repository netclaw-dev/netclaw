// -----------------------------------------------------------------------
// <copyright file="ApprovalEntryWireCodecTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json;
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class ApprovalEntryWireCodecTests
{
    [Fact]
    public void Token_prefix_has_exact_wire_form()
    {
        var createdAt = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
        var entry = ApprovalEntry.CreateTokenPrefix(
            ApprovalShell.Bash,
            ["git", "push"],
            createdAt: createdAt);

        var json = WriteEntry(entry);

        Assert.Equal(
            """
            {
              "shell": "Bash",
              "match": "TokenPrefix",
              "verbTokens": [
                "git",
                "push"
              ],
              "directory": null,
              "createdAt": "2026-08-11T12:00:00+00:00"
            }
            """,
            json);
    }

    [Fact]
    public void Legacy_exact_has_exact_wire_form()
    {
        var entry = ApprovalEntry.CreateLegacyExact(
            ApprovalShell.PowerShell,
            "Get-Content");

        var json = WriteEntry(entry);

        Assert.Equal(
            """
            {
              "shell": "PowerShell",
              "match": "LegacyExact",
              "verb": "Get-Content",
              "directory": null,
              "createdAt": null
            }
            """,
            json);
    }

    [Fact]
    public void Non_shell_entry_has_compatible_wire_form()
    {
        var entry = new ApprovalEntry("create-page");

        var json = WriteEntry(entry);

        Assert.Equal(
            """
            {
              "verb": "create-page",
              "directory": null,
              "createdAt": null
            }
            """,
            json);
    }

    [Fact]
    public void Legacy_non_shell_json_projection_stays_compatible()
    {
        var entry = ApprovalEntry.CreateNonShell("create-page");

        var json = JsonSerializer.Serialize(entry);

        Assert.Equal(
            """{"verb":"create-page","directory":null,"createdAt":null}""",
            json);
    }

    [Fact]
    public void Store_codec_round_trips_generated_wire_forms()
    {
        var shellEntries = new List<ApprovalEntry>
        {
            ApprovalEntry.CreateTokenPrefix(
                ApprovalShell.Bash,
                ["git", "push"],
                createdAt: new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero)),
            ApprovalEntry.CreateLegacyExact(ApprovalShell.Bash, "git status"),
        };
        var nonShellEntry = ApprovalEntry.CreateNonShell("create-page");
        var data = new ToolApprovalData();
        data.Audiences.Add(
            "personal",
            new Dictionary<string, List<ApprovalEntry>>(StringComparer.Ordinal)
            {
                ["shell_execute"] = shellEntries,
                ["create_page"] = [nonShellEntry],
            });

        var json = ApprovalStoreCodec.Serialize(data);
        Assert.Contains(
            "\"createdAt\": \"2026-08-11T12:00:00+00:00\"",
            json,
            StringComparison.Ordinal);
        Assert.DoesNotContain("\\u002B", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        var roundTrip = ApprovalStoreCodec.ReadVersion3(document.RootElement, "shell_execute");

        var actualShellEntries = roundTrip.Audiences["personal"]["shell_execute"];
        Assert.Collection(
            actualShellEntries,
            entry => Assert.True(ToolApprovalEntryComparer.Equals(shellEntries[0], entry)),
            entry => Assert.True(ToolApprovalEntryComparer.Equals(shellEntries[1], entry)));
        Assert.True(ToolApprovalEntryComparer.Equals(
            nonShellEntry,
            roundTrip.Audiences["personal"]["create_page"].Single()));
    }

    [Fact]
    public void Token_prefix_round_trips_as_closed_type()
    {
        const string Json =
            """{"shell":"Bash","match":"TokenPrefix","verbTokens":["git","push"],"directory":"/work/repo","createdAt":null}""";

        var entry = ReadEntry(Json);

        Assert.Equal(ApprovalShell.Bash, entry.Shell);
        Assert.Equal(ApprovalMatchKind.TokenPrefix, entry.Match);
        Assert.Equal(["git", "push"], entry.VerbTokens);
        Assert.Equal("git push", entry.Verb);
        Assert.Equal("/work/repo", entry.Directory);
    }

    [Theory]
    [InlineData("""{"shell":"Bash","match":"TokenPrefix","verbTokens":[],"directory":null,"createdAt":null}""")]
    [InlineData("""{"shell":null,"match":"TokenPrefix","verbTokens":["git"],"directory":null,"createdAt":null}""")]
    [InlineData("""{"shell":"Bash","match":null,"verbTokens":["git"],"directory":null,"createdAt":null}""")]
    [InlineData("""{"shell":"Bash","match":"TokenPrefix","verbTokens":null,"directory":null,"createdAt":null}""")]
    [InlineData("""{"shell":"Bash","match":"TokenPrefix","verbTokens":[null],"directory":null,"createdAt":null}""")]
    [InlineData("""{"shell":"Bash","match":"TokenPrefix","verbTokens":["git"],"verb":"git","directory":null,"createdAt":null}""")]
    [InlineData("""{"shell":"Bash","match":"Other","verbTokens":["git"],"directory":null,"createdAt":null}""")]
    [InlineData("""{"shell":"Bash","match":"TokenPrefix","verbTokens":["git push"],"directory":null,"createdAt":null}""")]
    [InlineData("""{"verb":" git","directory":null,"createdAt":null}""")]
    [InlineData("""{"verb":null,"directory":null,"createdAt":null}""")]
    [InlineData("""{"verb":"git","directory":null,"createdAt":42}""")]
    [InlineData("""{"verb":"git","createdAt":null}""")]
    [InlineData("""{"verb":"git","directory":null}""")]
    [InlineData("""{"verb":"git","directory":"relative/path","createdAt":null}""")]
    [InlineData("""{"shell":"Bash","match":"TokenPrefix","verbTokens":["git"],"directory":"/work/../etc","createdAt":null}""")]
    [InlineData("""{"shell":"Bash","match":"TokenPrefix","verbTokens":["git"],"directory":"/work//repo","createdAt":null}""")]
    [InlineData("""{"shell":"Bash","match":"TokenPrefix","verbTokens":["git"],"directory":"/work/repo/","createdAt":null}""")]
    [InlineData("""{"shell":"PowerShell","match":"TokenPrefix","verbTokens":["git"],"directory":"C:\\work\\..\\etc","createdAt":null}""")]
    [InlineData("""{"shell":"PowerShell","match":"TokenPrefix","verbTokens":["git"],"directory":"C:\\work\\\\repo","createdAt":null}""")]
    [InlineData("""{"shell":"PowerShell","match":"TokenPrefix","verbTokens":["git"],"directory":"C:/work/repo","createdAt":null}""")]
    [InlineData("""{"shell":"PowerShell","match":"TokenPrefix","verbTokens":["git"],"directory":"C:\\work\\repo\\","createdAt":null}""")]
    [InlineData("""{"verb":"git","directory":null,"createdAt":null,"extra":true}""")]
    public void Invalid_closed_form_fails(string json)
    {
        Assert.ThrowsAny<Exception>(() => ReadEntry(json));
    }

    [Fact]
    public void Duplicate_member_fails()
    {
        const string Json =
            """{"verb":"git","verb":"other","directory":null,"createdAt":null}""";

        Assert.Throws<JsonException>(() => ReadEntry(Json));
    }

    [Fact]
    public void Bidi_control_fails()
    {
        const string Json =
            """{"verb":"git\u202epush","directory":null,"createdAt":null}""";

        Assert.Throws<JsonException>(() => ReadEntry(Json));
    }

    [Fact]
    public void Token_factory_clones_input()
    {
        var tokens = new[] { "git", "push" };
        var entry = ApprovalEntry.CreateTokenPrefix(ApprovalShell.Bash, tokens);

        tokens[1] = "delete";

        Assert.Equal(["git", "push"], entry.VerbTokens);
        Assert.Equal("git push", entry.Verb);
    }

    [Theory]
    [InlineData(ApprovalShell.Bash, ApprovalMatchKind.TokenPrefix, "Bash token-prefix \"git push\" anywhere")]
    [InlineData(ApprovalShell.PowerShell, ApprovalMatchKind.LegacyExact, "PowerShell legacy-exact \"Get-Content\" anywhere")]
    public void Typed_scope_label_round_trips(
        ApprovalShell shell,
        ApprovalMatchKind match,
        string expected)
    {
        var original = match == ApprovalMatchKind.TokenPrefix
            ? ApprovalEntry.CreateTokenPrefix(shell, ["git", "push"])
            : ApprovalEntry.CreateLegacyExact(shell, "Get-Content");

        var label = original.FormatScope();
        var parsed = ApprovalEntry.TryParseScope(label, out var roundTrip, out var error);

        Assert.Equal(expected, label);
        Assert.True(parsed, error);
        Assert.NotNull(roundTrip);
        Assert.True(ToolApprovalEntryComparer.Equals(original, roundTrip));
    }

    [Fact]
    public void Typed_folder_scope_round_trips_quoted_phrase()
    {
        var directory = Path.Combine(Path.GetTempPath(), "approval in scope");
        var original = ApprovalEntry.CreateLegacyExact(
            ApprovalShell.Bash,
            "say-\"hello\"",
            directory);

        var parsed = ApprovalEntry.TryParseScope(
            original.FormatScope(),
            out var roundTrip,
            out var error);

        Assert.True(parsed, error);
        Assert.NotNull(roundTrip);
        Assert.True(ToolApprovalEntryComparer.Equals(original, roundTrip));
    }

    [Fact]
    public void Typed_scope_round_trip_preserves_significant_directory_space()
    {
        var original = ApprovalEntry.CreateTokenPrefix(
            ApprovalShell.Bash,
            ["git", "status"],
            "/work/repo ");

        var parsed = ApprovalEntry.TryParseScope(
            original.FormatScope(),
            out var roundTrip,
            out var error);

        Assert.True(parsed, error);
        Assert.NotNull(roundTrip);
        Assert.Equal("/work/repo ", roundTrip.Directory);
        Assert.True(ToolApprovalEntryComparer.Equals(original, roundTrip));
    }

    [Theory]
    [InlineData("tool in mode", "/work/repo", "NonShell exact \"tool in mode\" in /work/repo")]
    [InlineData("status anywhere", null, "NonShell exact \"status anywhere\" anywhere")]
    public void Non_shell_scope_label_round_trips_ambiguous_separator_text(
        string verb,
        string? directory,
        string expected)
    {
        var original = ApprovalEntry.CreateNonShell(verb, directory);

        var label = original.FormatScope();
        var parsed = ApprovalEntry.TryParseScope(label, out var roundTrip, out var error);

        Assert.Equal(expected, label);
        Assert.True(parsed, error);
        Assert.NotNull(roundTrip);
        Assert.True(ToolApprovalEntryComparer.Equals(original, roundTrip));
    }

    private static ApprovalEntry ReadEntry(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ApprovalEntryWireCodec.ReadVersion3(document.RootElement);
    }

    private static string WriteEntry(ApprovalEntry entry) => JsonSerializer.Serialize(
        ApprovalEntryWireCodec.WriteVersion3(entry),
        ApprovalStoreJsonContext.Default.ApprovalEntryWire);
}

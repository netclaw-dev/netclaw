// -----------------------------------------------------------------------
// <copyright file="ToolCallLogFormatterTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Security.Tests;

/// <summary>
/// The tool call log line leaves the host through the OTLP exporter (issue #2589).
/// These tests hold the helper rules in this order: walk the JSON at every depth,
/// redact secret keys and SensitiveString values, replace long strings with a
/// length and hash summary, serialize, run the text redactor, and then apply the cap.
/// </summary>
public sealed class ToolCallLogFormatterTests
{
    private const string Redacted = "***REDACTED***";

    [Fact]
    public void Null_arguments_format_as_an_empty_object()
    {
        Assert.Equal("{}", ToolCallLogFormatter.FormatArguments(null));
    }

    [Fact]
    public void Nested_secret_key_is_redacted()
    {
        var arguments = FromJson("""{"headers":{"Authorization":"x"},"url":"example.test/items"}""");

        var text = ToolCallLogFormatter.FormatArguments(arguments);

        Assert.Contains(Redacted, text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"x\"", text, StringComparison.Ordinal);
        Assert.Contains("example.test/items", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Secret_key_inside_an_array_is_redacted()
    {
        var arguments = FromJson("""{"accounts":[{"name":"build","github_token":"plain-array-value-31"}]}""");

        var text = ToolCallLogFormatter.FormatArguments(arguments);

        Assert.DoesNotContain("plain-array-value-31", text, StringComparison.Ordinal);
        Assert.Contains(Redacted, text, StringComparison.Ordinal);
        Assert.Contains("build", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Top_level_secret_key_is_redacted_when_the_text_redactor_does_not_match_it()
    {
        // SecretOutputRedactor.Redact only matches a JSON key that starts with a
        // secret word. "github_token" does not start with one, so only the
        // IsSecretKey step can redact this value.
        var arguments = FromJson("""{"github_token":"plainvalue123","repo":"netclaw"}""");

        var text = ToolCallLogFormatter.FormatArguments(arguments);

        Assert.DoesNotContain("plainvalue123", text, StringComparison.Ordinal);
        Assert.Contains(Redacted, text, StringComparison.Ordinal);
        Assert.Contains("netclaw", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Sensitive_string_value_is_redacted_at_any_depth()
    {
        // The SensitiveString JSON converter writes the raw value. The helper must
        // replace the value before it serializes the arguments.
        var arguments = new Dictionary<string, object?>
        {
            ["header"] = new SensitiveString("sens-raw-top-41"),
            ["options"] = new Dictionary<string, object?>
            {
                ["value"] = new SensitiveString("sens-raw-nested-42")
            }
        };

        var text = ToolCallLogFormatter.FormatArguments(arguments);

        Assert.DoesNotContain("sens-raw-top-41", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sens-raw-nested-42", text, StringComparison.Ordinal);
        Assert.Contains(Redacted, text, StringComparison.Ordinal);
    }

    [Fact]
    public void String_of_256_chars_stays_and_string_of_257_chars_becomes_a_hash()
    {
        var kept = new string('k', 256);
        var hashed = new string('h', 257);
        var arguments = new Dictionary<string, object?>
        {
            ["kept"] = kept,
            ["hashed"] = hashed
        };

        var text = ToolCallLogFormatter.FormatArguments(arguments);

        Assert.Contains(kept, text, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('h', 20), text, StringComparison.Ordinal);
        Assert.Contains(HashForm(hashed), text, StringComparison.Ordinal);
    }

    [Fact]
    public void Large_values_in_an_array_and_in_an_object_become_the_hash_form()
    {
        var inArray = new string('a', 300);
        var inObject = new string('o', 1834);
        var arguments = FromJson(
            $$$"""{"files":["{{{inArray}}}"],"edit":{"replacement":"{{{inObject}}}","count":4242,"dryRun":true,"note":null}}""");

        var text = ToolCallLogFormatter.FormatArguments(arguments);

        Assert.Contains(HashForm(inArray), text, StringComparison.Ordinal);
        Assert.Contains(HashForm(inObject), text, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('a', 20), text, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('o', 20), text, StringComparison.Ordinal);
        Assert.Contains("4242", text, StringComparison.Ordinal);
        Assert.Contains("true", text, StringComparison.Ordinal);
        Assert.Contains("null", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_secret_under_a_secret_key_is_redacted_and_not_hashed()
    {
        // The secret step comes before the hash step. A hash of a secret value is
        // not safe to export, because a reader can confirm a guessed value with it.
        var secret = new string('s', 400);
        var arguments = new Dictionary<string, object?> { ["api_key"] = secret };

        var text = ToolCallLogFormatter.FormatArguments(arguments);

        Assert.Contains(Redacted, text, StringComparison.Ordinal);
        Assert.DoesNotContain("sha256:", text, StringComparison.Ordinal);
        Assert.DoesNotContain(HashForm(secret), text, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_redactor_runs_on_short_values_under_ordinary_keys()
    {
        const string token = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";
        var arguments = new Dictionary<string, object?>
        {
            ["command"] = $"git clone https://{token}@example.test/repo.git"
        };

        var text = ToolCallLogFormatter.FormatArguments(arguments);

        Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        Assert.Contains(Redacted, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Many_short_values_are_capped_at_4096_chars()
    {
        var arguments = new Dictionary<string, object?>();
        for (var index = 0; index < 100; index++)
            arguments[$"k{index:D3}"] = new string('v', 100);

        var text = ToolCallLogFormatter.FormatArguments(arguments);

        Assert.True(
            text.Length <= ToolCallLogFormatter.MaxArgumentsChars + 3,
            $"The arguments text has {text.Length} chars.");
        Assert.EndsWith("...", text, StringComparison.Ordinal);
        Assert.StartsWith("{", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Secret_across_the_cap_boundary_is_redacted_and_not_cut_in_half()
    {
        // Each value is a JWT shorter than 256 chars, so the hash step does not
        // replace it. The values add up to more than the cap, so one JWT sits
        // across the boundary. When the cap comes before the redactor, the
        // redactor cannot match the cut JWT and its first part leaks.
        var jwt = "eyJ" + new string('a', 100) + ".eyJ" + new string('b', 100) + "." + new string('c', 12);
        var arguments = new Dictionary<string, object?>();
        for (var index = 0; index < 200; index++)
            arguments[$"k{index:D3}"] = jwt;

        var text = ToolCallLogFormatter.FormatArguments(arguments);

        // An empty text passes the DoesNotContain checks, so check the shape first.
        Assert.StartsWith("{", text, StringComparison.Ordinal);
        Assert.Contains("k000", text, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJ", text, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('a', 20), text, StringComparison.Ordinal);
        Assert.Contains(Redacted, text, StringComparison.Ordinal);
        Assert.True(
            text.Length <= ToolCallLogFormatter.MaxArgumentsChars + 3,
            $"The arguments text has {text.Length} chars.");
        Assert.EndsWith("...", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Rationale_is_redacted_and_capped_at_500_chars()
    {
        const string token = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";
        var rationale = $"Use {token} to read the repo. " + new string('r', 600);

        var text = ToolCallLogFormatter.FormatRationale(rationale);

        Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        Assert.Contains(Redacted, text, StringComparison.Ordinal);
        Assert.True(
            text.Length <= ToolCallLogFormatter.MaxRationaleChars + 3,
            $"The rationale text has {text.Length} chars.");
        Assert.EndsWith("...", text, StringComparison.Ordinal);
    }

    private static string HashForm(string value)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        return $"<{value.Length} chars, sha256:{hash[..16]}>";
    }

    private static Dictionary<string, object?> FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject()
            .ToDictionary(static property => property.Name, static property => (object?)property.Value.Clone());
    }
}

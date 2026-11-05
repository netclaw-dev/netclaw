// -----------------------------------------------------------------------
// <copyright file="SecretOutputRedactorLoggingTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Security.Tests;

/// <summary>
/// The log-only rules of <see cref="SecretOutputRedactor.RedactForLogging(string)"/> (#2770).
/// Each positive case also checks that <see cref="SecretOutputRedactor.Redact"/>, which operative
/// sinks use, keeps the text: a log-only rule must never reach the model.
/// </summary>
public sealed class SecretOutputRedactorLoggingTests
{
    private const string Masked = SecretOutputRedactor.Redacted;

    // ── A key and its value on separate lines ──

    public static TheoryData<string, string, string> NextLineSecrets()
    {
        var nel = ((char)0x85).ToString();
        var lineSeparator = ((char)0x2028).ToString();
        return new()
        {
            { "API_KEY=\nabc123secret", "abc123secret", "API_KEY=\n" + Masked },
            { "API_KEY=\r\nabc123secret", "abc123secret", "API_KEY=\r\n" + Masked },
            { "API_KEY=" + nel + "abc123secret", "abc123secret", "API_KEY=" + nel + Masked },
            { "API_KEY=" + lineSeparator + "abc123secret", "abc123secret", "API_KEY=" + lineSeparator + Masked },
            { "export DB_PASSWORD= \n\thunter2 # set", "hunter2", "export DB_PASSWORD= \n\t" + Masked + " # set" },
            { "GITHUB_TOKEN=\nabc;rest", "abc", "GITHUB_TOKEN=\n" + Masked + ";rest" },
            { "--api-key=\nabc123secret", "abc123secret", "--api-key=\n" + Masked },
        };
    }

    [Theory]
    [MemberData(nameof(NextLineSecrets))]
    public void A_value_on_the_line_after_its_key_is_masked_in_logs_only(string input, string secret, string expected)
    {
        Assert.Equal(expected, SecretOutputRedactor.RedactForLogging(input));
        Assert.Contains(secret, SecretOutputRedactor.Redact(input), StringComparison.Ordinal);
    }

    // A next-line token that is a key starts a new pair. It is not the value of the key before it.
    // The scan goes on, so the next pair is read on its own.
    [Theory]
    [InlineData("API_KEY=\nDB_PASSWORD=\nhunter2", "API_KEY=\nDB_PASSWORD=\n" + Masked)]
    [InlineData("API_KEY=\nHOME=/root\nPATH=/bin", "API_KEY=\nHOME=/root\nPATH=/bin")]
    [InlineData("API_KEY=\nDB_PASSWORD=\nAUTH_TOKEN=\nhunter2", "API_KEY=\nDB_PASSWORD=\nAUTH_TOKEN=\n" + Masked)]
    [InlineData("API_KEY=\nHOME=/root\nDB_PASSWORD=\nhunter2", "API_KEY=\nHOME=/root\nDB_PASSWORD=\n" + Masked)]
    [InlineData("API_KEY=\nabc123==", "API_KEY=\n" + Masked)]
    public void A_key_on_the_next_line_is_not_the_value_of_the_key_before_it(string input, string expected)
    {
        Assert.Equal(expected, SecretOutputRedactor.RedactForLogging(input));
    }

    // The next-line rule masks only what the shared rules mask on one line, so it keeps their
    // exceptions: counts, cursors, descriptors, references, code idioms, and "Name:" prose.
    [Theory]
    [InlineData("max_tokens=\n4096")]
    [InlineData("total_tokens=\n1234")]
    [InlineData("next_page_token=\nCAESBwoF")]
    [InlineData("DB_PASSWORD_FILE=\n/run/secrets/db")]
    [InlineData("API_KEY=\n$API_KEY")]
    [InlineData("foo(auth_token=\nauth_token)")]
    [InlineData("authorizationAttemptId=\nauth-3f9a1c")]
    [InlineData("Failed to refresh token:\nSystem.Exception: boom")]
    [InlineData("API_KEY=\n\nabc123secret")]
    [InlineData("Zm9vYmFy==\nnext")]
    public void An_ordinary_value_on_the_next_line_stays(string input)
    {
        Assert.Equal(input, SecretOutputRedactor.RedactForLogging(input));
    }

    // ── Presigned URL signatures ──

    [Theory]
    [InlineData(
        "https://b.s3.amazonaws.com/x.png?X-Amz-Signature=deadbeefcafe01&X-Amz-Date=20261105T000000Z",
        "deadbeefcafe01",
        "https://b.s3.amazonaws.com/x.png?X-Amz-Signature=" + Masked + "&X-Amz-Date=20261105T000000Z")]
    [InlineData(
        "https://storage.googleapis.com/b/x?X-Goog-Algorithm=GOOG4-RSA-SHA256&X-Goog-Signature=0a1b2c3d4e",
        "0a1b2c3d4e",
        "https://storage.googleapis.com/b/x?X-Goog-Algorithm=GOOG4-RSA-SHA256&X-Goog-Signature=" + Masked)]
    [InlineData(
        "https://d111.cloudfront.net/x.png?Expires=1793750400&Signature=Qm9vT2s~abc&Key-Pair-Id=K2JCJMDEHXQW5F",
        "Qm9vT2s~abc",
        "https://d111.cloudfront.net/x.png?Expires=1793750400&Signature=" + Masked + "&Key-Pair-Id=K2JCJMDEHXQW5F")]
    [InlineData(
        "https://acct.blob.core.windows.net/c/x.png?sv=2024-11-04&se=2026-11-06&sp=r&sig=Zm9vYmFy%3D",
        "Zm9vYmFy%3D",
        "https://acct.blob.core.windows.net/c/x.png?sv=2024-11-04&se=2026-11-06&sp=r&sig=" + Masked)]
    [InlineData(
        "<a href=\"https://acct.blob.core.windows.net/c/x?sv=2024-11-04&amp;sig=Zm9vYmFy%3D\">",
        "Zm9vYmFy%3D",
        "<a href=\"https://acct.blob.core.windows.net/c/x?sv=2024-11-04&amp;sig=" + Masked + "\">")]
    public void A_presigned_url_signature_is_masked_in_logs_only(string input, string signature, string expected)
    {
        Assert.Equal(expected, SecretOutputRedactor.RedactForLogging(input));
        Assert.Equal(input, SecretOutputRedactor.Redact(input));
        Assert.Contains(signature, SecretOutputRedactor.Redact(input), StringComparison.Ordinal);
    }

    [Fact]
    public void An_s3_signature_is_masked_in_either_order()
    {
        const string signatureFirst =
            "https://b.r2.cloudflarestorage.com/x.png?X-Amz-Signature=deadbeefcafe01"
            + "&X-Amz-Credential=abc123def456%2F20261105%2Fauto%2Fs3%2Faws4_request&X-Amz-Date=20261105T000000Z";
        const string credentialFirst =
            "https://b.r2.cloudflarestorage.com/x.png?X-Amz-Credential=abc123def456%2F20261105%2Fauto%2Fs3%2Faws4_request"
            + "&X-Amz-Date=20261105T000000Z&X-Amz-Signature=deadbeefcafe01";

        foreach (var url in new[] { signatureFirst, credentialFirst })
        {
            var logged = SecretOutputRedactor.RedactForLogging(url);
            Assert.DoesNotContain("deadbeefcafe01", logged, StringComparison.Ordinal);
            Assert.DoesNotContain("abc123def456", logged, StringComparison.Ordinal);
        }

        // The shared rules alone miss the signature that comes first.
        Assert.Contains("deadbeefcafe01", SecretOutputRedactor.Redact(signatureFirst), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://example.com/x?signed=true&sigma=2")]
    [InlineData("https://example.com/x?design=flat&assign=me")]
    [InlineData("the sig=abc in prose has no query separator")]
    [InlineData("https://example.com/x?sig={signature}")]
    public void A_parameter_that_is_not_a_signature_stays(string input)
    {
        Assert.Equal(input, SecretOutputRedactor.RedactForLogging(input));
    }

    // ── The authorization attempt ID (shared rule) ──

    [Theory]
    [InlineData("authorizationAttemptId=auth-3f9a1c sessionId=D1/1700000000.0001")]
    [InlineData("authorization_attempt_id=auth-3f9a1c")]
    [InlineData("AuthorizationAttemptId=auth-0123abcd")]
    [InlineData("authorizationAttemptId=auth-0123abcd")]
    public void An_authorization_attempt_id_is_not_a_secret(string input)
    {
        Assert.Equal(input, SecretOutputRedactor.Redact(input));
        Assert.Equal(input, SecretOutputRedactor.RedactForLogging(input));
    }

    [Theory]
    [InlineData("password_attempt=hunter2", "hunter2")]
    [InlineData("api_key_attempt_id=hunter2", "hunter2")]
    [InlineData("TOKEN_ATTEMPTS=hunter2", "hunter2")]
    [InlineData("authorizationCode=abc123", "abc123")]
    [InlineData("authorization=Bearer-abc123", "Bearer-abc123")]
    [InlineData("token_id_secret=abc123", "abc123")]
    public void A_name_that_only_mentions_an_attempt_or_an_id_stays_a_secret(string input, string secret)
    {
        Assert.DoesNotContain(secret, SecretOutputRedactor.Redact(input), StringComparison.Ordinal);
    }

    // ── The mask map ──

    [Fact]
    public void The_mask_map_marks_only_the_masked_characters()
    {
        const string input = "Authorization: Bearer abc123 for user bob";

        var masks = SecretOutputRedactor.FindLogMasks(input);

        Assert.Equal(input.Length, masks.Length);
        var start = input.IndexOf("abc123", StringComparison.Ordinal);
        for (var i = 0; i < input.Length; i++)
            Assert.Equal(i >= start && i < start + "abc123".Length, masks[i]);
    }

    [Fact]
    public void The_mask_map_follows_each_rule_through_the_text_that_earlier_rules_changed()
    {
        // The JSON rule changes the spaces around ':' and the length of the text before the
        // header value, so a map that does not follow each rule marks the wrong characters.
        const string input = "{\"password\" :  \"pw\"} X-Api-Key:k3y9 then ?a=1&sig=s1g2 redis://u:p4ss@db API_KEY=\nv4lue";

        var masks = SecretOutputRedactor.FindLogMasks(input);

        foreach (var secret in new[] { "pw", "k3y9", "s1g2", "p4ss", "v4lue" })
        {
            var start = input.IndexOf(secret, StringComparison.Ordinal);
            Assert.All(Enumerable.Range(start, secret.Length), i => Assert.True(masks[i], $"'{input[i]}' of {secret}"));
        }

        // "@db" is after the password inside the URL credential match, so the map must keep it.
        foreach (var kept in new[] { "} X-Api-Key:", " then ?a=1&sig=", " redis://u:", "@db API_KEY=\n" })
        {
            var start = input.IndexOf(kept, StringComparison.Ordinal);
            Assert.All(Enumerable.Range(start, kept.Length), i => Assert.False(masks[i], $"'{input[i]}' of {kept}"));
        }
    }

    [Fact]
    public void The_mask_map_marks_nothing_in_text_without_a_secret_or_with_a_mask_already()
    {
        Assert.DoesNotContain(true, SecretOutputRedactor.FindLogMasks("max_tokens=4096 total_tokens=1234"));
        Assert.DoesNotContain(true, SecretOutputRedactor.FindLogMasks("api_key=" + Masked));
        Assert.Empty(SecretOutputRedactor.FindLogMasks(string.Empty));
    }
}

// -----------------------------------------------------------------------
// <copyright file="SecretOutputRedactorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Xunit;

namespace Netclaw.Security.Tests;

public sealed class SecretOutputRedactorTests
{
    // ── JSON secret key redaction ──

    [Theory]
    [InlineData("""{"apiKey": "sk-or-test-123", "safe": "ok"}""", "sk-or-test-123")]
    [InlineData("""{"client_secret": "super-secret-value-123"}""", "super-secret-value-123")]
    [InlineData("""{"signing_key": "hmac-sha256-key-abc"}""", "hmac-sha256-key-abc")]
    [InlineData("""{"credential": "some-cred-value"}""", "some-cred-value")]
    [InlineData("""{"access_token": "tok-abc-123"}""", "tok-abc-123")]
    [InlineData("""{"refresh_token": "rt-xyz-456"}""", "rt-xyz-456")]
    public void Redact_masks_json_secret_values(string input, string secretValue)
    {
        var redacted = SecretOutputRedactor.Redact(input);

        Assert.Contains("***REDACTED***", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(secretValue, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_preserves_safe_json_keys()
    {
        const string input = """{"apiKey": "sk-or-test-123", "safe": "ok"}""";

        var redacted = SecretOutputRedactor.Redact(input);

        Assert.Contains("\"safe\": \"ok\"", redacted, StringComparison.Ordinal);
    }

    // ── Environment variable redaction ──

    [Fact]
    public void Redact_masks_env_style_secrets()
    {
        const string input = "API_KEY=secret123\nNORMAL=value";

        var redacted = SecretOutputRedactor.Redact(input);

        Assert.Contains("API_KEY=***REDACTED***", redacted, StringComparison.Ordinal);
        Assert.Contains("NORMAL=value", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("secret123", redacted, StringComparison.Ordinal);
    }

    // A form-urlencoded OAuth token/DCR error body ("client_secret=...&grant_type=...") uses
    // compound keys whose secret-bearing fragment is not the first word. These must redact the
    // same way the JSON key form already does.
    [Theory]
    [InlineData("client_secret=super-secret-value-123&grant_type=refresh_token", "super-secret-value-123")]
    [InlineData("access_token=tok-abc-123&token_type=Bearer", "tok-abc-123")]
    [InlineData("refresh_token=rt-xyz-456", "rt-xyz-456")]
    public void Redact_masks_env_style_compound_key_secrets(string input, string secretValue)
    {
        var redacted = SecretOutputRedactor.Redact(input);

        Assert.Contains("***REDACTED***", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(secretValue, redacted, StringComparison.Ordinal);
    }

    // ── Authorization header redaction ──

    [Fact]
    public void Redact_masks_authorization_header()
    {
        const string input = "Authorization: Bearer abcdefghijklmnop";

        var redacted = SecretOutputRedactor.Redact(input);

        Assert.Equal("Authorization: Bearer ***REDACTED***", redacted);
    }

    // ── Credential-bearing URLs ──

    [Theory]
    [InlineData("https://hooks.slack.com/services/T000TEST/B000TEST/fakeWebhookToken")]
    [InlineData("""{"Url": "https://hooks.slack.com/services/T000TEST/B000TEST/fakeWebhookToken"}""")]
    [InlineData("Sending request to https://hooks.slack.com/services/T000TEST/B000TEST/fakeWebhookToken")]
    public void Redact_masks_slack_webhook_credentials(string input)
    {
        var redacted = SecretOutputRedactor.Redact(input);

        Assert.Contains("https://hooks.slack.com/services/***REDACTED***", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("T000TEST", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("B000TEST", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("fakeWebhookToken", redacted, StringComparison.Ordinal);
    }

    // ── Connection string redaction ──

    [Theory]
    [InlineData("Server=db.example.com;Database=mydb;User Id=admin;Password=s3cret!;", "s3cret!")]
    [InlineData("Server=localhost;Pwd=hunter2;Database=test;", "hunter2")]
    public void Redact_masks_connection_string_passwords(string input, string secretValue)
    {
        var redacted = SecretOutputRedactor.Redact(input);

        Assert.DoesNotContain(secretValue, redacted, StringComparison.Ordinal);
        Assert.Contains("***REDACTED***;", redacted, StringComparison.Ordinal);
    }

    // ── Provider-specific token redaction ──

    [Theory]
    [InlineData("Found key: AKIAIOSFODNN7EXAMPLE in config", "AKIAIOSFODNN7EXAMPLE")]
    [InlineData("sk-1234567890abcdef", "sk-1234567890abcdef")]
    [InlineData("xoxb-123456789-abcdefgh", "xoxb-123456789-abcdefgh")]
    [InlineData("ghp_ABCDEFghijklmnopqrstuvwx", "ghp_ABCDEFghijklmnopqrstuvwx")]
    public void Redact_masks_provider_tokens(string input, string secretValue)
    {
        var redacted = SecretOutputRedactor.Redact(input);

        Assert.DoesNotContain(secretValue, redacted, StringComparison.Ordinal);
        Assert.Contains("***REDACTED***", redacted, StringComparison.Ordinal);
    }

    // ── Known token formats ──
    // Tokens are assembled from a documented prefix plus filler so no real-looking credential
    // sits in the source. Each shape below has a published format, referenced beside its regex
    // in SecretOutputRedactor.

    private static string Pad(string prefix, int length, char fill = 'a') => prefix + new string(fill, length);

    public static TheoryData<string, string> KnownTokenFormats() => new()
    {
        { "GitHub personal access token", Pad("ghp_", 36) },
        { "GitHub OAuth token", Pad("gho_", 36) },
        { "GitHub user-to-server token", Pad("ghu_", 36) },
        { "GitHub server-to-server token", Pad("ghs_", 36) },
        { "GitHub refresh token", Pad("ghr_", 36) },
        { "GitHub fine-grained token", "github_pat_" + new string('1', 11) + "_" + new string('b', 59) },
        { "Slack bot token", "xoxb-1234567890-1234567890123-" + new string('c', 24) },
        { "Slack user token", "xoxp-1234567890-1234567890-1234567890-" + new string('d', 32) },
        { "Slack app token", "xapp-1-A0123456789-1234567890123-" + new string('e', 40) },
        { "Slack legacy token", "xoxa-2-1234567890-" + new string('f', 24) },
        { "AWS long-term access key id", "AKIA" + new string('A', 16) },
        { "AWS temporary access key id", "ASIA" + new string('B', 16) },
        { "Stripe live secret key", Pad("sk_live_", 24, 'G') },
        { "Stripe live restricted key", Pad("rk_live_", 24, 'H') },
        { "Stripe webhook signing secret", Pad("whsec_", 32, 'W') },
        { "Slack config token", "xoxe-1-" + new string('g', 40) },
        { "Google API key ending in dash", Pad("AIza", 34, 'x') + "-" },
        { "Google API key ending in underscore", Pad("AIza", 34, 'x') + "_" },
        { "Discord bot token HMAC ending in dash", "MTAxMDEwMTAxMDEwMTAxMDEw.GabcDE." + new string('d', 26) + "-" },
        { "Discord bot token starting with N", "NTAxMDEwMTAxMDEwMTAxMDEw.GabcDE." + new string('d', 27) },
        { "OpenAI key ending in dash", Pad("sk-", 30, 'k') + "-" },
        { "Anthropic API key", "sk-ant-api03-" + new string('Z', 40) },
        { "OpenAI classic key", Pad("sk-", 48, 'k') },
        { "OpenAI project key", "sk-proj-" + new string('k', 40) + "_" + new string('K', 20) },
        { "Google API key", Pad("AIza", 35, 'x') },
        { "npm access token", Pad("npm_", 36, 'n') },
        { "PyPI API token", Pad("pypi-AgEIcHlwaS5vcmc", 60, 'p') },
        { "Discord bot token", "MTAxMDEwMTAxMDEwMTAxMDEw." + "GabcDE." + new string('d', 27) },
    };

    [Theory]
    [MemberData(nameof(KnownTokenFormats))]
    public void Redact_masks_known_token_formats(string format, string token)
    {
        var redacted = SecretOutputRedactor.Redact($"value is {token}, kept");

        Assert.DoesNotContain(token, redacted, StringComparison.Ordinal);
        Assert.Equal("value is ***REDACTED***, kept", redacted);
        _ = format;
    }

    // ── Where a token ends ──
    // A format whose characters are letters and digits ends at \\b, as the released redactor did,
    // so "ghp_...-backup" is still masked. A format that may itself contain '-' or '_' ends where
    // its own character class stops. What follows the token decides the outcome; the table
    // below is that rule, written out per format.

    private readonly record struct Boundary(string Name, string Token, string ClassChars, bool VariableLength, bool WordBoundary);

    private static readonly Boundary[] BoundaryFormats =
    [
        new("github ghp", Pad("ghp_", 36), "alnum", true, true),
        new("github gho", Pad("gho_", 36), "alnum", true, true),
        new("github ghu", Pad("ghu_", 36), "alnum", true, true),
        new("github ghs", Pad("ghs_", 36), "alnum", true, true),
        new("github ghr", Pad("ghr_", 36), "alnum", true, true),
        new("aws AKIA", "AKIA" + new string('A', 16), "alnum", false, true),
        new("aws ASIA", "ASIA" + new string('B', 16), "alnum", false, true),
        new("npm", Pad("npm_", 36, 'n'), "alnum", false, true),
        new("stripe sk_live", Pad("sk_live_", 24, 'G'), "alnum", true, true),
        new("stripe rk_live", Pad("rk_live_", 24, 'H'), "alnum", true, true),
        new("stripe whsec", Pad("whsec_", 32, 'W'), "alnum", true, true),
        new("github_pat", "github_pat_" + new string('1', 11) + "_" + new string('b', 59), "alnum_", true, false),
        new("slack xoxb", "xoxb-1234567890-1234567890123-" + new string('c', 24), "alnum-", true, false),
        new("slack xapp", "xapp-1-A0123456789-1234567890123-" + new string('e', 40), "alnum-", true, false),
        new("openai sk-", Pad("sk-", 40, 'k'), "alnum_-", true, false),
        new("pypi", Pad("pypi-AgEIcHlwaS5vcmc", 60, 'p'), "alnum_-", true, false),
        new("google AIza", Pad("AIza", 35, 'x'), "alnum_-", false, false),
        new("discord (longest HMAC)", "MTAxMDEwMTAxMDEwMTAxMDEw.GabcDE." + new string('d', 38), "alnum_-", false, false),
    ];

    private static readonly string[] Followers =
        ["-x", "_x", ".x", "/x", "-", "_", "-backup", " old", ",", ")", "\n", "", new string('Z', 40)];

    public static TheoryData<string, string> BoundaryCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var f in BoundaryFormats)
        {
            foreach (var follower in Followers)
                data.Add(f.Name, follower);
        }

        return data;
    }

    // Mirrors the regex semantics. A follower's first character either continues the token (it is
    // in the format's class, or is a word character for a \\b format) or ends it. A continuing
    // character is swallowed when the format is variable length and the character is in its
    // class; otherwise the token is not a token any more and the text passes through.
    // Returns whether the token is masked and, when it is, the text left after the mask: a
    // character in the token's class is swallowed with it, anything else stays.
    private static (bool Masked, string Remainder) Expect(Boundary f, string follower)
    {
        if (follower.Length == 0)
            return (true, "");

        var c = follower[0];
        var inClass = char.IsAsciiLetterOrDigit(c) || (f.ClassChars.Contains('_') && c == '_') || (f.ClassChars.Contains('-') && c == '-');
        var continues = inClass || (f.WordBoundary && c == '_');
        if (!continues)
            return (true, follower);

        return f.VariableLength && inClass ? (true, "") : (false, follower);
    }

    [Theory]
    [MemberData(nameof(BoundaryCases))]
    public void Redact_ends_each_token_format_at_its_own_boundary(string format, string follower)
    {
        var f = BoundaryFormats.Single(x => x.Name == format);
        var input = "see " + f.Token + follower;

        var redacted = SecretOutputRedactor.Redact(input);

        var (masked, remainder) = Expect(f, follower);
        // The exact text pins where the mask ends: a \\b ending would leave the tail of a Slack token behind.
        Assert.Equal(masked ? "see ***REDACTED***" + remainder : input, redacted);
    }

    [Theory]
    [InlineData("ghp_", 36, "-backup")]
    [InlineData("gho_", 36, "-backup")]
    [InlineData("npm_", 36, "-old")]
    [InlineData("rk_live_", 24, "-rotated")]
    [InlineData("whsec_", 32, "-v1")]
    public void Redact_masks_a_token_followed_by_a_hyphen_suffix(string prefix, int length, string suffix)
    {
        var redacted = SecretOutputRedactor.Redact(Pad(prefix, length, 'q') + suffix);

        Assert.Equal("***REDACTED***" + suffix, redacted);
    }

    [Fact]
    public void Redact_masks_an_aws_key_id_followed_by_a_hyphen_suffix()
    {
        var redacted = SecretOutputRedactor.Redact("key AKIA" + new string('A', 16) + "-old rotated");

        Assert.Equal("key ***REDACTED***-old rotated", redacted);
    }

    // The token has to start at a word boundary: a longer identifier that merely ends in a
    // prefix is not a credential.
    [Theory]
    [InlineData("task-1234567890abcdef")]
    [InlineData("xghp_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("xAKIAAAAAAAAAAAAAAAAA")]
    [InlineData("nonpm_nnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnn")]
    public void Redact_requires_a_token_to_start_at_a_word_boundary(string input)
    {
        Assert.Equal(input, SecretOutputRedactor.Redact(input));
    }

    [Fact]
    public void Redact_masks_the_xoxp_part_of_a_rotating_slack_token()
    {
        var redacted = SecretOutputRedactor.Redact("xoxe.xoxp-1-" + new string('h', 40));

        Assert.DoesNotContain("xoxp-1-", redacted, StringComparison.Ordinal);
    }

    // Near misses: right prefix, wrong shape. These must pass through unchanged so a format
    // rule never eats ordinary text.
    [Theory]
    [InlineData("ghp_tooShort")]
    [InlineData("ghx_0123456789012345678901234567890123456")]
    [InlineData("github_pat_short")]
    [InlineData("xoxb-short")]
    [InlineData("xapp-1")]
    [InlineData("AKIAIOSFODNN7EXAMPL")]
    [InlineData("AKIAIOSFODNN7EXAMPLEX")]
    [InlineData("akiaiosfodnn7example")]
    [InlineData("sk_live_short")]
    [InlineData("sk-short")]
    [InlineData("task-1234567890abcdef")]
    [InlineData("AIzaShort")]
    [InlineData("AIzaxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")] // 34 after the prefix, one short
    [InlineData("AIzaxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")] // 36 after the prefix, one long
    [InlineData("whsec_short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAA.GabcDE.dddddddddddddddddddddddddddd")] // Discord shape, wrong first character
    [InlineData("npm_0123456789012345678901234567890123")]
    [InlineData("pypi-abc")]
    [InlineData("pypi-AgEIcHlwaS5vcmc")]
    [InlineData("Netclaw.Security.Tests.SecretOutputRedactorTests")]
    [InlineData("Microsoft.Extensions.Hosting.Abstractions.dll")]
    [InlineData("MTAxMDEwMTAxMDEwMTAxMDEw.GabcDEF.short")]
    public void Redact_leaves_near_miss_token_shapes_alone(string input)
    {
        Assert.Equal(input, SecretOutputRedactor.Redact(input));
    }

    // Stripe test-mode keys are deliberately left alone. The value is built at run time so the
    // source holds no string that secret scanners read as a key.
    [Fact]
    public void Redact_leaves_stripe_test_mode_keys_alone()
    {
        var input = Pad("sk_test_", 24, '7');

        Assert.Equal(input, SecretOutputRedactor.Redact(input));
    }

    // ── Secret variable names ──

    [Theory]
    [InlineData("DB_PASSWORD=hunter2", "hunter2")]
    [InlineData("AWS_SECRET_ACCESS_KEY=wJalrXUtnFEMI-K7MDENG-bPxRfiCYEXAMPLEKEY", "wJalrXUtnFEMI")]
    [InlineData("AWS_SESSION_TOKEN=FwoGZXIvYXdzEXAMPLEsession", "FwoGZXIvYXdz")]
    [InlineData("GITHUB_TOKEN=plainvalue", "plainvalue")]
    [InlineData("SLACK_BOT_TOKEN=plainvalue", "plainvalue")]
    [InlineData("STRIPE_API_KEY=plainvalue", "plainvalue")]
    [InlineData("MY_APIKEY=plainvalue", "plainvalue")]
    [InlineData("SERVICE_PRIVATE_KEY=plainvalue", "plainvalue")]
    [InlineData("GOOGLE_CREDENTIAL=plainvalue", "plainvalue")]
    [InlineData("db_passwd=plainvalue", "plainvalue")]
    [InlineData("db-password=plainvalue", "plainvalue")]
    [InlineData("export DB_PASSWORD=\"plainvalue\"", "plainvalue")]
    [InlineData("PASSWD=plainvalue", "plainvalue")]
    [InlineData("mysql --user=root --password=plainvalue", "plainvalue")]
    public void Redact_masks_env_secrets_with_prefixed_names(string input, string secretValue)
    {
        var redacted = SecretOutputRedactor.Redact(input);

        Assert.DoesNotContain(secretValue, redacted, StringComparison.Ordinal);
        Assert.Contains("=***REDACTED***", redacted, StringComparison.Ordinal);
    }

    // A trailing run of closers after the value of a prefixed name is given back, so code around
    // the assignment survives. A numeric value is still masked: PINs and numeric passwords are real.
    [Theory]
    [InlineData("connect(DB_PASSWORD=hunter2)", "connect(DB_PASSWORD=***REDACTED***)")]
    [InlineData("f(a, db_password=hunter2, b)", "f(a, db_password=***REDACTED***, b)")]
    [InlineData("[x, db_password=hunter2]", "[x, db_password=***REDACTED***]")]
    [InlineData("{db_password=hunter2}", "{db_password=***REDACTED***}")]
    [InlineData("DB_PASSWORD=123456", "DB_PASSWORD=***REDACTED***")]
    [InlineData("foo(auth_token=other_token)", "foo(auth_token=***REDACTED***)")]
    [InlineData("f(a, db_password=hunter2),", "f(a, db_password=***REDACTED***),")]
    [InlineData("x[db_password=hunter2]]", "x[db_password=***REDACTED***]]")]
    // A closer or comma inside the value is part of the secret: all of it is masked.
    [InlineData("MYSQL_ROOT_PASSWORD=p@ss,word123", "MYSQL_ROOT_PASSWORD=***REDACTED***")]
    [InlineData("DB_PASSWORD=Tr0ub4dor)&3xyz", "DB_PASSWORD=***REDACTED***")]
    [InlineData("DB_PASSWORD=a]b}c", "DB_PASSWORD=***REDACTED***")]
    [InlineData("(DB_PASSWORD=Tr0ub4dor)&3xyz)", "(DB_PASSWORD=***REDACTED***)")]
    public void Redact_masks_a_whole_prefixed_secret_value_and_gives_back_only_trailing_closers(string input, string expected)
    {
        Assert.Equal(expected, SecretOutputRedactor.Redact(input));
    }

    // The secret word has to start a segment. Names that bury it inside a camel-case or
    // run-together word are a known gap; guessing there would redact too much.
    [Theory]
    [InlineData("DbPassword=plainvalue")]
    [InlineData("MYTOKEN=plainvalue")]
    [InlineData("1token=plainvalue")]
    public void Redact_requires_the_secret_word_to_start_a_name_segment(string input)
    {
        Assert.Equal(input, SecretOutputRedactor.Redact(input));
    }

    // Ordinary settings whose names merely contain a secret word. Each passed through
    // unchanged before the prefixed-name rule and must keep doing so.
    [Theory]
    [InlineData("max_tokens=4096")]
    [InlineData("MAX_TOKENS=4096")]
    [InlineData("OPENAI_MAX_TOKENS=4096")]
    [InlineData("total_tokens=1234 prompt_tokens=1000 completion_tokens=234")]
    [InlineData("COMPACTION: before input_tokens=5 output_tokens=7")]
    [InlineData("HF_TOKENIZERS_PARALLELISM=false")]
    [InlineData("GOOGLE_APPLICATION_CREDENTIALS=/etc/gcp/sa.json")]
    [InlineData("AWS_SHARED_CREDENTIALS_FILE=/x")]
    [InlineData("NPM_TOKEN_FILE=/x")]
    [InlineData("VAULT_TOKEN_PATH=/x")]
    [InlineData("GITHUB_TOKEN_PERMISSIONS=read")]
    [InlineData("DB_PASSWORD_MIN_LENGTH=12")]
    [InlineData("DOCKER_PASSWORD_STDIN=1")]
    [InlineData("CSRF_TOKEN_HEADER=X-CSRF")]
    [InlineData("OAUTH_AUTHORIZATION_URL=https://idp/authorize")]
    [InlineData("JWT_SECRET_NAME=prod/jwt")]
    [InlineData("AWS_SECRET_ARN=arn:aws:x")]
    [InlineData("NETCLAW_API_KEY_ENV=OPENAI_API_KEY")]
    [InlineData("ASPNETCORE_TOKEN_LIFETIME=3600")]
    [InlineData("AWS_ACCESS_KEY_ID=ID_NOT_AN_AKIA_VALUE")]
    [InlineData("ctx.next_token=resp.next_token")]
    [InlineData("next_page_token=abc")]
    [InlineData("https://api.example/items?page_token=CAESBwoF&limit=10")]
    [InlineData("env: GITHUB_TOKEN=${{ secrets.GITHUB_TOKEN }}")]
    [InlineData("DB_PASSWORD=${DB_PASSWORD}")]
    [InlineData("API_TOKEN=$API_TOKEN")]
    [InlineData("DB_PASSWORD=%DB_PASSWORD%")]
    [InlineData("export NETCLAW_EVAL_API_KEY=\"$EVAL_API_KEY\"")]
    [InlineData("device_token=\"$(echo \"$resp\" | jq -r '.token')\"")]
    [InlineData("EVAL_PROVIDER_API_KEY=\"\"")]
    [InlineData("""{"next_page_token": "CAESBwoF", "nextPageToken": "abc"}""")]
    [InlineData("foo(auth_token=auth_token)")]
    [InlineData("build(x, db_password=db_password)")]
    [InlineData("is_token_valid=false")]
    [InlineData("is_secret_set=True")]
    [InlineData("num_password_attempts=3")]
    [InlineData("MAX_PASSWORD_ATTEMPTS=5")]
    public void Redact_keeps_ordinary_settings_that_only_contain_a_secret_word(string input)
    {
        Assert.Equal(input, SecretOutputRedactor.Redact(input));
    }

    // Everything the old name rule (keyword at the start of a name) redacted must still be
    // redacted, including its free-suffix and hyphen quirks.
    [Theory]
    [InlineData("--continuation-token=abc", "abc")]
    [InlineData("ACCESS_TOKEN_EXPIRE_MINUTES=30", "30")]
    [InlineData("TOKEN_FILE=/run/secrets/x", "/run/secrets/x")]
    [InlineData("TOKENIZER=bert", "bert")]
    [InlineData("SECRET_SANTA=bob", "bob")]
    [InlineData("PASSWORD_MIN_LENGTH=12", "12")]
    [InlineData("x.token=y.token", "y.token")]
    [InlineData("CREDENTIALS_FILE=/home/u/.aws/credentials", "/home/u/.aws/credentials")]
    [InlineData("token=${{ secrets.X }}", "${{")]
    [InlineData("password=$HOME_PW", "$HOME_PW")]
    public void Redact_still_masks_what_the_old_name_rule_masked(string input, string value)
    {
        var oldRule = new Regex(
            @"\b((?:api[_-]?key|token|secret|password|authorization|access[_-]?token|refresh[_-]?token|client[_-]?secret|signing[_-]?key|private[_-]?key|connection[_-]?string|credential)[A-Z0-9_-]*)=([^\s;]+)",
            RegexOptions.IgnoreCase);
        Assert.Matches(oldRule, input); // the case really is an old-rule case

        Assert.DoesNotContain(value, SecretOutputRedactor.Redact(input), StringComparison.Ordinal);
    }

    // Bounded name tails keep these linear. A quadratic scan took seconds per megabyte, and
    // the redactor runs several times over every tool output (model, session log, spill file).
    [Theory]
    [InlineData("token_")]
    [InlineData("api_key_")]
    [InlineData("secret_")]
    [InlineData("password_")]
    public void Redact_stays_linear_on_long_runs_of_a_secret_word(string unit)
    {
        var input = string.Concat(Enumerable.Repeat(unit, 4_000_000 / unit.Length));
        SecretOutputRedactor.Redact(input[..1000]);

        var sw = Stopwatch.StartNew();
        var redacted = SecretOutputRedactor.Redact(input);
        sw.Stop();

        Assert.Equal(input, redacted);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"Redact took {sw.Elapsed.TotalMilliseconds:F0} ms for 4 MB of '{unit}'");
    }

    [Fact]
    public void Redact_keeps_ordinary_environment_lines()
    {
        const string input = "PATH=/usr/local/bin:/usr/bin\nHOME=/home/netclaw\nKEYBOARD=us\nMONKEY=1\n"
            + "SORT_KEY=name\nPRIMARY_KEY=id\nSSH_KEY_PATH=/root/.ssh/id_rsa\nBUCKET=my-bucket\n"
            + "LANG=en_US.UTF-8\nPWD=/work\nDOTNET_gcServer=0";

        Assert.Equal(input, SecretOutputRedactor.Redact(input));
    }

    // ── Pass-through invariants ──
    // Redaction must never rewrite structured protocol traffic or identifiers the model has to
    // echo back exactly (the MCP incidents behind SuppressOutputRedaction on McpToolAdapter).

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":"7c9e6679-7425-40de-944b-e07fc1f90ae7","method":"tools/call","params":{"name":"search","arguments":{"query":"redaction","limit":10}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":42,"result":{"content":[{"type":"text","text":"ok"}],"isError":false}}""")]
    [InlineData("""{"jsonrpc":"2.0","method":"notifications/progress","params":{"progressToken":"op-17","progress":0.5}}""")]
    [InlineData("call_8Fv3kXq2Lm9PzRtYw1NbHc7D")]
    [InlineData("toolu_01A09q90qw90lq917835lq9k")]
    [InlineData("https://example.com/search?q=netclaw&page=2&sort=desc&utm_source=newsletter#results")]
    [InlineData("https://api.example.com/v1/items/7c9e6679-7425-40de-944b-e07fc1f90ae7?expand=owner&limit=50")]
    [InlineData("commit 9fceb02d0ae598e95dc970b74767f19372d61af8")]
    [InlineData("9fceb02d0ae598e95dc970b74767f19372d61af8 refs/heads/dev")]
    [InlineData("sha256:2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824")]
    [InlineData("7c9e6679-7425-40de-944b-e07fc1f90ae7")]
    [InlineData("data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==")]
    [InlineData("TWFuIGlzIGRpc3Rpbmd1aXNoZWQsIG5vdCBvbmx5IGJ5IGhpcyByZWFzb24sIGJ1dCBieSB0aGlzIHNpbmd1bGFyIHBhc3Npb24gZnJvbSBvdGhlciBhbmltYWxz")]
    [InlineData("-----BEGIN PUBLIC KEY-----\nMFwwDQYJKoZIhvcNAQEBBQADSwAwSAJBAKj34GkxFhD90vcNLYLInFEX6Ppy1tPf\n-----END PUBLIC KEY-----")]
    [InlineData("-----BEGIN CERTIFICATE-----\nMIIBszCCAV2gAwIBAgIUQ0tHcGVjdCBjZXJ0aWZpY2F0ZSBib2R5\n-----END CERTIFICATE-----")]
    public void Redact_passes_protocol_ids_urls_hashes_and_encoded_content_through_byte_identical(string input)
    {
        Assert.Equal(input, SecretOutputRedactor.Redact(input));
        Assert.False(SecretOutputRedactor.ContainsSecretLikeContent(input));
    }

    // ── JWT redaction ──

    [Fact]
    public void Redact_masks_jwt_token()
    {
        const string input = "token: eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dGVzdHNpZ25hdHVyZQ";

        var redacted = SecretOutputRedactor.Redact(input);

        Assert.DoesNotContain("eyJhbGciOiJSUzI1NiJ9", redacted, StringComparison.Ordinal);
        Assert.Contains("***REDACTED***", redacted, StringComparison.Ordinal);
    }

    // ── Private key block redaction ──

    [Fact]
    public void Redact_masks_private_key_blocks()
    {
        const string input = """
            -----BEGIN OPENSSH PRIVATE KEY-----
            abcdefghijklmnop
            -----END OPENSSH PRIVATE KEY-----
            """;

        Assert.True(SecretOutputRedactor.ContainsSecretLikeContent(input));
        Assert.Equal("***REDACTED***", SecretOutputRedactor.Redact(input));
    }

    // ── False positive guards ──

    [Theory]
    [InlineData("""{"name": "Aaron", "email": "test@example.com"}""")]
    [InlineData("the word eyJust is not a JWT")]
    [InlineData("NORMAL=value")]
    [InlineData("ls -la /tmp")]
    public void Redact_does_not_touch_safe_content(string input)
    {
        var redacted = SecretOutputRedactor.Redact(input);

        Assert.Equal(input, redacted);
    }

    // ── Exception redaction for logging ──

    [Fact]
    public void RedactForLogging_masks_secret_shaped_exception_text()
    {
        var exception = new HttpRequestException(
            HttpRequestError.Unknown,
            "invalid_client: client_secret=secret-value token=token-value",
            null,
            HttpStatusCode.BadRequest);

        var redacted = SecretOutputRedactor.RedactForLogging(exception);

        Assert.DoesNotContain("secret-value", redacted.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("token-value", redacted.ToString(), StringComparison.Ordinal);
        Assert.Contains("HttpRequestException", redacted.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RedactForLogging_keeps_the_original_instance_when_nothing_matches()
    {
        // The common case (network errors, cancellations, disposal failures) must keep its
        // native stack trace and type for diagnostics -- redaction only swaps in a summary
        // when secret-shaped content is actually detected.
        var exception = new HttpRequestException("Connection refused");

        var redacted = SecretOutputRedactor.RedactForLogging(exception);

        Assert.Same(exception, redacted);
    }

    [Fact]
    public void RedactForLogging_masks_slack_webhook_credentials()
    {
        const string url = "https://hooks.slack.com/services/T000TEST/B000TEST/fakeWebhookToken";
        var exception = new HttpRequestException($"Delivery to {url} failed.");

        var redacted = SecretOutputRedactor.RedactForLogging(exception);

        Assert.DoesNotContain("T000TEST", redacted.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("B000TEST", redacted.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("fakeWebhookToken", redacted.ToString(), StringComparison.Ordinal);
    }
}

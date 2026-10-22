// -----------------------------------------------------------------------
// <copyright file="SecretOutputRedactorCredentialShapeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Security.Tests;

/// <summary>
/// Secret shapes from issue #2595: URL credentials, option values, secret headers, name and
/// value pairs, and secret key names. Each shape has positive cases and negative cases.
/// Over-redaction has a cost. The model reads redacted tool results, the operator reads the
/// redacted approval display, and memory curation drops any text that
/// <see cref="SecretOutputRedactor.ContainsSecretLikeContent"/> flags.
/// </summary>
public sealed class SecretOutputRedactorCredentialShapeTests
{
    private const string Mask = "***REDACTED***";

    // The secrets are separated by '|'. The kept parts are separated by '|' too.
    // Each secret part must be gone, and each kept part must stay.
    private static void AssertMasked(string input, string secrets, string kept)
    {
        var redacted = SecretOutputRedactor.Redact(input);

        Assert.Contains(Mask, redacted, StringComparison.Ordinal);
        foreach (var secret in secrets.Split('|', StringSplitOptions.RemoveEmptyEntries))
            Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        foreach (var part in kept.Split('|', StringSplitOptions.RemoveEmptyEntries))
            Assert.Contains(part, redacted, StringComparison.Ordinal);
        Assert.True(SecretOutputRedactor.ContainsSecretLikeContent(input));
    }

    private static void AssertUnchanged(string input)
    {
        Assert.Equal(input, SecretOutputRedactor.Redact(input));
        Assert.False(SecretOutputRedactor.ContainsSecretLikeContent(input));
    }

    // ── URL credentials ──
    // The password in the userinfo part of a URL is a secret. The user name can stay.
    // The scheme, the host, the port, and the path must stay, so the model can still use the URL.

    [Theory]
    [InlineData("https://bob:hunter2@host/x", "hunter2", "https://|@host/x")]
    [InlineData("postgres://u:p%40ss@db:5432/app", "p%40ss", "postgres://|@db:5432/app")]
    [InlineData("git clone https://user:ghp_xxx@github.com/o/r", "ghp_xxx", "git clone https://|@github.com/o/r")]
    [InlineData("redis://:hunter2@cache:6379/0", "hunter2", "redis://|@cache:6379/0")]
    [InlineData("mongodb+srv://admin:s3cr3tPass@cluster0.example.net/app?retryWrites=true", "s3cr3tPass", "mongodb+srv://|@cluster0.example.net/app")]
    [InlineData("amqp://guest:rabbitPw9@rabbit:5672/vhost", "rabbitPw9", "amqp://|@rabbit:5672/vhost")]
    [InlineData("""{"url": "https://bob:hunter2@host/x"}""", "hunter2", "@host/x")]
    // RFC 3986 lets the password contain ':'. All of it is the secret.
    [InlineData("https://bob:pa55:w0rd@host/x", "pa55|w0rd", "@host/x")]
    public void Redact_masks_the_password_in_url_credentials(string input, string secrets, string kept)
        => AssertMasked(input, secrets, kept);

    // Each case has an '@' or a ':' near a URL, but no password in a userinfo part.
    [Theory]
    [InlineData("https://host/x")]
    [InlineData("https://bob@host/x")]
    [InlineData("mailto:bob@example.com")]
    [InlineData("meet at 12:30@site")]
    [InlineData("email:bob@example.com")]
    [InlineData("git@github.com:netclaw-dev/netclaw.git")]
    [InlineData("ssh://git@github.com:2222/o/r.git")]
    [InlineData("http://localhost:4873/@myorg/pkg")]
    [InlineData("https://host:8443/path@v2")]
    [InlineData("https://example.com/search?q=from:bob@example.com")]
    [InlineData("http://[::1]:8080/health")]
    [InlineData("docker://registry.example.com:5000/app@sha256:2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824")]
    public void Redact_leaves_urls_without_a_password_alone(string input)
        => AssertUnchanged(input);

    // ── Option values ──
    // A secret-named option followed by a value. The option name and the other arguments stay.

    [Theory]
    [InlineData("mysql -u root --password hunter2 -h db.example.com", "hunter2", "mysql -u root --password|-h db.example.com")]
    [InlineData("vault login --token s.abc123tok", "s.abc123tok", "vault login --token")]
    [InlineData("tool --api-key abc123key --region eu", "abc123key", "tool --api-key|--region eu")]
    [InlineData("az login --service-principal -u app-id --client-secret abc123sec --tenant t1", "abc123sec", "--client-secret|--tenant t1")]
    [InlineData("tool --access-token abc123acc", "abc123acc", "tool --access-token")]
    [InlineData("deploy --admin-password hunter2 --region eu", "hunter2", "deploy --admin-password|--region eu")]
    [InlineData("tool --password \"correct horse battery\" --verbose", "correct|horse|battery", "--password|--verbose")]
    public void Redact_masks_a_secret_option_value_after_a_space(string input, string secrets, string kept)
        => AssertMasked(input, secrets, kept);

    // The '=' form is already masked by the name rule. These cases keep that behavior.
    [Theory]
    [InlineData("mysql --password=hunter2", "hunter2", "mysql --")]
    [InlineData("tool --api-key=abc123key", "abc123key", "tool --")]
    [InlineData("tool --client-secret=abc123sec", "abc123sec", "tool --")]
    [InlineData("tool --token=abc123tok", "abc123tok", "tool --")]
    public void Redact_masks_a_secret_option_value_after_an_equals_sign(string input, string secrets, string kept)
        => AssertMasked(input, secrets, kept);

    // An option that names a file, a flag that takes no value, and a flag at the end of a line
    // have no secret value. '-p' is a port, a path flag, or a database name for many tools, so
    // the redactor cannot know when '-p' takes a password. Help text shows placeholders, not secrets.
    [Theory]
    [InlineData("tool --password-file /run/secrets/db")]
    [InlineData("tool --token-file /run/secrets/tok")]
    [InlineData("pg_dump --no-password mydb")]
    [InlineData("mysql -u root --password")]
    [InlineData("mysql -u root --password\nmysql> SHOW DATABASES;")]
    [InlineData("echo \"$PW\" | docker login --username bob --password-stdin registry.example.com")]
    [InlineData("docker run -p 8080:80 nginx")]
    [InlineData("ssh -p 22 admin@host")]
    [InlineData("mysql -u root -p mydb")]
    [InlineData("mkdir -p /var/lib/app")]
    [InlineData("  --password PASSWORD   The password for the user")]
    [InlineData("  --token <token>       An API token")]
    public void Redact_leaves_options_without_a_secret_value_alone(string input)
        => AssertUnchanged(input);

    // ── Headers ──
    // A header that carries a credential. The header name and the text after the header stay.

    [Theory]
    [InlineData("curl -H \"X-Api-Key: abc123key\" https://api.example.com/v1", "abc123key", "X-Api-Key|https://api.example.com/v1")]
    [InlineData("curl -H 'X-Api-Key:abc123key' https://api.example.com/v1", "abc123key", "X-Api-Key|https://api.example.com/v1")]
    [InlineData("> x-api-key: abc123key\n> accept: */*", "abc123key", "x-api-key|> accept: */*")]
    [InlineData("api-key: abc123key", "abc123key", "api-key")]
    [InlineData("X-Auth-Token: tok7f3a9c\nX-Request-Id: 42", "tok7f3a9c", "X-Auth-Token|X-Request-Id: 42")]
    [InlineData("X-Vault-Token: hvs.CAESIabc123", "hvs.CAESIabc123", "X-Vault-Token")]
    [InlineData("Authorization: Basic dXNlcjpwYXNz", "dXNlcjpwYXNz", "Authorization")]
    [InlineData("Authorization: token abc123def", "abc123def", "Authorization")]
    [InlineData("Proxy-Authorization: Basic dXNlcjpwYXNz", "dXNlcjpwYXNz", "Proxy-Authorization")]
    [InlineData("Cookie: session=abc123sess; theme=dark", "abc123sess", "Cookie")]
    [InlineData("< Set-Cookie: session=abc123sess; Path=/; HttpOnly", "abc123sess", "Set-Cookie")]
    [InlineData("""{"headers": {"X-Api-Key": "abc123key", "Accept": "application/json"}}""", "abc123key", "X-Api-Key|\"Accept\": \"application/json\"")]
    public void Redact_masks_secret_header_values(string input, string secrets, string kept)
        => AssertMasked(input, secrets, kept);

    // A header that names a credential, or that has a scheme but no credential, is not a secret.
    [Theory]
    [InlineData("X-Request-Id: 123")]
    [InlineData("Content-Type: application/json")]
    [InlineData("Accept: */*")]
    [InlineData("X-RateLimit-Remaining: 59")]
    [InlineData("WWW-Authenticate: Bearer realm=\"api\"")]
    [InlineData("Access-Control-Allow-Headers: Authorization, X-Api-Key, Content-Type")]
    [InlineData("Cookie policy: essential cookies only")]
    public void Redact_leaves_ordinary_headers_alone(string input)
        => AssertUnchanged(input);

    // ── Name and value pairs ──
    // HAR files and Postman collections keep a header or a variable as a name and a value.

    [Theory]
    [InlineData("""[{"name":"Authorization","value":"Bearer abc123tok"}]""", "abc123tok", "\"name\":\"Authorization\"")]
    [InlineData("""{"key":"api_key","value":"abc123key"}""", "abc123key", "\"key\":\"api_key\"")]
    [InlineData("""{ "name": "X-Api-Key", "value": "abc123key" }""", "abc123key", "\"name\": \"X-Api-Key\"")]
    [InlineData("""{"key":"password","value":"hunter2","type":"secret"}""", "hunter2", "\"key\":\"password\"|\"type\":\"secret\"")]
    public void Redact_masks_the_value_of_a_secret_name_value_pair(string input, string secrets, string kept)
        => AssertMasked(input, secrets, kept);

    [Theory]
    [InlineData("""{"name":"Content-Type","value":"text/plain"}""")]
    [InlineData("""[{"name":"Accept","value":"application/json"},{"name":"page","value":"2"}]""")]
    [InlineData("""{"name":"next_page_token","value":"CAESBwoF"}""")]
    public void Redact_leaves_ordinary_name_value_pairs_alone(string input)
        => AssertUnchanged(input);

    // ── Secret key names ──
    // IsSecretKey hides an argument value in the approval display. It also stops
    // "netclaw doctor --fix" from a delete of an unknown config property.

    [Theory]
    [InlineData("passwd")]
    [InlineData("db_passwd")]
    [InlineData("pwd")]
    [InlineData("Pwd")]
    [InlineData("admin_pwd")]
    [InlineData("passphrase")]
    [InlineData("ssh_passphrase")]
    [InlineData("cookie")]
    [InlineData("Cookie")]
    [InlineData("cookies")]
    [InlineData("session_cookie")]
    [InlineData("set-cookie")]
    [InlineData("Set-Cookie")]
    [InlineData("x-api-key")]
    public void IsSecretKey_matches_secret_key_names(string key)
        => Assert.True(SecretOutputRedactor.IsSecretKey(key), key);

    // These names start with a short secret word, but their values are not secrets.
    // A plain substring rule for "pwd" or "cookie" would hide them.
    [Theory]
    [InlineData("pwd_dir")]
    [InlineData("cookie_consent")]
    [InlineData("cookie_domain")]
    [InlineData("cookie_path")]
    [InlineData("cookie_jar")]
    public void IsSecretKey_leaves_names_that_only_start_with_a_short_secret_word(string key)
        => Assert.False(SecretOutputRedactor.IsSecretKey(key), key);

    // Redact and IsSecretKey share one vocabulary. A passphrase value in text is masked too.
    [Theory]
    [InlineData("""{"passphrase": "correct-horse-9"}""", "correct-horse-9")]
    [InlineData("GPG_PASSPHRASE=correct-horse-9", "correct-horse-9")]
    public void Redact_masks_passphrase_values(string input, string secret)
        => AssertMasked(input, secret, "");
}

/// <summary>
/// The patterns for issue #2595 must stay linear. Each input is adversarial for one pattern:
/// a long run of a prefix with no end. The work of a linear pattern for 4 MB is well below the
/// budget. A quadratic pattern needs minutes.
/// </summary>
[Collection(RedactorTimingCollection.Name)]
public sealed class SecretOutputRedactorCredentialShapeLinearTimeTests
{
    private const int AdversarialChars = 4 * 1024 * 1024;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(4);

    private static string Rep(string unit) => string.Concat(Enumerable.Repeat(unit, Math.Max(1, AdversarialChars / unit.Length)));

    private static IEnumerable<(string Name, Func<string> Build)> Shapes()
    {
        yield return ("url scheme and user with no '@'", () => Rep("https://a:"));
        yield return ("one scheme then user parts with no '@'", () => "https://" + Rep("a:"));
        yield return ("one scheme then a long user", () => "https://" + new string('a', AdversarialChars));
        yield return ("password option repeats", () => Rep("--password "));
        yield return ("password option then spaces", () => "--password" + new string(' ', AdversarialChars));
        yield return ("long option name", () => "--" + Rep("token-"));
        yield return ("api key header names", () => Rep("X-Api-Key:"));
        yield return ("api key header then spaces", () => "X-Api-Key: " + new string(' ', AdversarialChars));
        yield return ("cookie pairs", () => "Cookie: " + Rep("a=b; "));
        yield return ("basic auth then a long value", () => "Authorization: Basic " + new string('a', AdversarialChars));
        yield return ("name with no value", () => Rep("{\"name\":\"Authorization\","));
        yield return ("name then an open value", () => "{\"name\":\"Authorization\",\"value\":\"" + new string('a', AdversarialChars));
    }

    public static TheoryData<string> ShapeNames()
    {
        var data = new TheoryData<string>();
        foreach (var (name, _) in Shapes())
            data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(ShapeNames))]
    public void Redact_finishes_adversarial_credential_shapes_within_budget(string shape)
    {
        var input = Shapes().Single(s => s.Name == shape).Build();
        SecretOutputRedactor.Redact(input[..1024]); // regex construction is a one-off cost, not the subject here

        // A quadratic pattern cannot be interrupted, so run it on a background thread and stop waiting.
        var worker = new Thread(() => SecretOutputRedactor.Redact(input)) { IsBackground = true };
        worker.Start();
        var finished = worker.Join(Budget);

        Assert.True(finished, $"{shape}: {AdversarialChars / 1024 / 1024} MB did not finish within {Budget.TotalSeconds} s");
    }

    // A long run of unfinished URL credentials must not hide a real one after it.
    [Fact]
    public void Redact_masks_url_credentials_that_follow_a_run_of_unfinished_ones()
    {
        var input = string.Concat(Enumerable.Repeat("https://a:", 200)) + " https://bob:hunter2@host/x tail";

        var redacted = SecretOutputRedactor.Redact(input);

        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        Assert.EndsWith("@host/x tail", redacted, StringComparison.Ordinal);
    }
}
